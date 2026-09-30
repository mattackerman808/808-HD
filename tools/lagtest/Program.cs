// lagtest <sample.cu8>
// FM-demodulates the analog audio in the capture, decodes the HD audio with nrsc5 (recording
// the IQ-time at which each HD audio frame is emitted), and cross-correlates the two to find
// how much later the same program content comes out of the HD path than out of analog.
using SDRSharp.HDRadio;

// |x| smoothed over 20 ms, resampled to outRate, mean removed.
static float[] Envelope(float[] x, double rate, double outRate)
{
    double mean = x.Average(v => (double)v);
    int step = (int)Math.Round(rate / outRate), win = (int)(rate * 0.02);
    var env = new float[x.Length / step];
    for (int i = 0; i < env.Length; i++)
    {
        double s = 0; int c = 0;
        for (int k = i * step - win / 2; k < i * step + win / 2; k++)
            if (k >= 0 && k < x.Length) { s += Math.Abs(x[k] - mean); c++; }
        env[i] = (float)(s / Math.Max(1, c));
    }
    double em = env.Average(v => (double)v);
    for (int i = 0; i < env.Length; i++) env[i] -= (float)em;
    return env;
}

unsafe
{
    const double iqRate = 1488375;
    const double lowRate = 2000;            // analysis rate
    var raw = File.ReadAllBytes(args[0]);
    int n = raw.Length / 2;

    // ---- analog: FM discriminator, then average down to lowRate ----
    int decim = (int)Math.Round(iqRate / lowRate);
    var analog = new List<float>();
    double acc = 0; int cnt = 0;
    float pi = 0, pq = 0;
    for (int i = 0; i < n; i++)
    {
        float I = (raw[2 * i] - 127.5f) / 127.5f, Q = (raw[2 * i + 1] - 127.5f) / 127.5f;
        // arg(x[n] * conj(x[n-1]))
        float re = I * pi + Q * pq, im = Q * pi - I * pq;
        acc += Math.Atan2(im, re);
        pi = I; pq = Q;
        if (++cnt == decim) { analog.Add((float)(acc / cnt)); acc = 0; cnt = 0; }
    }
    double analogRate = iqRate / decim;

    // ---- HD: decode, placing each audio sample at the IQ time its frame was emitted ----
    var hdAt = new List<(double t, short[] pcm)>();
    double iqTime = 0;
    Native.EnsureResolver();
    Native.Callback cb = (e, _) =>
    {
        if (Native.EventType(e) != Native.EventAudio || Native.U32(e, 8) != 0) return;
        if ((Native.U32(e, 32) & Native.AudioFlagUnavailable) != 0) return;
        int count = (int)Native.Size(e, 24);
        var pcm = new short[count];
        System.Runtime.InteropServices.Marshal.Copy(Native.Ptr(e, 16), pcm, 0, count);
        hdAt.Add((iqTime, pcm));
    };
    Native.nrsc5_open_pipe(out var st);
    Native.nrsc5_set_mode(st, Native.ModeFm);
    Native.nrsc5_set_callback(st, cb, IntPtr.Zero);
    Native.nrsc5_start(st);
    var chan = new Channelizer(iqRate, Native.SampleRateNativeFm);
    chan.SetOffset(0);
    float[] outBuf = null;
    const int block = 16384;
    var tmp = new float[block * 2];
    for (int pos = 0; pos < n; pos += block)
    {
        int len = Math.Min(block, n - pos);
        for (int i = 0; i < len * 2; i++) tmp[i] = (raw[pos * 2 + i] - 127.5f) / 127.5f;
        fixed (float* t = tmp)
        {
            int m = chan.Process(t, len, ref outBuf);
            fixed (float* o = outBuf) Native.nrsc5_pipe_samples_cf32(st, o, (uint)m);
        }
        iqTime = (pos + len) / iqRate;
    }
    Native.nrsc5_close(st);
    GC.KeepAlive(cb);

    // HD as a continuous stream starting when its first frame was emitted (played immediately).
    double hdStart = hdAt[0].t;
    var hdMono = hdAt.SelectMany(f => Enumerable.Range(0, f.pcm.Length / 2).Select(k => (f.pcm[2 * k] + f.pcm[2 * k + 1]) / 65536f)).ToArray();
    int hdDecim = (int)Math.Round(44100 / analogRate);
    var hd = new float[hdMono.Length / hdDecim];
    for (int i = 0; i < hd.Length; i++) { double s = 0; for (int k = 0; k < hdDecim; k++) s += hdMono[i * hdDecim + k]; hd[i] = (float)(s / hdDecim); }
    double hdRate = 44100.0 / hdDecim;
    Console.WriteLine($"HD audio first emitted at IQ time {hdStart:0.00}s; {hdMono.Length / 44100.0:0.0}s of HD audio; frames arrive in bursts of {hdAt[0].pcm.Length / 2} samples");

    // Compare loudness envelopes (robust to FM pre-emphasis, filtering and phase differences).
    const double envRate = 100;
    float[] aEnv = Envelope(analog.ToArray(), analogRate, envRate);
    float[] hEnv = Envelope(hd, hdRate, envRate);
    Console.WriteLine($"analog {aEnv.Length / envRate:0.0}s, HD {hEnv.Length / envRate:0.0}s of envelope");

    // lag > 0: HD plays the same content `lag` seconds later than analog.
    var scores = new List<(double lag, double c)>();
    for (int L = (int)(-3 * envRate); L <= (int)(14 * envRate); L++)
    {
        double lag = L / envRate;
        double sab = 0, saa = 0, sbb = 0; int used = 0;
        for (int i = 0; i < hEnv.Length; i++)
        {
            int ai = (int)Math.Round((hdStart + i / envRate - lag) * envRate);
            if (ai < 0 || ai >= aEnv.Length) continue;
            sab += hEnv[i] * aEnv[ai]; saa += aEnv[ai] * aEnv[ai]; sbb += hEnv[i] * hEnv[i]; used++;
        }
        if (used < envRate * 3) continue;
        scores.Add((lag, sab / Math.Sqrt(saa * sbb + 1e-12)));
    }
    foreach (var s in scores.OrderByDescending(s => s.c).Take(5))
        Console.WriteLine($"  lag {s.lag,7:0.00} s  correlation {s.c:0.00}  (overlap {Math.Min(hEnv.Length / envRate, (aEnv.Length / envRate) - (hdStart - s.lag)):0.0}s)");
}
