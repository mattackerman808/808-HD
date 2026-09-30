using SDRSharp.Radio;

namespace SDRSharp.HDRadio
{
    /// <summary>
    /// Registered on SDR#'s RawIQ stream: receives the full-bandwidth device samples
    /// and passes a copy to the decoder. Samples are left untouched.
    /// </summary>
    internal sealed unsafe class IQHook : IIQProcessor
    {
        private readonly HdDecoder _decoder;

        public IQHook(HdDecoder decoder)
        {
            _decoder = decoder;
        }

        public double SampleRate { get; set; }

        public bool Enabled { get; set; }

        public void Process(Complex* buffer, int length)
        {
            if (!Enabled || SampleRate <= 0) return;
            // SDRSharp.Radio.Complex is { float Real; float Imag; } == interleaved cf32.
            _decoder.Enqueue((float*)buffer, length, SampleRate);
        }
    }
}
