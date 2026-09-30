using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace SDRSharp.HDRadio
{
    /// <summary>
    /// The "808 HD" mark drawn in the now-playing strip. Original artwork (not the HD Radio
    /// trademark). If a logo.png sits next to the plugin DLL it is drawn instead.
    /// </summary>
    internal static class BrandMark
    {
        public enum Style { Badge, Waves, Italic }

        private static readonly Color Orange = Color.FromArgb(0xF7, 0x94, 0x1D);
        private static readonly Color Blue = Color.FromArgb(0x2D, 0x8C, 0xFF);

        private static Image _custom;
        private static bool _customLoaded;

        /// <summary>User-supplied logo.png next to the plugin DLL, if any.</summary>
        public static Image Custom
        {
            get
            {
                if (_customLoaded) return _custom;
                _customLoaded = true;
                try
                {
                    var dir = Path.GetDirectoryName(typeof(BrandMark).Assembly.Location);
                    var path = Path.Combine(dir ?? ".", "logo.png");
                    if (File.Exists(path))
                    {
                        using var fs = File.OpenRead(path);
                        using var img = Image.FromStream(fs);
                        _custom = new Bitmap(img);
                    }
                }
                catch { _custom = null; }
                return _custom;
            }
        }

        /// <summary>Width the mark needs at the given height.</summary>
        public static int Measure(Graphics g, int height, Style style)
        {
            var custom = Custom;
            if (custom != null) return (int)Math.Round(custom.Width * (double)height / custom.Height);
            using var numFont = NumberFont(height);
            int w808 = TextRenderer.MeasureText(g, "808", numFont, Size.Empty, TextFormatFlags.NoPadding).Width;
            return style switch
            {
                Style.Badge => w808 + 4 + BadgeWidth(g, height),
                Style.Waves => w808 + 4 + HdWidth(g, height, bold: true) + height * 3 / 4,
                _ => w808 + 2 + HdWidth(g, height, bold: true),
            };
        }

        /// <summary>Draws the mark right-aligned in <paramref name="area"/>, vertically centered.</summary>
        public static void Draw(Graphics g, Rectangle area, Style style, Color fore)
        {
            int h = area.Height;
            int w = Measure(g, h, style);
            int x = area.Right - w, y = area.Y;
            var custom = Custom;
            if (custom != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(custom, new Rectangle(x, y, w, h));
                return;
            }

            g.SmoothingMode = SmoothingMode.AntiAlias;
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
            using var numFont = NumberFont(h);
            int w808 = TextRenderer.MeasureText(g, "808", numFont, Size.Empty, TextFormatFlags.NoPadding).Width;

            switch (style)
            {
                case Style.Badge:
                {
                    TextRenderer.DrawText(g, "808", numFont, new Rectangle(x, y, w808 + 2, h), fore, flags);
                    int bw = BadgeWidth(g, h);
                    var r = new Rectangle(x + w808 + 4, y + h / 8, bw, h - h / 4);
                    using (var path = Rounded(r, Math.Max(3, h / 6)))
                    using (var b = new SolidBrush(Orange))
                        g.FillPath(b, path);
                    using var hdFont = new Font("Segoe UI", h * 0.46f, FontStyle.Bold, GraphicsUnit.Pixel);
                    TextRenderer.DrawText(g, "HD", hdFont, r, Color.White, flags | TextFormatFlags.HorizontalCenter);
                    break;
                }
                case Style.Waves:
                {
                    TextRenderer.DrawText(g, "808", numFont, new Rectangle(x, y, w808 + 2, h), fore, flags);
                    using var hdFont = HdFont(h, bold: true);
                    int hx = x + w808 + 4, hw = HdWidth(g, h, bold: true);
                    TextRenderer.DrawText(g, "HD", hdFont, new Rectangle(hx, y, hw + 2, h), Orange, flags);
                    // Three broadcast arcs, radiating to the right of the text.
                    float cx = hx + hw + h * 0.05f, cy = y + h / 2f;
                    for (int i = 1; i <= 3; i++)
                    {
                        float r = h * 0.17f * i;
                        using var pen = new Pen(Color.FromArgb(255 - (i - 1) * 60, Orange), Math.Max(1.5f, h / 11f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                        g.DrawArc(pen, cx - r, cy - r, 2 * r, 2 * r, -45, 90);
                    }
                    break;
                }
                default:
                {
                    TextRenderer.DrawText(g, "808", numFont, new Rectangle(x, y, w808 + 2, h), Blue, flags);
                    using var hdFont = new Font("Segoe UI", h * 0.72f, FontStyle.Bold | FontStyle.Italic, GraphicsUnit.Pixel);
                    int hx = x + w808 + 2;
                    TextRenderer.DrawText(g, "HD", hdFont, new Rectangle(hx, y, HdWidth(g, h, bold: true) + 4, h), Orange, flags);
                    using var bar = new SolidBrush(Orange);
                    g.FillRectangle(bar, x, y + h - Math.Max(2, h / 10), w, Math.Max(2, h / 10));
                    break;
                }
            }
        }

        private static Font NumberFont(int h) => new Font("Segoe UI", h * 0.72f, FontStyle.Bold, GraphicsUnit.Pixel);
        private static Font HdFont(int h, bool bold) => new Font("Segoe UI", h * 0.72f, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);

        private static int HdWidth(Graphics g, int h, bool bold)
        {
            using var f = HdFont(h, bold);
            return TextRenderer.MeasureText(g, "HD", f, Size.Empty, TextFormatFlags.NoPadding).Width;
        }

        private static int BadgeWidth(Graphics g, int h)
        {
            using var f = new Font("Segoe UI", h * 0.46f, FontStyle.Bold, GraphicsUnit.Pixel);
            return TextRenderer.MeasureText(g, "HD", f, Size.Empty, TextFormatFlags.NoPadding).Width + h / 2;
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
