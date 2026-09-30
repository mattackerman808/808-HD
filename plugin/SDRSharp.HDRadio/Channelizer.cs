using System;

namespace SDRSharp.HDRadio
{
    /// <summary>
    /// Shifts the wanted station to 0 Hz and resamples the IQ stream from the
    /// device rate to the rate libnrsc5 expects (744187.5 S/s for FM).
    ///
    /// The resampler is a windowed-sinc interpolator evaluated at arbitrary
    /// fractional positions, with the kernel pre-tabulated. The low-pass cutoff
    /// keeps the full HD Radio signal (about +/-200 kHz) and removes anything
    /// that would alias into it at the output rate.
    /// </summary>
    internal sealed class Channelizer
    {
        private const int TableOversample = 512;
        private const double PassbandHz = 250_000;

        private readonly double _inRate;
        private readonly double _step;       // input samples per output sample
        private readonly int _halfWidth;     // kernel half-width in input samples
        private readonly float[] _kernel;    // kernel sampled every 1/TableOversample input samples

        // Mixer state (complex phasor rotated by -offset each sample).
        private double _offsetHz = double.NaN;
        private double _phRe = 1, _phIm = 0, _rotRe = 1, _rotIm = 0;

        // Pending input (after mixing) and the fractional read position.
        private float[] _re = new float[1 << 16];
        private float[] _im = new float[1 << 16];
        private int _count;
        private double _t;

        public double InputRate => _inRate;

        public Channelizer(double inputRate, double outputRate)
        {
            if (inputRate < outputRate)
                throw new ArgumentException($"Device sample rate {inputRate / 1e6:0.###} MS/s is too low; HD Radio needs at least {outputRate / 1e6:0.###} MS/s.");

            _inRate = inputRate;
            _step = inputRate / outputRate;

            // Everything above (outputRate - 200 kHz) folds back onto the HD sidebands.
            double stopHz = outputRate - 200_000;
            double transition = (stopHz - PassbandHz) / inputRate;    // cycles/sample
            double cutoff = (PassbandHz + stopHz) / 2 / inputRate;     // midpoint, cycles/sample
            int taps = (int)Math.Ceiling(6.0 / transition);                   // Blackman-Harris, ~90 dB
            _halfWidth = Math.Max(4, (taps + 1) / 2);

            int n = 2 * _halfWidth * TableOversample + 1;
            _kernel = new float[n + 1];
            for (int i = 0; i <= n; i++)
            {
                double x = (double)i / TableOversample - _halfWidth;         // input-sample units
                double sinc = x == 0 ? 1 : Math.Sin(2 * Math.PI * cutoff * x) / (2 * Math.PI * cutoff * x);
                double w = BlackmanHarris((x + _halfWidth) / (2.0 * _halfWidth));
                _kernel[i] = (float)(2 * cutoff * sinc * w);
            }
            _t = _halfWidth;
        }

        private static double BlackmanHarris(double u)
        {
            if (u < 0 || u > 1) return 0;
            double a = 2 * Math.PI * u;
            return 0.35875 - 0.48829 * Math.Cos(a) + 0.14128 * Math.Cos(2 * a) - 0.01168 * Math.Cos(3 * a);
        }

        public void SetOffset(double offsetHz)
        {
            if (offsetHz == _offsetHz) return;
            _offsetHz = offsetHz;
            double w = -2 * Math.PI * offsetHz / _inRate;
            _rotRe = Math.Cos(w);
            _rotIm = Math.Sin(w);
        }

        /// <summary>
        /// Mixes and resamples <paramref name="length"/> complex samples (interleaved re/im)
        /// and appends the interleaved output to <paramref name="output"/>.
        /// Returns the number of floats written.
        /// </summary>
        public unsafe int Process(float* input, int length, ref float[] output)
        {
            EnsureCapacity(_count + length);

            // Mix to baseband.
            double pr = _phRe, pi = _phIm, rr = _rotRe, ri = _rotIm;
            fixed (float* re = _re, im = _im)
            {
                for (int i = 0; i < length; i++)
                {
                    float xr = input[2 * i], xi = input[2 * i + 1];
                    re[_count + i] = (float)(xr * pr - xi * pi);
                    im[_count + i] = (float)(xr * pi + xi * pr);
                    double npr = pr * rr - pi * ri;
                    pi = pr * ri + pi * rr;
                    pr = npr;
                }
            }
            double mag = Math.Sqrt(pr * pr + pi * pi);   // keep the phasor on the unit circle
            _phRe = pr / mag;
            _phIm = pi / mag;
            _count += length;

            // Resample.
            int maxOut = (int)((_count - _t) / _step) + 2;
            if (output == null || output.Length < maxOut * 2)
                output = new float[maxOut * 2];

            int o = 0;
            int hw = _halfWidth;
            fixed (float* re = _re, im = _im, k = _kernel, dst = output)
            {
                while (_t + hw < _count)
                {
                    int center = (int)_t;
                    float sr = 0, si = 0;
                    int j0 = center - hw + 1;
                    // Kernel table index for input sample j is (j - t + hw) * oversample.
                    double pos = (j0 - _t + hw) * TableOversample;
                    for (int j = j0; j <= center + hw; j++, pos += TableOversample)
                    {
                        int ki = (int)pos;
                        float kf = (float)(pos - ki);
                        float kv = k[ki] + (k[ki + 1] - k[ki]) * kf;
                        sr += re[j] * kv;
                        si += im[j] * kv;
                    }
                    dst[o++] = sr;
                    dst[o++] = si;
                    _t += _step;
                }
            }

            // Drop consumed input, keeping enough history for the next kernel.
            int drop = (int)_t - hw;
            if (drop > 0)
            {
                int keep = _count - drop;
                Array.Copy(_re, drop, _re, 0, keep);
                Array.Copy(_im, drop, _im, 0, keep);
                _count = keep;
                _t -= drop;
            }
            return o;
        }

        private void EnsureCapacity(int n)
        {
            if (n <= _re.Length) return;
            int size = _re.Length;
            while (size < n) size *= 2;
            Array.Resize(ref _re, size);
            Array.Resize(ref _im, size);
        }
    }
}
