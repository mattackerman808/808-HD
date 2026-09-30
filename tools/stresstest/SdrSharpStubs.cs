// Minimal stand-ins for the SDR# interfaces AudioInjector implements. The SDK's
// SDRSharp.Radio.dll is a reference assembly and cannot be loaded outside SDR#.
namespace SDRSharp.Radio
{
    public interface IBaseProcessor
    {
        bool Enabled { get; set; }
    }

    public interface IStreamProcessor : IBaseProcessor
    {
        double SampleRate { set; }
    }

    public unsafe interface IRealProcessor : IStreamProcessor
    {
        void Process(float* buffer, int length);
    }
}
