// uirender <outdir>: draws the now-playing strip in several states and sizes to PNG files.
using System.Drawing;
using System.Drawing.Imaging;
using SDRSharp.Common;
using SDRSharp.HDRadio;

namespace SDRSharp.Common
{
    // Stand-in for the few ISharpControl members the strip reads.
    public interface ISharpControl
    {
        bool ThemeIsDark { get; }
        Color ThemePanelColor { get; }
        Color ThemeForeColor { get; }
    }
}

// ControlPanel.LoadImage stand-in (the real ControlPanel needs all of SDR#).
namespace SDRSharp.HDRadio
{
    public static class HDRadioPlugin
    {
        public const string Name = "808 HD";
    }

    public static class ControlPanel
    {
        internal static Image LoadImage(byte[] bytes)
        {
            if (bytes == null) return null;
            using var ms = new MemoryStream(bytes);
            using var img = Image.FromStream(ms);
            return new Bitmap(img);
        }
    }
}

class DarkTheme : ISharpControl
{
    public bool ThemeIsDark => true;
    public Color ThemePanelColor => Color.FromArgb(30, 30, 30);
    public Color ThemeForeColor => Color.White;
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        string outDir = args[0];
        Directory.CreateDirectory(outDir);

        // Fake album art: a gradient square as PNG bytes.
        byte[] art;
        using (var bmp = new Bitmap(300, 300))
        {
            using (var g = Graphics.FromImage(bmp))
            using (var br = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, 300, 300), Color.Goldenrod, Color.SteelBlue, 45f))
                g.FillRectangle(br, 0, 0, 300, 300);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            art = ms.ToArray();
        }

        HdStatus Hd(bool synced, float mer)
        {
            var s = new HdStatus
            {
                Synced = synced, MerLower = mer, MerUpper = mer + 0.5f,
                StationName = "KBAY", Slogan = "Bay Country",
                Title = "Our Song", Artist = "Taylor Swift", AlbumArt = art,
                WeatherMap = art,
            };
            s.Programs[0] = "Country";
            s.Programs[1] = "Classic Rock";
            return s;
        }

        var cases = new (string name, int w, int h, BarInfo info)[]
        {
            ("hd-normal", 1500, 170, new BarInfo { Hd = Hd(true, 12), Program = 0, PlayingHd = true, State = "Playing HD audio", FrequencyHz = 98_500_000 }),
            ("hd-tall-panel", 1500, 390, new BarInfo { Hd = Hd(true, 12), Program = 0, PlayingHd = true, State = "Playing HD audio", FrequencyHz = 98_500_000 }),
            ("hd-weak-retrying", 1500, 110, new BarInfo { Hd = Hd(false, 3), Program = 0, PlayingHd = false, State = "HD signal weak, retrying in 4s", FrequencyHz = 98_500_000 }),
            ("hd-forced-analog", 1500, 110, new BarInfo { Hd = Hd(true, 11), Program = 1, PlayingHd = false, ForceAnalog = true, State = "Analog (HD available)", FrequencyHz = 98_500_000 }),
            ("hd-found-not-playing", 1500, 170, new BarInfo { Hd = new Func<HdStatus>(() => { var s = new HdStatus { Synced = true, MerLower = -1.8f, MerUpper = 0, StationName = "KDFC", Slogan = "Classical California" }; return s; })(), State = "HD found, waiting for audio...", RdsText = "Siegfried Idyll by Richard Wagner", FrequencyHz = 104_900_000 }),
            ("analog-rds", 1500, 110, new BarInfo { Hd = new HdStatus(), PlayingHd = false, State = "Analog station (no HD)", RdsName = "KFOG", RdsText = "Now playing: The Very Long Title Of A Song By An Artist With A Long Name", FrequencyHz = 104_500_000 }),
            ("long-text-narrow", 900, 110, new BarInfo
            {
                Hd = new Func<HdStatus>(() => { var s = Hd(true, 9); s.StationName = "WXYZ-HD Metropolitan Public Radio"; s.Slogan = "The very best of everything, all day long"; s.Title = "An Extremely Long Song Title (Extended Remastered Version)"; s.Artist = "Somebody Featuring Somebody Else"; return s; })(),
                Program = 0, PlayingHd = true, State = "Playing HD audio", FrequencyHz = 90_100_000,
            }),
        };

        foreach (var c in cases)
        {
            using var bar = new NowPlayingBar(new DarkTheme()) { Width = c.w, Height = c.h, Visible = true };
            bar.CreateControl();
            bar.UpdateInfo(c.info);
            using var bmp = new Bitmap(c.w, c.h);
            bar.DrawToBitmap(bmp, new Rectangle(0, 0, c.w, c.h));
            string path = Path.Combine(outDir, c.name + ".png");
            bmp.Save(path, ImageFormat.Png);
            Console.WriteLine(path);
        }

        // Brand mark previews: each style large on its own, then in place in a full strip.
        var styles = new[] { BrandMark.Style.Badge, BrandMark.Style.Waves, BrandMark.Style.Italic };
        var stripInfo = cases[0].info;
        for (int i = 0; i < styles.Length; i++)
        {
            NowPlayingBar.BrandStyle = styles[i];
            const int w = 1500, stripH = 120, bigH = 64, gap = 24;
            using var sheet = new Bitmap(w, 24 + bigH + gap + stripH);
            using (var g = Graphics.FromImage(sheet))
            {
                g.Clear(Color.FromArgb(24, 26, 30));
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                using var label = new Font("Segoe UI", 11f);
                g.DrawString($"Option {(char)('A' + i)}: {styles[i]}", label, Brushes.Gray, 12, 4);
                int bw = BrandMark.Measure(g, bigH, styles[i]);
                BrandMark.Draw(g, new Rectangle(24, 28, bw, bigH), styles[i], Color.FromArgb(235, 235, 235));
                using var bar = new NowPlayingBar(new DarkTheme()) { Width = w, Height = stripH, Visible = true };
                bar.CreateControl();
                bar.UpdateInfo(stripInfo);
                using var strip = new Bitmap(w, stripH);
                bar.DrawToBitmap(strip, new Rectangle(0, 0, w, stripH));
                g.DrawImage(strip, 0, 24 + bigH + gap);
            }
            string path = Path.Combine(outDir, $"brand-{(char)('a' + i)}-{styles[i].ToString().ToLower()}.png");
            sheet.Save(path, ImageFormat.Png);
            Console.WriteLine(path);
        }
    }
}
