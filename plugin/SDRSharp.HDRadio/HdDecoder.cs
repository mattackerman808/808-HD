using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace SDRSharp.HDRadio
{
    /// <summary>Snapshot of everything the UI shows. Replaced wholesale under a lock.</summary>
    internal sealed class HdStatus
    {
        public bool Synced;
        public float MerLower, MerUpper, Ber;
        public string StationName, Slogan, Message, Alert;
        public string Title, Artist, Album;
        public byte[] AlbumArt;
        public byte[] StationLogo;
        public byte[] WeatherMap;
        public DateTime WeatherTime;
        public byte[][] TrafficTiles = new byte[9][];   // row-major 3x3
        public DateTime TrafficTime;
        public SortedDictionary<uint, string> Programs = new SortedDictionary<uint, string>();
        public DateTime LastAudio = DateTime.MinValue;
        public int FilesReceived;
        public string LastFile;
        public string Error;
        public double InputRate;

        public HdStatus Clone()
        {
            var c = (HdStatus)MemberwiseClone();
            c.Programs = new SortedDictionary<uint, string>(Programs);
            return c;
        }
    }

    /// <summary>
    /// Owns the libnrsc5 session and a worker thread. SDR#'s DSP thread hands IQ blocks
    /// to <see cref="Enqueue"/>; the worker mixes/resamples them and pipes them into
    /// nrsc5, whose callbacks (on the same worker thread) update <see cref="HdStatus"/>
    /// and push audio to the output.
    /// </summary>
    internal sealed unsafe class HdDecoder : IDisposable
    {
        private sealed class Block
        {
            public float[] Data;
            public int Length;      // complex samples
            public double Rate;
        }

        private readonly BlockingCollection<Block> _queue = new BlockingCollection<Block>(128);
        private readonly ConcurrentBag<Block> _pool = new ConcurrentBag<Block>();
        private readonly AudioInjector _audio;
        private readonly Native.Callback _callback;   // kept alive while nrsc5 holds it
        private readonly object _lock = new object();
        private readonly Dictionary<uint, byte[]> _lotImages = new Dictionary<uint, byte[]>();
        // Latest album art / logo per audio program, keyed via the SIG service that carried it.
        private readonly ConcurrentDictionary<uint, byte[]> _programArt = new ConcurrentDictionary<uint, byte[]>();
        private readonly ConcurrentDictionary<uint, byte[]> _programLogo = new ConcurrentDictionary<uint, byte[]>();

        private Thread _thread;
        private volatile bool _running;
        private IntPtr _nrsc5;
        private Channelizer _chan;
        private float[] _out;

        private HdStatus _status = new HdStatus();
        private volatile uint _program;
        private long _tunedHz;
        private volatile int _offsetHz;
        private long _sessionFrequency = -1;
        private int _pendingArtLot = -1;
        private int _dropped;

        public HdDecoder(AudioInjector audio)
        {
            _audio = audio;
            _callback = OnEvent;
        }

        public uint Program
        {
            get => _program;
            set
            {
                if (_program == value) return;
                _program = value;
                _audio.Program = (int)value;
                _audio.Clear();
                _pendingArtLot = -1;
                _programArt.TryGetValue(value, out var art);
                _programLogo.TryGetValue(value, out var logo);
                Update(s =>
                {
                    s.Title = s.Artist = s.Album = null;
                    s.AlbumArt = art;
                    s.StationLogo = logo ?? s.StationLogo;
                });
            }
        }

        public int DroppedBlocks => _dropped;

        public HdStatus Status
        {
            get { lock (_lock) return _status; }
        }

        /// <summary>Called from the UI thread with SDR#'s current VFO and center frequencies.</summary>
        public void SetTuning(long vfoHz, long centerHz)
        {
            Interlocked.Exchange(ref _tunedHz, vfoHz);
            _offsetHz = (int)(vfoHz - centerHz);
        }

        public void Start()
        {
            if (_running) return;
            if (_thread != null && !_thread.Join(10000)) return;   // previous worker still exiting
            while (_queue.TryTake(out var stale)) if (stale != null) _pool.Add(stale);
            _running = true;
            _thread = new Thread(Run) { IsBackground = true, Name = "HD Radio decoder", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        public void Stop()
        {
            if (_thread == null) return;
            _running = false;
            // Discard the backlog so the worker exits after at most the block it is on,
            // then wake it. The worker closes the nrsc5 session itself: closing it from
            // this thread while a pipe call is in flight is a use-after-free.
            while (_queue.TryTake(out var b)) if (b != null) _pool.Add(b);
            _queue.Add(null);
            if (_thread.Join(10000))
                _thread = null;
            _audio.Clear();
            lock (_lock) _status = new HdStatus();
        }

        /// <summary>Called on SDR#'s DSP thread: copy and hand off, never block.</summary>
        public void Enqueue(float* iq, int length, double sampleRate)
        {
            if (!_running) return;
            if (!_pool.TryTake(out var b)) b = new Block();
            if (b.Data == null || b.Data.Length < length * 2) b.Data = new float[length * 2];
            fixed (float* dst = b.Data)
                Buffer.MemoryCopy(iq, dst, b.Data.Length * sizeof(float), length * 2 * sizeof(float));
            b.Length = length;
            b.Rate = sampleRate;
            if (!_queue.TryAdd(b))
            {
                _dropped++;
                _pool.Add(b);
            }
        }

        private void Run()
        {
            try
            {
                Native.EnsureResolver();
                while (_running)
                {
                    var b = _queue.Take();
                    if (b == null || !_running) break;
                    try { ProcessBlock(b); }
                    finally { _pool.Add(b); }
                }
            }
            catch (Exception ex)
            {
                Update(s => s.Error = ex.Message);
                _running = false;
            }
            finally
            {
                // Only this thread ever touches the nrsc5 session.
                CloseSession();
                _chan = null;
                _sessionFrequency = -1;
            }
        }

        private void ProcessBlock(Block b)
        {
            long tuned = Interlocked.Read(ref _tunedHz);

            // New station or new device rate: start a fresh nrsc5 session.
            if (_chan == null || _chan.InputRate != b.Rate)
            {
                _chan = new Channelizer(b.Rate, Native.SampleRateNativeFm);
                lock (_lock) _status.InputRate = b.Rate;
                _sessionFrequency = -1;
            }
            if (tuned != _sessionFrequency)
            {
                OpenSession();
                _sessionFrequency = tuned;
            }

            _chan.SetOffset(_offsetHz);
            int n;
            fixed (float* src = b.Data)
                n = _chan.Process(src, b.Length, ref _out);
            if (n > 0)
                fixed (float* dst = _out)
                    Native.nrsc5_pipe_samples_cf32(_nrsc5, dst, (uint)n);
        }

        private void OpenSession()
        {
            CloseSession();
            if (Native.nrsc5_open_pipe(out _nrsc5) != 0 || _nrsc5 == IntPtr.Zero)
                throw new InvalidOperationException("nrsc5_open_pipe failed");
            Native.nrsc5_set_mode(_nrsc5, Native.ModeFm);
            Native.nrsc5_set_callback(_nrsc5, _callback, IntPtr.Zero);
            Native.nrsc5_start(_nrsc5);

            _audio.Clear();
            _lotImages.Clear();
            _programArt.Clear();
            _programLogo.Clear();
            _pendingArtLot = -1;
            _program = 0;
            _audio.Program = 0;
            lock (_lock) _status = new HdStatus { InputRate = _chan?.InputRate ?? 0 };
        }

        private void CloseSession()
        {
            if (_nrsc5 == IntPtr.Zero) return;
            Native.nrsc5_close(_nrsc5);
            _nrsc5 = IntPtr.Zero;
        }

        // ---- nrsc5 callbacks (worker thread) ----

        private void OnEvent(IntPtr e, IntPtr opaque)
        {
            try
            {
                switch (Native.EventType(e))
                {
                    case Native.EventSync:
                        Update(s => s.Synced = true);
                        break;
                    case Native.EventLostSync:
                        Update(s => s.Synced = false);
                        _audio.TimelineLost();
                        break;
                    case Native.EventMer:
                        float lo = Native.F32(e, 8), up = Native.F32(e, 12);
                        Update(s => { s.MerLower = lo; s.MerUpper = up; });
                        break;
                    case Native.EventBer:
                        float ber = Native.F32(e, 8);
                        Update(s => s.Ber = ber);
                        break;
                    case Native.EventAudio:
                        OnAudio(e);
                        break;
                    case Native.EventId3:
                        OnId3(e);
                        break;
                    case Native.EventLot:
                        OnLot(e);
                        break;
                    case Native.EventAudioService:
                        uint prog = Native.U32(e, 8), type = Native.U32(e, 16);
                        string typeName = Native.ProgramTypeName(type);
                        Update(s => s.Programs[prog] = typeName);
                        break;
                    case Native.EventStationId:
                        break;
                    case Native.EventStationName:
                        string name = Native.Str(e, 8);
                        Update(s => s.StationName = name);
                        break;
                    case Native.EventStationSlogan:
                        string slogan = Native.Str(e, 8);
                        Update(s => s.Slogan = slogan);
                        break;
                    case Native.EventStationMessage:
                        string msg = Native.Str(e, 8);
                        Update(s => s.Message = msg);
                        break;
                    case Native.EventEmergencyAlert:
                        string alert = Native.Str(e, 8);
                        Update(s => s.Alert = alert);
                        break;
                    case Native.EventHereImage:
                        OnHereImage(e);
                        break;
                }
            }
            catch
            {
                // Never let an exception unwind into native code.
            }
        }

        private void OnAudio(IntPtr e)
        {
            uint program = Native.U32(e, 8);
            var data = Native.Ptr(e, 16);
            long count = Native.Size(e, 24);          // int16 values, interleaved stereo
            uint flags = Native.U32(e, 32);

            if (!_status.Programs.ContainsKey(program))
                Update(s => s.Programs[program] = null);

            if (program != _program) return;
            _audio.Add((short*)data, (int)count, flags);
            if ((flags & Native.AudioFlagUnavailable) == 0)
                Update(s => s.LastAudio = DateTime.UtcNow);
        }

        private void OnId3(IntPtr e)
        {
            if (Native.U32(e, 8) != _program) return;
            string title = Native.Str(e, 16), artist = Native.Str(e, 24), album = Native.Str(e, 32);
            uint xhdrMime = Native.U32(e, 64);
            int xhdrLot = Native.I32(e, 72);

            byte[] art = null;
            if (xhdrMime == Native.MimePrimaryImage && xhdrLot >= 0)
            {
                _pendingArtLot = xhdrLot;
                _lotImages.TryGetValue((uint)xhdrLot, out art);
            }
            Update(s =>
            {
                if (title != null) s.Title = title;
                if (artist != null) s.Artist = artist;
                if (album != null) s.Album = album;
                if (art != null) s.AlbumArt = art;
            });
        }

        private void OnLot(IntPtr e)
        {
            uint lot = Native.U32(e, 12);
            uint size = Native.U32(e, 16);
            string name = Native.Str(e, 24);
            var data = Native.Ptr(e, 32);
            var service = Native.Ptr(e, 48);
            var component = Native.Ptr(e, 56);

            Update(s => { s.FilesReceived++; s.LastFile = name; });
            if (data == IntPtr.Zero || size < 8 || size > 4 * 1024 * 1024) return;

            var bytes = new byte[size];
            Marshal.Copy(data, bytes, 0, (int)size);
            if (!IsImage(bytes)) return;

            // The SIG component says what the file is (album art vs. logo), and the SIG audio
            // service it belongs to says which program (its audio component's port field).
            uint componentMime = component == IntPtr.Zero ? 0 : Native.U32(component, 20);
            int program = -1;
            if (service != IntPtr.Zero && Marshal.ReadByte(service, 8) == Native.SigServiceAudio)
            {
                var audioComponent = Native.Ptr(service, 32);
                program = audioComponent != IntPtr.Zero
                    ? Marshal.ReadByte(audioComponent, 12)
                    : Native.U16(service, 10) - 1;
            }
            bool current = program < 0 || program == _program;

            if (componentMime == Native.MimeStationLogo)
            {
                if (program >= 0) _programLogo[(uint)program] = bytes;
                if (current) Update(s => s.StationLogo = bytes);
                return;
            }

            if (_lotImages.Count > 64) _lotImages.Clear();
            _lotImages[lot] = bytes;
            if (componentMime == Native.MimePrimaryImage && program >= 0)
                _programArt[(uint)program] = bytes;
            if ((int)lot == _pendingArtLot || (componentMime == Native.MimePrimaryImage && current))
                Update(s => s.AlbumArt = bytes);
        }

        private void OnHereImage(IntPtr e)
        {
            int type = Native.I32(e, 8);
            int n1 = Native.I32(e, 16);
            string name = Native.Str(e, 48) ?? "";
            uint size = Native.U32(e, 56);
            var data = Native.Ptr(e, 64);
            if (data == IntPtr.Zero || size == 0 || size > 4 * 1024 * 1024) return;

            var bytes = new byte[size];
            Marshal.Copy(data, bytes, 0, (int)size);

            if (type == Native.HereImageWeather)
            {
                Update(s => { s.WeatherMap = bytes; s.WeatherTime = DateTime.Now; s.FilesReceived++; s.LastFile = name; });
            }
            else if (type == Native.HereImageTraffic)
            {
                // Tiles are named like "trafficMap_<row>_<col>_rdhs.png" (1-based); fall back to n1 order.
                int idx = n1 - 1;
                var parts = name.Split('_');
                if (parts.Length >= 3 && int.TryParse(parts[1], out int row) && int.TryParse(parts[2], out int col)
                    && row is >= 1 and <= 3 && col is >= 1 and <= 3)
                    idx = (row - 1) * 3 + (col - 1);
                if (idx < 0 || idx > 8) return;
                Update(s =>
                {
                    var tiles = (byte[][])s.TrafficTiles.Clone();
                    tiles[idx] = bytes;
                    s.TrafficTiles = tiles;
                    s.TrafficTime = DateTime.Now;
                    s.FilesReceived++;
                    s.LastFile = name;
                });
            }
        }

        private static bool IsImage(byte[] b) =>
            (b[0] == 0xFF && b[1] == 0xD8) ||                                   // JPEG
            (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) ||   // PNG
            (b[0] == 'G' && b[1] == 'I' && b[2] == 'F');                         // GIF

        /// <summary>Copy-on-write so the UI can read a snapshot without holding the lock.</summary>
        private void Update(Action<HdStatus> change)
        {
            lock (_lock)
            {
                var s = _status.Clone();
                change(s);
                _status = s;
            }
        }

        public void Dispose()
        {
            Stop();
            _queue.Dispose();
        }
    }
}
