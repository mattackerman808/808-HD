using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SDRSharp.HDRadio
{
    /// <summary>
    /// P/Invoke bindings for the subset of libnrsc5 used in "pipe" mode.
    /// See nrsc5/include/nrsc5.h for the full API.
    /// </summary>
    internal static unsafe class Native
    {
        private const string Lib = "libnrsc5";

        public const double SampleRateNativeFm = 744187.5;
        public const int AudioSampleRate = 44100;

        public const int ModeFm = 0;

        public const uint MimePrimaryImage = 0xBE4B7536;
        public const uint MimeStationLogo = 0xD9C72536;
        public const uint MimeJpeg = 0x1E653E9C;
        public const uint MimePng = 0x4F328CA0;

        public const uint AudioFlagUnavailable = 1;

        // Event ids (enum order in nrsc5.h).
        public const uint EventSync = 2;
        public const uint EventLostSync = 3;
        public const uint EventMer = 4;
        public const uint EventBer = 5;
        public const uint EventAudio = 7;
        public const uint EventId3 = 8;
        public const uint EventLot = 10;
        public const uint EventAudioService = 14;
        public const uint EventStationId = 15;
        public const uint EventStationName = 16;
        public const uint EventStationSlogan = 17;
        public const uint EventStationMessage = 18;
        public const uint EventEmergencyAlert = 22;
        public const uint EventHereImage = 23;

        public const byte SigServiceAudio = 0;
        public const int HereImageTraffic = 8;
        public const int HereImageWeather = 13;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void Callback(IntPtr evt, IntPtr opaque);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void nrsc5_get_version(out IntPtr version);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void nrsc5_program_type_name(uint type, out IntPtr name);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int nrsc5_open_pipe(out IntPtr st);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void nrsc5_close(IntPtr st);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void nrsc5_start(IntPtr st);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int nrsc5_set_mode(IntPtr st, int mode);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void nrsc5_set_callback(IntPtr st, Callback callback, IntPtr opaque);

        /// <param name="length">Number of floats (2 per complex sample).</param>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int nrsc5_pipe_samples_cf32(IntPtr st, float* samples, uint length);

        private static bool _resolverSet;

        /// <summary>
        /// SDR# loads plugins from sub-folders, so make sure libnrsc5.dll is found
        /// next to this assembly rather than relying on the default search path.
        /// </summary>
        public static void EnsureResolver()
        {
            if (_resolverSet) return;
            _resolverSet = true;
            NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, asm, path) =>
            {
                if (name != Lib) return IntPtr.Zero;
                var dir = Path.GetDirectoryName(asm.Location);
                var full = Path.Combine(dir ?? ".", "libnrsc5.dll");
                return NativeLibrary.TryLoad(full, out var h) ? h : IntPtr.Zero;
            });
        }

        public static string Version()
        {
            nrsc5_get_version(out var p);
            return Marshal.PtrToStringUTF8(p);
        }

        public static string ProgramTypeName(uint type)
        {
            nrsc5_program_type_name(type, out var p);
            return p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
        }

        // ---- nrsc5_event_t field access (x64 layout: 4-byte event id, union at offset 8) ----

        public static uint EventType(IntPtr evt) => (uint)Marshal.ReadInt32(evt, 0);
        public static int I32(IntPtr evt, int offset) => Marshal.ReadInt32(evt, offset);
        public static uint U32(IntPtr evt, int offset) => (uint)Marshal.ReadInt32(evt, offset);
        public static ushort U16(IntPtr evt, int offset) => (ushort)Marshal.ReadInt16(evt, offset);
        public static float F32(IntPtr evt, int offset) => BitConverter.Int32BitsToSingle(Marshal.ReadInt32(evt, offset));
        public static IntPtr Ptr(IntPtr evt, int offset) => Marshal.ReadIntPtr(evt, offset);
        public static long Size(IntPtr evt, int offset) => Marshal.ReadInt64(evt, offset);

        public static string Str(IntPtr evt, int offset)
        {
            var p = Marshal.ReadIntPtr(evt, offset);
            return p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
        }
    }
}
