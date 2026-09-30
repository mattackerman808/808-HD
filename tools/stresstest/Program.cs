// stresstest <sample.cu8> [rounds]        HdDecoder start/stop stress test (use-after-free regression)
// stresstest <sample.cu8> audio <out.wav>  AudioInjector test: SDR#-like 37.5 kHz / 256-frame pulls,
//                                          440 Hz tone as "analog", 2 s signal dropout in the middle.
using SDRSharp.HDRadio;

unsafe
{
    var raw = File.ReadAllBytes(args[0]);
    var iq = new float[raw.Length];
    for (int i = 0; i < raw.Length; i++) iq[i] = (raw[i] - 127.5f) / 127.5f;

    if (args.Length > 1 && args[1] == "audio")
        AudioTest(iq, args[2], flap: false);
    else if (args.Length > 1 && args[1] == "flap")
        AudioTest(iq, args[2], flap: true);
    else if (args.Length > 1 && args[1] == "align")
        AlignTest.Run(iq, args[2], fadeStart: args.Length > 3 ? double.Parse(args[3]) : 99, fadeEnd: args.Length > 4 ? double.Parse(args[4]) : 99);
    else
        StressTest(iq, args.Length > 1 ? int.Parse(args[1]) : 40);
}

static unsafe void StressTest(float[] iq, int rounds)
{
    int total = iq.Length / 2;
    var decoder = new HdDecoder(new AudioInjector());
    var rng = new Random(1);
    for (int round = 1; round <= rounds; round++)
    {
        decoder.SetTuning(97_300_000 + round, 97_300_000);
        decoder.Start();
        var stopAt = DateTime.UtcNow.AddMilliseconds(rng.Next(50, 1500));
        int pos = rng.Next(total / 2);
        fixed (float* p = iq)
        {
            while (DateTime.UtcNow < stopAt)
            {
                int len = Math.Min(65536, total - pos);
                if (len <= 0) { pos = 0; continue; }
                decoder.Enqueue(p + pos * 2, len, 1488375);
                pos += len;
            }
        }
        var s = decoder.Status;
        decoder.Stop();
        Console.WriteLine($"round {round,2}: stopped cleanly (synced={s.Synced})");
    }
    decoder.Dispose();
    Console.WriteLine("PASS: no crash");
}

static unsafe void AudioTest(float[] iq, string wavPath, bool flap)
{
    // flap: marginal signal - from 7 s on, dead for 0.6 s out of every 2 s (the capture is looped to 40 s).
    const double iqRate = 1488375, outRate = 37500;
    const int frames = 256;
    int total = iq.Length / 2;
    double dropStart = 8.0, dropEnd = 10.0;   // seconds of dead air (zeros) to force a fallback

    var injector = new AudioInjector { SampleRate = outRate };
    Native.EnsureResolver();
    Native.Callback cb = (e, _) =>
    {
        if (Native.EventType(e) == Native.EventAudio && Native.U32(e, 8) == 0)
            injector.Add((short*)Native.Ptr(e, 16), (int)Native.Size(e, 24), Native.U32(e, 32));
    };
    Native.nrsc5_open_pipe(out var st);
    Native.nrsc5_set_mode(st, Native.ModeFm);
    Native.nrsc5_set_callback(st, cb, IntPtr.Zero);
    Native.nrsc5_start(st);
    var chan = new Channelizer(iqRate, Native.SampleRateNativeFm);
    chan.SetOffset(0);

    var output = new List<float>();
    var buffer = new float[frames * 2];
    var chunk = new float[(int)(iqRate * frames / outRate + 2) * 2];
    float[] outBuf = null;
    double iqPos = 0, phase = 0, t = 0;
    bool wasHd = false;
    int transitions = 0, hdBuffers = 0, buffers = 0;
    double firstHd = -1, maxJump = 0, maxJumpAt = 0, maxJumpNearTransition = 0, lastTransition = -1;
    double hdSq = 0, anSq = 0; long hdN = 0, anN = 0;
    float prevL = 0;

    double duration = flap ? 40 : total / iqRate;
    while (t < duration)
    {
        // Feed the IQ that corresponds to one SDR# audio buffer (looping the capture).
        if ((int)iqPos + 2 >= total) iqPos = 0;
        double next = iqPos + iqRate * frames / outRate;
        int start = (int)iqPos, n = Math.Min((int)next, total) - start;
        bool dead = flap ? t >= 7 && t % 2 < 0.6 : t >= dropStart && t < dropEnd;
        for (int i = 0; i < n * 2; i++) chunk[i] = dead ? 0 : iq[start * 2 + i];
        fixed (float* c = chunk)
        {
            int m = chan.Process(c, n, ref outBuf);
            fixed (float* o = outBuf) Native.nrsc5_pipe_samples_cf32(st, o, (uint)m);
        }
        iqPos = next;

        // "Analog" audio: 440 Hz tone, RMS 0.15 (what the probe measured on your station).
        for (int i = 0; i < frames; i++)
        {
            float v = (float)(0.212 * Math.Sin(phase));
            phase += 2 * Math.PI * 440 / outRate;
            buffer[2 * i] = buffer[2 * i + 1] = v;
        }
        fixed (float* b = buffer) injector.Process(b, frames * 2);

        bool hd = injector.PlayingHd;
        if (hd != wasHd) { transitions++; lastTransition = t; Console.WriteLine($"  t={t,5:0.00}s  -> {(hd ? "HD" : "analog")}"); }
        if (hd && firstHd < 0) firstHd = t;
        wasHd = hd;
        buffers++;
        if (hd) hdBuffers++;
        for (int i = 0; i < frames; i++)
        {
            float l = buffer[2 * i];
            float jump = Math.Abs(l - prevL);
            if (jump > maxJump) { maxJump = jump; maxJumpAt = t + i / outRate; }
            // Clicks would show up right at a blend transition.
            if (lastTransition >= 0 && t - lastTransition < 0.2) maxJumpNearTransition = Math.Max(maxJumpNearTransition, jump);
            prevL = l;
            if (hd) { hdSq += l * l; hdN++; } else { anSq += l * l; anN++; }
        }
        output.AddRange(buffer);
        t += frames / outRate;
    }
    Native.nrsc5_close(st);
    GC.KeepAlive(cb);

    Console.WriteLine($"duration {t:0.0}s, HD first heard at {firstHd:0.00}s, HD {100.0 * hdBuffers / buffers:0}% of the time, {transitions} transitions");
    Console.WriteLine($"output RMS: HD {Math.Sqrt(hdSq / Math.Max(1, hdN)):0.000}, analog {Math.Sqrt(anSq / Math.Max(1, anN)):0.000} (analog tone RMS 0.150)");
    Console.WriteLine($"largest sample-to-sample step: {maxJump:0.000} at t={maxJumpAt:0.000}s; largest within 0.2 s of a transition: {maxJumpNearTransition:0.000}");
    WriteWav(wavPath, output, (int)outRate);
    Console.WriteLine($"wrote {wavPath}");
}

static void WriteWav(string path, List<float> stereo, int rate)
{
    using var w = new BinaryWriter(File.Create(path));
    int bytes = stereo.Count * 2;
    w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
    w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)16);
    w.Write("data"u8); w.Write(bytes);
    foreach (var v in stereo) w.Write((short)Math.Clamp(v * 32767, -32768, 32767));
}
