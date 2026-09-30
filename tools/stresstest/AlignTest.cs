using SDRSharp.HDRadio;

/// <summary>
/// stresstest <sample.cu8> align <out.wav>
/// Runs the capture through nrsc5 + AudioInjector with the capture's own FM-demodulated analog
/// audio as SDR#'s "analog" input (37.5 kHz, 256-frame buffers). Marks a stretch of HD frames
/// unavailable to simulate a fade. Reports the measured HD lead, the residual misalignment
/// while HD plays, and the blend transitions; writes the output as a WAV.
/// </summary>
static unsafe class AlignTest
{
    const double IqRate = 1488375, OutRate = 37500;
    const int Frames = 256;

    public static void Run(float[] iq, string wavPath, double fadeStart, double fadeEnd)
    {
        var analog = FmDemod(iq);
        Console.WriteLine($"analog demodulated: {analog.Length / OutRate:0.0}s");

        var inj = new AudioInjector { SampleRate = OutRate };
        double iqTime = 0;
        Native.EnsureResolver();
        Native.Callback cb = (e, _) =>
        {
            uint ev = Native.EventType(e);
            if (ev == Native.EventLostSync) { inj.TimelineLost(); Console.WriteLine($"  t={iqTime,5:0.00}s  nrsc5 lost sync"); }
            if (ev != Native.EventAudio || Native.U32(e, 8) != 0) return;
            uint flags = Native.U32(e, 32);
            // Simulated fade: HD frames emitted in this window are "unavailable".
            if (iqTime >= fadeStart && iqTime < fadeEnd) flags |= Native.AudioFlagUnavailable;
            inj.Add((short*)Native.Ptr(e, 16), (int)Native.Size(e, 24), flags);
        };
        Native.nrsc5_open_pipe(out var st);
        Native.nrsc5_set_mode(st, Native.ModeFm);
        Native.nrsc5_set_callback(st, cb, IntPtr.Zero);
        Native.nrsc5_start(st);
        var chan = new Channelizer(IqRate, Native.SampleRateNativeFm);
        chan.SetOffset(0);

        int total = iq.Length / 2;
        var chunk = new float[(int)(IqRate * Frames / OutRate + 2) * 2];
        var buf = new float[Frames * 2];
        float[] outBuf = null;
        var output = new List<float>();
        var hdMask = new List<bool>();
        double iqPos = 0, t = 0;
        bool wasHd = false, reportedAlign = false;
        int ai = 0;

        while ((int)iqPos + 2 < total && ai + Frames <= analog.Length)
        {
            double next = iqPos + IqRate * Frames / OutRate;
            int start = (int)iqPos, n = Math.Min((int)next, total) - start;
            Array.Copy(iq, start * 2, chunk, 0, n * 2);
            fixed (float* c = chunk)
            {
                int m = chan.Process(c, n, ref outBuf);
                fixed (float* o = outBuf) Native.nrsc5_pipe_samples_cf32(st, o, (uint)m);
            }
            iqPos = next;
            iqTime = iqPos / IqRate;

            for (int i = 0; i < Frames; i++) buf[2 * i] = buf[2 * i + 1] = analog[ai + i];
            fixed (float* b = buf) inj.Process(b, Frames * 2);

            bool hd = inj.PlayingHd;
            if (hd != wasHd) Console.WriteLine($"  t={t,5:0.00}s  -> {(hd ? "HD" : "analog")}");
            wasHd = hd;
            if (!reportedAlign && inj.Aligned)
            {
                reportedAlign = true;
                Console.WriteLine($"  t={t,5:0.00}s  aligned: HD runs {inj.HdLeadSeconds:0.000}s ahead of analog (score {inj.AlignScore:0.00})");
            }
            output.AddRange(buf);
            for (int i = 0; i < Frames; i++) hdMask.Add(hd);
            ai += Frames;
            t += Frames / OutRate;
        }
        Native.nrsc5_close(st);
        GC.KeepAlive(cb);

        // Residual misalignment while HD is fully playing: envelope-correlate output vs analog.
        var outMono = new float[hdMask.Count];
        for (int i = 0; i < outMono.Length; i++) outMono[i] = output[2 * i];
        ReportResidual(outMono, analog, hdMask);

        Wav.Write(wavPath, output, (int)OutRate);
        Console.WriteLine($"wrote {wavPath}");
    }

    static void ReportResidual(float[] outMono, float[] analog, List<bool> hdMask)
    {
        // Longest HD stretch.
        int bestStart = 0, bestLen = 0;
        for (int i = 0; i < hdMask.Count;)
        {
            if (!hdMask[i]) { i++; continue; }
            int j = i;
            while (j < hdMask.Count && hdMask[j]) j++;
            if (j - i > bestLen) { bestLen = j - i; bestStart = i; }
            i = j;
        }
        if (bestLen < OutRate * 1.5) { Console.WriteLine("HD played too briefly to measure alignment"); return; }
        int skip = (int)(OutRate * 0.2);   // ignore the fade-in
        int s0 = bestStart + skip, len = bestLen - 2 * skip;
        const int env = 75;                // 2 ms envelope buckets
        float[] E(float[] x, int from) { var e = new float[len / env]; for (int b = 0; b < e.Length; b++) { double a = 0; for (int k = 0; k < env; k++) { int idx = from + b * env + k; if (idx >= 0 && idx < x.Length) a += Math.Abs(x[idx]); } e[b] = (float)a; } return e; }
        var o = E(outMono, s0);
        double best = -2; int bestLag = 0;
        for (int lagMs = -600; lagMs <= 600; lagMs += 2)
        {
            var a = E(analog, s0 + (int)(lagMs / 1000.0 * OutRate));
            double c = Corr(o, a);
            if (c > best) { best = c; bestLag = lagMs; }
        }
        Console.WriteLine($"HD played {bestLen / OutRate:0.0}s continuously; HD vs analog residual offset: {bestLag} ms (correlation {best:0.00})");
    }

    static double Corr(float[] x, float[] y)
    {
        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0; int n = Math.Min(x.Length, y.Length);
        for (int i = 0; i < n; i++) { sx += x[i]; sy += y[i]; sxx += x[i] * x[i]; syy += y[i] * y[i]; sxy += x[i] * y[i]; }
        double cov = sxy - sx * sy / n, vx = sxx - sx * sx / n, vy = syy - sy * sy / n;
        return cov / Math.Sqrt(vx * vy + 1e-12);
    }

    /// <summary>Mono FM demodulation to 37.5 kHz: discriminator, /8 boxcar, 15 kHz FIR, resample, 75 us de-emphasis.</summary>
    static float[] FmDemod(float[] iq)
    {
        int n = iq.Length / 2;
        const int d1 = 8;
        double r1 = IqRate / d1;
        var s1 = new float[n / d1];
        float pi = 0, pq = 0; double acc = 0; int cnt = 0, k = 0;
        for (int i = 0; i < n && k < s1.Length; i++)
        {
            float I = iq[2 * i], Q = iq[2 * i + 1];
            acc += Math.Atan2(Q * pi - I * pq, I * pi + Q * pq);
            pi = I; pq = Q;
            if (++cnt == d1) { s1[k++] = (float)(acc / d1); acc = 0; cnt = 0; }
        }
        // 15 kHz low-pass at r1 (~186 kHz), Blackman-windowed sinc.
        const int taps = 95;
        var h = new double[taps];
        double fc = 15000 / r1, sum = 0;
        for (int i = 0; i < taps; i++)
        {
            double x = i - (taps - 1) / 2.0;
            double sinc = x == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * x) / (Math.PI * x);
            double w = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (taps - 1)) + 0.08 * Math.Cos(4 * Math.PI * i / (taps - 1));
            h[i] = sinc * w; sum += h[i];
        }
        var s2 = new float[s1.Length];
        Parallel.For(0, s1.Length, i =>
        {
            double y = 0;
            for (int j = 0; j < taps; j++) { int idx = i - j + taps / 2; if (idx >= 0 && idx < s1.Length) y += s1[idx] * h[j]; }
            s2[i] = (float)(y / sum);
        });
        // Resample to 37.5 kHz (linear; signal is already band-limited), de-emphasis, scale.
        int outN = (int)(s2.Length * OutRate / r1);
        var o = new float[outN];
        double alpha = 1 - Math.Exp(-1 / (75e-6 * OutRate)), yPrev = 0;
        for (int i = 0; i < outN; i++)
        {
            double pos = i * r1 / OutRate; int p = (int)pos; double f = pos - p;
            double v = p + 1 < s2.Length ? s2[p] * (1 - f) + s2[p + 1] * f : 0;
            yPrev += alpha * (v - yPrev);
            o[i] = (float)(yPrev * 1.5);
        }
        return o;
    }
}

static class Wav
{
    public static void Write(string path, List<float> stereo, int rate)
    {
        using var w = new BinaryWriter(File.Create(path));
        int bytes = stereo.Count * 2;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)16);
        w.Write("data"u8); w.Write(bytes);
        foreach (var v in stereo) w.Write((short)Math.Clamp(v * 32767, -32768, 32767));
    }
}
