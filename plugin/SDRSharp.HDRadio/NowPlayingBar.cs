using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using SDRSharp.Common;

namespace SDRSharp.HDRadio
{
    /// <summary>Everything the strip shows, gathered once per UI tick.</summary>
    internal sealed class BarInfo
    {
        public HdStatus Hd;
        public uint Program;
        public bool PlayingHd;
        public bool ForceAnalog;
        public string State;
        public string RdsName;
        public string RdsText;
        public long FrequencyHz;
    }

    /// <summary>
    /// "Now playing" strip that SDR# docks above the spectrum (RegisterFrontControl).
    /// Stays up while the decoder runs; a badge shows whether you're hearing HD or FM,
    /// so a marginal signal changes a badge instead of the whole layout.
    /// Custom-painted so it adapts to whatever height SDR# gives the panel.
    /// </summary>
    internal sealed class NowPlayingBar : UserControl
    {
        private static readonly Color Accent = Color.FromArgb(0x2D, 0x8C, 0xFF);
        private const TextFormatFlags TfText = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        private const TextFormatFlags TfClip = TfText | TextFormatFlags.EndEllipsis;
        private const TextFormatFlags TfCenter = TfText | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;

        private readonly ISharpControl _control;
        private readonly List<(Rectangle Rect, uint Program)> _chips = new List<(Rectangle, uint)>();
        private readonly ToolTip _tip = new ToolTip();
        private Rectangle _weatherChip, _trafficChip, _autoButton, _analogButton;
        private string _tipText;

        private BarInfo _info;
        private byte[] _artBytes;
        private Image _art;

        public event Action<uint> ProgramClicked;
        public event Action WeatherClicked;
        public event Action TrafficClicked;
        public event Action<bool> ForceAnalogChanged;

        public NowPlayingBar(ISharpControl control)
        {
            _control = control;
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            Height = 170;
            MinimumSize = new Size(0, 162);
            Dock = DockStyle.Top;
            Visible = false;
            // SDR# titles the docking panel that hosts front controls; give it our name.
            Name = "HDRadioNowPlaying";
            Text = HDRadioPlugin.Name;
        }

        /// <summary>Which 808 HD mark to draw (the preview tool switches it; the plugin uses the default).</summary>
        internal static BrandMark.Style BrandStyle = BrandMark.Style.Waves;

        public void UpdateInfo(BarInfo info)
        {
            _info = info;
            var bytes = info.Hd?.AlbumArt ?? info.Hd?.StationLogo;
            if (!ReferenceEquals(bytes, _artBytes))
            {
                _artBytes = bytes;
                _art?.Dispose();
                _art = ControlPanel.LoadImage(bytes);
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            bool dark = _control.ThemeIsDark;
            var back = dark ? Color.FromArgb(24, 26, 30) : _control.ThemePanelColor;
            var fore = dark ? Color.FromArgb(235, 235, 235) : _control.ThemeForeColor;
            var dim = dark ? Color.FromArgb(150, 155, 165) : Color.FromArgb(100, 100, 100);
            var faint = dark ? Color.FromArgb(60, 64, 70) : Color.FromArgb(210, 210, 210);
            g.Clear(back);

            _chips.Clear();
            _weatherChip = _trafficChip = _autoButton = _analogButton = Rectangle.Empty;
            var info = _info;
            if (info == null) return;
            var s = info.Hd;

            using var badgeFont = new Font(Font.FontFamily, 8f, FontStyle.Bold);
            using var stationFont = new Font(Font.FontFamily, 10f, FontStyle.Bold);
            using var titleFont = new Font(Font.FontFamily, 13f, FontStyle.Bold);
            using var artistFont = new Font(Font.FontFamily, 12f, FontStyle.Regular);
            using var smallFont = new Font(Font.FontFamily, 8.5f, FontStyle.Regular);
            using var chipFont = new Font(Font.FontFamily, 8f, FontStyle.Bold);

            // Header row (808 HD mark) above a fixed-height content block; the pair is
            // vertically centered in whatever height SDR# gives the panel.
            const int pad = 8, markH = 34, headerGap = 12;
            int block = Math.Clamp(Height - 2 * pad - markH - headerGap, 100, 150);
            int groupTop = Math.Max(pad, (Height - markH - headerGap - block) / 2);

            // ---- header: 808 HD mark, left-aligned with the art ----
            int markW = BrandMark.Measure(g, markH, BrandStyle);
            BrandMark.Draw(g, new Rectangle(pad, groupTop, markW, markH), BrandStyle, fore);
            int top = groupTop + markH + headerGap;

            // ---- art / placeholder tile ----
            var artRect = new Rectangle(pad, top, block, block);
            // Placeholder follows what you are hearing (same as the badge), not whether HD exists.
            bool hdStation = info.PlayingHd;
            if (_art != null)
            {
                g.DrawImage(_art, artRect);
            }
            else
            {
                using var b = new SolidBrush(hdStation ? Accent : faint);
                using var path = Rounded(artRect, 8);
                g.FillPath(b, path);
                using var f = new Font(Font.FontFamily, Math.Max(12, block / 5f), FontStyle.Bold);
                TextRenderer.DrawText(g, hdStation ? "HD" : "FM", f, artRect, hdStation ? Color.White : dim, TfCenter);
            }

            // ---- right column: signal, state, Auto/Analog switch ----
            const int rightW = 220;
            int rx = Width - rightW - pad;
            int sy = top;
            float mer = s == null ? 0 : Math.Min(s.MerLower, s.MerUpper);
            bool synced = s?.Synced ?? false;
            int bars = !synced ? 0 : mer >= 10 ? 5 : mer >= 8 ? 4 : mer >= 6 ? 3 : mer >= 4 ? 2 : 1;
            for (int i = 0; i < 5; i++)
            {
                int h = 6 + i * 4;
                using var b = new SolidBrush(i < bars ? Accent : faint);
                g.FillRectangle(b, rx + i * 8, sy + 22 - h, 6, h);
            }
            TextRenderer.DrawText(g, synced ? $"HD signal  MER {mer:0.0} dB" : "No HD signal", smallFont,
                new Rectangle(rx + 48, sy + 6, rightW - 48, 16), dim, TfClip);
            TextRenderer.DrawText(g, info.State ?? "", smallFont, new Rectangle(rx, sy + 28, rightW, 16), dim, TfClip);

            int by = top + block - 24;
            _autoButton = new Rectangle(rx, by, rightW / 2, 24);
            _analogButton = new Rectangle(rx + rightW / 2, by, rightW / 2, 24);
            DrawSegment(g, _autoButton, "Auto HD", !info.ForceAnalog, chipFont, fore, faint, left: true);
            DrawSegment(g, _analogButton, "Analog", info.ForceAnalog, chipFont, fore, faint, left: false);

            // ---- middle column ----
            int x = artRect.Right + 14;
            int right = rx - 16;
            int y = top;

            // Row 1: source badge, station, slogan (or emergency alert).
            string badge = info.PlayingHd ? $"HD{info.Program + 1}" : "FM";
            var badgeSize = TextRenderer.MeasureText(g, badge, badgeFont, Size.Empty, TfText);
            var badgeRect = new Rectangle(x, y + 1, badgeSize.Width + 12, 18);
            using (var path = Rounded(badgeRect, 4))
            using (var b = new SolidBrush(info.PlayingHd ? Accent : faint))
                g.FillPath(b, path);
            TextRenderer.DrawText(g, badge, badgeFont, badgeRect, info.PlayingHd ? Color.White : fore, TfCenter);

            string station = s?.StationName?.Trim();
            if (string.IsNullOrEmpty(station)) station = info.RdsName?.Trim();
            if (string.IsNullOrEmpty(station)) station = $"{info.FrequencyHz / 1e6:0.0} FM";
            int sx = badgeRect.Right + 8;
            int stationW = Math.Min(TextRenderer.MeasureText(g, station, stationFont, Size.Empty, TfText).Width, Math.Max(0, right - sx));
            TextRenderer.DrawText(g, station, stationFont, new Rectangle(sx, y + 1, stationW, 20), Accent, TfClip);

            var rest = new Rectangle(sx + stationW + 12, y + 2, Math.Max(0, right - sx - stationW - 12), 18);
            if (!string.IsNullOrEmpty(s?.Alert))
            {
                using var ab = new SolidBrush(Color.Firebrick);
                using var ap = Rounded(rest, 4);
                g.FillPath(ab, ap);
                TextRenderer.DrawText(g, " ALERT: " + s.Alert, smallFont, rest, Color.White, TfClip | TextFormatFlags.VerticalCenter);
            }
            else if (!string.IsNullOrEmpty(s?.Slogan))
            {
                TextRenderer.DrawText(g, s.Slogan.Trim(), smallFont, rest, dim, TfClip | TextFormatFlags.VerticalCenter);
            }

            // Row 2: title - artist (HD), else RDS radio text, else nothing.
            y += 28;
            string title = s?.Title?.Trim(), artist = s?.Artist?.Trim();
            if (!string.IsNullOrEmpty(title))
            {
                int titleW = Math.Min(TextRenderer.MeasureText(g, title, titleFont, Size.Empty, TfText).Width, Math.Max(0, right - x));
                TextRenderer.DrawText(g, title, titleFont, new Rectangle(x, y, titleW, 26), fore, TfClip);
                int ax = x + titleW + 10;
                if (!string.IsNullOrEmpty(artist) && ax + 30 < right)
                    TextRenderer.DrawText(g, "\u2014  " + artist, artistFont, new Rectangle(ax, y + 2, right - ax, 24), dim, TfClip);
            }
            else if (!string.IsNullOrEmpty(info.RdsText?.Trim()))
            {
                TextRenderer.DrawText(g, info.RdsText.Trim(), artistFont, new Rectangle(x, y + 2, Math.Max(0, right - x), 24), fore, TfClip);
            }

            // Row 3 (bottom-aligned with the art): program buttons + map buttons.
            int cx = x, cy = top + block - 22;
            if (s != null)
            {
                foreach (var p in s.Programs)
                {
                    string label = string.IsNullOrEmpty(p.Value) || p.Value == "None" ? $"HD{p.Key + 1}" : $"HD{p.Key + 1}  {p.Value}";
                    var r = new Rectangle(cx, cy, TextRenderer.MeasureText(g, label, chipFont, Size.Empty, TfText).Width + 20, 22);
                    if (r.Right > right) break;
                    bool sel = p.Key == info.Program;
                    using var path = Rounded(r, 11);
                    if (sel) { using var b = new SolidBrush(Accent); g.FillPath(b, path); }
                    else { using var pen = new Pen(dim); g.DrawPath(pen, path); }
                    TextRenderer.DrawText(g, label, chipFont, r, sel ? Color.White : fore, TfCenter);
                    _chips.Add((r, p.Key));
                    cx = r.Right + 8;
                }
                if (s.WeatherMap != null)
                    _weatherChip = ActionChip(g, "Weather map", chipFont, ref cx, cy, right, fore, dim);
                if (Array.Exists(s.TrafficTiles, t => t != null))
                    _trafficChip = ActionChip(g, "Traffic map", chipFont, ref cx, cy, right, fore, dim);
            }
        }

        private void DrawSegment(Graphics g, Rectangle r, string label, bool on, Font font, Color fore, Color faint, bool left)
        {
            using var path = new GraphicsPath();
            int d = 8;
            if (left)
            {
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddLine(r.Right, r.Y, r.Right, r.Bottom);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            }
            else
            {
                path.AddLine(r.X, r.Y, r.Right - d, r.Y);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddLine(r.X, r.Bottom, r.X, r.Y);
            }
            path.CloseFigure();
            using (var b = new SolidBrush(on ? Accent : Color.Transparent)) g.FillPath(b, path);
            using (var pen = new Pen(on ? Accent : faint)) g.DrawPath(pen, path);
            TextRenderer.DrawText(g, label, font, r, on ? Color.White : fore, TfCenter);
        }

        private Rectangle ActionChip(Graphics g, string label, Font font, ref int cx, int cy, int right, Color fore, Color dim)
        {
            var r = new Rectangle(cx, cy, TextRenderer.MeasureText(g, label, font, Size.Empty, TfText).Width + 20, 22);
            if (r.Right > right) return Rectangle.Empty;
            using (var path = Rounded(r, 11))
            using (var pen = new Pen(dim) { DashStyle = DashStyle.Dot })
                g.DrawPath(pen, path);
            TextRenderer.DrawText(g, label, font, r, fore, TfCenter);
            cx = r.Right + 8;
            return r;
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            var p = e.Location;
            if (_autoButton.Contains(p)) { ForceAnalogChanged?.Invoke(false); return; }
            if (_analogButton.Contains(p)) { ForceAnalogChanged?.Invoke(true); return; }
            if (_weatherChip.Contains(p)) { WeatherClicked?.Invoke(); return; }
            if (_trafficChip.Contains(p)) { TrafficClicked?.Invoke(); return; }
            foreach (var (rect, program) in _chips)
                if (rect.Contains(p)) { ProgramClicked?.Invoke(program); return; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var p = e.Location;
            string tip =
                _autoButton.Contains(p) ? "Play HD audio whenever it's available, analog otherwise" :
                _analogButton.Contains(p) ? "Always play analog FM (HD data still shown)" :
                null;
            bool over = tip != null || _weatherChip.Contains(p) || _trafficChip.Contains(p);
            foreach (var (rect, _) in _chips) over |= rect.Contains(p);
            Cursor = over ? Cursors.Hand : Cursors.Default;
            if (tip != _tipText)
            {
                _tipText = tip;
                _tip.SetToolTip(this, tip);
            }
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _art?.Dispose();
                _tip.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
