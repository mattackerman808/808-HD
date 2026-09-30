using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using SDRSharp.Common;
using SDRSharp.Radio;

namespace SDRSharp.HDRadio
{
    /// <summary>
    /// Runs the HD Radio engine (decides when to decode, owns the stream hooks) and is the
    /// plugin's small settings page. Everything you look at while listening lives in the
    /// <see cref="NowPlayingBar"/> above the spectrum.
    /// </summary>
    public sealed class ControlPanel : UserControl
    {
        private const string SettingEnabled = "hdradio.enabled";
        private const string SettingForceAnalog = "hdradio.forceAnalog";

        private readonly ISharpControl _control;
        private readonly AudioInjector _audio = new AudioInjector();
        private readonly HdDecoder _decoder;
        private readonly IQHook _iqHook;
        private readonly NowPlayingBar _bar;
        private readonly Timer _timer = new Timer { Interval = 250 };

        private readonly CheckBox _enable = new CheckBox { Text = "Decode HD Radio automatically (WFM)", AutoSize = true };
        private readonly Label _state = new Label { AutoSize = true };
        private readonly Label _signal = new Label { AutoSize = true };
        private readonly Label _align = new Label { AutoSize = true, MaximumSize = new Size(260, 0) };
        private readonly Label _message = new Label { AutoSize = true, MaximumSize = new Size(260, 0) };
        private readonly Label _data = new Label { AutoSize = true, MaximumSize = new Size(260, 0) };
        private readonly Label _hint = new Label { AutoSize = true, MaximumSize = new Size(260, 0), ForeColor = SystemColors.GrayText };

        private bool _hooksRegistered;
        private bool _running;
        private long _tunedFrequency;
        private DateTime _tunedAt;

        public ControlPanel(ISharpControl control)
        {
            _control = control;
            _decoder = new HdDecoder(_audio);
            _iqHook = new IQHook(_decoder);

            BuildLayout();

            _bar = new NowPlayingBar(control);
            _bar.ProgramClicked += p => _decoder.Program = p;
            _bar.WeatherClicked += () => ShowLarge(LoadImage(_decoder.Status.WeatherMap), "Weather map");
            _bar.TrafficClicked += () => ShowLarge(ComposeTiles(_decoder.Status.TrafficTiles), "Traffic map");
            _bar.ForceAnalogChanged += force =>
            {
                _audio.ForceAnalog = force;
                Utils.SaveSetting(SettingForceAnalog, force);
                Tick();
            };
            _control.RegisterFrontControl(_bar, PluginPosition.Top);
            _audio.ForceAnalog = Utils.GetBooleanSetting(SettingForceAnalog, false);

            _enable.Checked = Utils.GetBooleanSetting(SettingEnabled, true);
            _enable.CheckedChanged += (s, e) =>
            {
                Utils.SaveSetting(SettingEnabled, _enable.Checked);
                Tick();
            };

            _timer.Tick += (s, e) => Tick();
            _timer.Start();
        }

        /// <summary>True while the decoder is actually running (enabled, radio playing, WFM).</summary>
        public bool DecoderRunning => _running;

        private void BuildLayout()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(4) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            void Row(Control c, string label = null)
            {
                if (label == null)
                {
                    t.Controls.Add(c);
                    t.SetColumnSpan(c, 2);
                }
                else
                {
                    t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left });
                    t.Controls.Add(c);
                }
            }

            Row(_enable);
            Row(_state, "Status");
            Row(_signal, "Signal");
            Row(_align, "Alignment");
            Row(_message, "Message");
            Row(_data, "Data");
            Row(_hint);

            _hint.Text = "Station, song, art, HD1/HD2 and the Auto HD / Analog switch are in the strip above the spectrum. " +
                         "Audio plays through SDR#'s own volume and output. Needs a device sample rate of at least 1 MS/s.";
            Controls.Add(t);
            AutoScroll = true;
            Size = new Size(280, 220);
        }

        // ---- engine ----

        private void Tick()
        {
            string idleReason =
                !_enable.Checked ? "Off" :
                !_control.IsPlaying ? "Idle (radio stopped)" :
                _control.DetectorType != DetectorType.WFM ? "Idle (HD Radio needs WFM mode)" :
                null;

            if (idleReason == null && !_running) StartDecoder();
            else if (idleReason != null && _running) StopDecoder();

            if (_running)
            {
                _decoder.SetTuning(_control.Frequency, _control.CenterFrequency);
                RefreshUi(_decoder.Status);
            }
            else
            {
                _state.Text = idleReason;
            }
        }

        private void StartDecoder()
        {
            _running = true;
            _audio.Clear();
            _decoder.SetTuning(_control.Frequency, _control.CenterFrequency);
            _decoder.Start();
            _iqHook.Enabled = true;
            if (!_hooksRegistered)
            {
                _control.RegisterStreamHook(_iqHook, ProcessorType.RawIQ);
                _control.RegisterStreamHook(_audio, ProcessorType.PostAF);
                _hooksRegistered = true;
            }
            _bar.Visible = true;
        }

        private void StopDecoder()
        {
            _running = false;
            _iqHook.Enabled = false;
            if (_hooksRegistered)
            {
                _control.UnregisterStreamHook(_iqHook);
                _control.UnregisterStreamHook(_audio);
                _hooksRegistered = false;
            }
            _decoder.Stop();
            _audio.Clear();
            _bar.Visible = false;
            _signal.Text = _align.Text = _message.Text = _data.Text = "";
        }

        private void RefreshUi(HdStatus s)
        {
            long freq = _control.Frequency;
            if (freq != _tunedFrequency)
            {
                _tunedFrequency = freq;
                _tunedAt = DateTime.UtcNow;
            }

            // State text is driven by the audio blend (which has hysteresis), not raw sync,
            // so a marginal signal doesn't make it flicker.
            bool hdAudio = (DateTime.UtcNow - s.LastAudio).TotalSeconds < 3;
            double retry = _audio.RetryIn;
            string state =
                s.Error != null ? "Error: " + s.Error :
                s.InputRate > 0 && s.InputRate < 1_000_000 ? $"Sample rate {s.InputRate / 1e6:0.###} MS/s is too low" :
                _audio.ForceAnalog ? (hdAudio ? "Analog (HD available)" : "Analog") :
                _audio.PlayingHd ? "Playing HD audio" :
                retry > 0 ? $"HD signal unstable, analog for now ({Math.Ceiling(retry):0}s)" :
                hdAudio && _decoder.Program == 0 && !_audio.Aligned ? "Aligning HD with analog..." :
                hdAudio ? "Starting HD audio..." :
                s.Synced ? "HD found, waiting for audio..." :
                (DateTime.UtcNow - _tunedAt).TotalSeconds < 15 ? "Looking for HD signal..." :
                "Analog station (no HD)";

            _state.Text = state;
            _signal.Text = s.Synced ? $"MER {s.MerLower:0.0} / {s.MerUpper:0.0} dB   BER {s.Ber:0.0000}" : "-";
            _align.Text = _decoder.Program != 0 ? "Not applicable (HD2+ has no analog simulcast)"
                : _audio.Aligned ? $"HD runs {_audio.HdLeadSeconds:0.00}s ahead of analog, compensated (match {_audio.AlignScore:0.00})"
                : "Measuring...";
            _message.Text = s.Message ?? "";
            _data.Text = s.FilesReceived == 0 ? "No data files received yet"
                : $"{s.FilesReceived} files (last: {s.LastFile})";

            if (!_bar.Visible) _bar.Visible = true;
            _bar.UpdateInfo(new BarInfo
            {
                Hd = s,
                Program = _decoder.Program,
                PlayingHd = _audio.PlayingHd,
                ForceAnalog = _audio.ForceAnalog,
                State = state,
                RdsName = _control.RdsProgramService,
                RdsText = _control.RdsRadioText,
                FrequencyHz = freq,
            });
        }

        // ---- map images ----

        private static Image ComposeTiles(byte[][] tiles)
        {
            var images = tiles.Select(LoadImage).ToArray();
            var first = images.FirstOrDefault(i => i != null);
            if (first == null) return null;
            int w = first.Width, h = first.Height;
            var bmp = new Bitmap(w * 3, h * 3);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.DimGray);
                for (int i = 0; i < 9; i++)
                    if (images[i] != null)
                        g.DrawImage(images[i], (i % 3) * w, (i / 3) * h, w, h);
            }
            foreach (var img in images) img?.Dispose();
            return bmp;
        }

        internal static Image LoadImage(byte[] bytes)
        {
            if (bytes == null) return null;
            try
            {
                using var ms = new MemoryStream(bytes);
                using var img = Image.FromStream(ms);
                return new Bitmap(img);   // detach from the stream
            }
            catch
            {
                return null;   // corrupt or partial image
            }
        }

        private void ShowLarge(Image image, string title)
        {
            if (image == null) return;
            var box = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, Image = image };
            var form = new Form
            {
                Text = title,
                Size = new Size(640, 660),
                StartPosition = FormStartPosition.CenterParent,
                ShowInTaskbar = false,
            };
            form.Controls.Add(box);
            form.FormClosed += (s, e) => image.Dispose();
            form.Show(_bar.FindForm());
        }

        public void Shutdown()
        {
            _timer.Stop();
            if (_running) StopDecoder();
            _decoder.Dispose();
        }
    }
}
