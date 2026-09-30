// Usage: pipetest <sample.cu8> [deviceRate] [offsetHz]
//
// Reads nrsc5's cu8 sample capture (1488375 S/s, station at 0 Hz). To mimic SDR#,
// it first moves the station to +offsetHz and resamples to deviceRate (as if the
// dongle were tuned off-center at that rate), then runs the plugin's Channelizer
// and libnrsc5 exactly as the plugin does, and reports what nrsc5 decoded.
using System.Runtime.InteropServices;
using SDRSharp.HDRadio;

unsafe
{
    string path = args[0];
    double deviceRate = args.Length > 1 ? double.Parse(args[1]) : 1488375;
    double offset = args.Length > 2 ? double.Parse(args[2]) : 0;
    const double captureRate = 1488375;

    var raw = File.ReadAllBytes(path);
    int n = raw.Length / 2;
    var iq = new float[n * 2];
    for (int i = 0; i < raw.Length; i++) iq[i] = (raw[i] - 127.5f) / 127.5f;
    Console.WriteLine($"capture: {n} samples, {n / captureRate:0.00} s");

    // Simulate the SDR# device stream: shift up by offset, then resample to deviceRate.
    var shifted = Shift(iq, offset, captureRate);
    var device = deviceRate == captureRate ? shifted : Resample(shifted, captureRate, deviceRate);
    Console.WriteLine($"device stream: {device.Length / 2} samples @ {deviceRate} S/s, station at {offset:+0;-0} Hz");

    // Plugin path.
    Native.EnsureResolver();
    Console.WriteLine("libnrsc5 " + Native.Version());
    int sync = 0, audioFrames = 0, audioOk = 0, id3 = 0, lots = 0;
    float merL = 0, merU = 0;
    var names = new HashSet<string>();
    var programs = new SortedSet<uint>();
    Native.Callback cb = (e, _) =>
    {
        switch (Native.EventType(e))
        {
            case Native.EventSync: sync++; break;
            case Native.EventMer: merL = Native.F32(e, 8); merU = Native.F32(e, 12); break;
            case Native.EventAudio:
                programs.Add(Native.U32(e, 8));
                if (Native.U32(e, 8) == 0)
                {
                    audioFrames++;
                    if ((Native.U32(e, 32) & Native.AudioFlagUnavailable) == 0 && Native.Size(e, 24) == 4096) audioOk++;
                }
                break;
            case Native.EventId3:
                id3++;
                if (id3 <= 3) Console.WriteLine($"  ID3 prog {Native.U32(e, 8)}: {Native.Str(e, 16)} / {Native.Str(e, 24)}");
                break;
            case Native.EventLot:
                lots++;
                if (lots <= 5) Console.WriteLine($"  LOT {Native.U32(e, 12)} {Native.Str(e, 24)} {Native.U32(e, 16)} bytes");
                break;
            case Native.EventStationName: names.Add(Native.Str(e, 8)); break;
        }
    };
    Native.nrsc5_open_pipe(out var st);
    Native.nrsc5_set_mode(st, Native.ModeFm);
    Native.nrsc5_set_callback(st, cb, IntPtr.Zero);
    Native.nrsc5_start(st);

    var chan = new Channelizer(deviceRate, Native.SampleRateNativeFm);
    chan.SetOffset(offset);
    float[] outBuf = null;
    const int block = 65536;   // similar to SDR# buffer sizes
    var sw = System.Diagnostics.Stopwatch.StartNew();
    long chanTicks = 0;
    fixed (float* src = device)
    {
        for (int pos = 0; pos < device.Length / 2; pos += block)
        {
            int len = Math.Min(block, device.Length / 2 - pos);
            long t0 = sw.ElapsedTicks;
            int m = chan.Process(src + pos * 2, len, ref outBuf);
            chanTicks += sw.ElapsedTicks - t0;
            fixed (float* o = outBuf) Native.nrsc5_pipe_samples_cf32(st, o, (uint)m);
        }
    }
    sw.Stop();
    Native.nrsc5_close(st);
    GC.KeepAlive(cb);

    double secs = device.Length / 2 / deviceRate;
    Console.WriteLine($"sync events: {sync}, MER {merL:0.0}/{merU:0.0} dB");
    Console.WriteLine($"programs: {string.Join(",", programs.Select(p => "HD" + (p + 1)))}");
    Console.WriteLine($"HD1 audio frames: {audioFrames} ({audioOk} good) = {audioOk * 2048 / 44100.0:0.0} s of audio");
    Console.WriteLine($"station: {string.Join(" | ", names)}  ID3: {id3}  LOT files: {lots}");
    Console.WriteLine($"cpu: channelizer {chanTicks * 100.0 / System.Diagnostics.Stopwatch.Frequency / secs:0.0}% of realtime, total {sw.Elapsed.TotalSeconds * 100 / secs:0.0}% of realtime");
}

static float[] Shift(float[] x, double hz, double rate)
{
    if (hz == 0) return x;
    var y = new float[x.Length];
    for (int i = 0; i < x.Length / 2; i++)
    {
        double a = 2 * Math.PI * hz * i / rate;
        float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
        y[2 * i] = x[2 * i] * c - x[2 * i + 1] * s;
        y[2 * i + 1] = x[2 * i] * s + x[2 * i + 1] * c;
    }
    return y;
}

// Test-only upsampler: slow but accurate windowed-sinc interpolation.
static float[] Resample(float[] x, double inRate, double outRate)
{
    int n = x.Length / 2;
    int m = (int)(n * outRate / inRate);
    var y = new float[m * 2];
    const int hw = 24;
    double fc = 0.47;  // cycles per input sample
    Parallel.For(0, m, k =>
    {
        double t = k * inRate / outRate;
        int c = (int)t;
        double sr = 0, si = 0;
        for (int j = c - hw + 1; j <= c + hw; j++)
        {
            if (j < 0 || j >= n) continue;
            double d = j - t;
            double sinc = d == 0 ? 1 : Math.Sin(2 * Math.PI * fc * d) / (2 * Math.PI * fc * d);
            double w = 0.5 + 0.5 * Math.Cos(Math.PI * d / hw);
            double h = 2 * fc * sinc * w;
            sr += x[2 * j] * h;
            si += x[2 * j + 1] * h;
        }
        y[2 * k] = (float)sr;
        y[2 * k + 1] = (float)si;
    });
    return y;
}
