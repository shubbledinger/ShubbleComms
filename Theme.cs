using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.Json;

// Themes are JSON files in %APPDATA%\ShubbleComms\themes\ — one file per
// theme, theme name = filename. The four built-ins are written on first run
// and only when missing, so editing them (or deleting one to reset it)
// behaves like any user file. Colors are "#RRGGBB" or "#AARRGGBB" — the
// radial colors genuinely need alpha.
//
// "indicator" replaces the stream/full text tag in the typing overlay:
//   "text"        — the classic "stream" / "full" label
//   "<file>.png"  — a PNG silhouette (any solid color works — the tint
//                   overwrites RGB and keeps alpha). Stream mode shows it in
//                   the spoken-text color, full mode in the live-text color.
//                   Looked up next to the theme file, then next to the exe.
// A missing or broken theme falls back to the Dark palette with a log line;
// the app never dies over a bad hand-edit.
internal static class Theme
{
    public static Color Bg, Text, Spoken, SelBg, CaretBg, CaretFg;
    public static Color RadWedge, RadSel, RadCenter, RadNeedle;
    public static bool Scanlines;

    public static string Current = "Dark";
    public static string[] Names { get; private set; } = new[] { "Dark" };

    // built by Apply when the theme has a PNG indicator. IndicatorStream is
    // shown in stream mode (spoken-text tint — words gray out as they're
    // spoken, and the logo matches them); IndicatorFull in full mode
    // (live-text tint — everything stays bright until Enter).
    public static Bitmap? IndicatorStream, IndicatorFull;

    static string _indicator = "text";

    public static string ThemesDir => Path.Combine(AppData.Folder, "themes");

    // write the built-ins once, then list every theme file in the folder
    public static void Init()
    {
        try
        {
            Directory.CreateDirectory(ThemesDir);
            WriteIfMissing("Dark.json", Dark);
            WriteIfMissing("Matrix.json", Matrix);
            WriteIfMissing("Portal.json", Portal);
            WriteIfMissing("Ice.json", Ice);
            Names = Directory.GetFiles(ThemesDir, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (Names.Length == 0) Names = new[] { "Dark" };
        }
        catch (Exception ex)
        {
            Log.Enqueue("themes init failed: " + ex.Message);
            Names = new[] { "Dark" };
        }
    }

    static void WriteIfMissing(string file, string json)
    {
        try
        {
            string path = Path.Combine(ThemesDir, file);
            if (!File.Exists(path)) File.WriteAllText(path, json);
        }
        catch { }
    }

    public static void Apply(string name)
    {
        Current = name;
        _indicator = "text";
        LoadJson(Dark);                       // defaults first…
        try
        {
            string path = Path.Combine(ThemesDir, name + ".json");
            if (File.Exists(path))
                LoadJson(File.ReadAllText(path));   // …then override with the file
            else
                Log.Enqueue($"theme '{name}' not found — using Dark");
        }
        catch (Exception ex)
        {
            _indicator = "text";
            LoadJson(Dark);
            Log.Enqueue($"theme '{name}' is broken ({ex.Message}) — using Dark");
        }
        LoadIndicator();
    }

    // unknown/missing fields keep their Dark defaults, so hand-edits can
    // omit anything they don't care about
    static void LoadJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        Bg = Field(r, "bg", Bg);
        Text = Field(r, "text", Text);
        Spoken = Field(r, "spoken", Spoken);
        SelBg = Field(r, "selBg", SelBg);
        CaretBg = Field(r, "caretBg", CaretBg);
        CaretFg = Field(r, "caretFg", CaretFg);
        RadWedge = Field(r, "radWedge", RadWedge);
        RadSel = Field(r, "radSel", RadSel);
        RadCenter = Field(r, "radCenter", RadCenter);
        RadNeedle = Field(r, "radNeedle", RadNeedle);
        Scanlines = Field(r, "scanlines", Scanlines);
        if (r.TryGetProperty("indicator", out var ind) && ind.ValueKind == JsonValueKind.String)
            _indicator = ind.GetString() ?? "text";
    }

    static Color Field(JsonElement r, string name, Color fallback)
    {
        if (!r.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
            return fallback;
        return ParseColor(v.GetString()!);
    }

    static bool Field(JsonElement r, string name, bool fallback)
    {
        if (!r.TryGetProperty(name, out var v) ||
            (v.ValueKind != JsonValueKind.True && v.ValueKind != JsonValueKind.False))
            return fallback;
        return v.GetBoolean();
    }

    // "#RRGGBB" or "#AARRGGBB"
    static Color ParseColor(string s)
    {
        s = s.Trim().TrimStart('#');
        if (s.Length == 6) s = "FF" + s;
        if (s.Length != 8) throw new FormatException("bad color '" + s + "'");
        return Color.FromArgb(
            Convert.ToInt32(s[..2], 16),
            Convert.ToInt32(s.Substring(2, 2), 16),
            Convert.ToInt32(s.Substring(4, 2), 16),
            Convert.ToInt32(s.Substring(6, 2), 16));
    }

    static void LoadIndicator()
    {
        IndicatorStream?.Dispose(); IndicatorStream = null;
        IndicatorFull?.Dispose(); IndicatorFull = null;

        if (string.IsNullOrWhiteSpace(_indicator) || _indicator == "text") return;

        string? path = null;
        try
        {
            string inThemes = Path.Combine(ThemesDir, _indicator);
            string nextToExe = Path.Combine(AppContext.BaseDirectory, _indicator);
            if (File.Exists(inThemes)) path = inThemes;
            else if (File.Exists(nextToExe)) path = nextToExe;
        }
        catch { }
        if (path == null)
        {
            Log.Enqueue($"theme {Current}: indicator image '{_indicator}' not found — using the text tag");
            return;
        }

        try
        {
            using var src = Image.FromFile(path);
            Diagnose(src);
            IndicatorStream = Tint(src, Spoken);   // stream mode: the gray of spoken words
            IndicatorFull = Tint(src, Text);      // full mode: the live text color
        }
        catch (Exception ex)
        {
            Log.Enqueue($"theme {Current}: indicator image failed to load ({ex.Message})");
        }
    }

    // Direct pixel-level tint: every pixel's RGB is overwritten with the target
    // color, its alpha is kept, and the result is cropped to the artwork's own
    // bounds — a logo exported with transparent padding then centers and
    // scales as the logo, not as the canvas it happened to sit on.
    // Replaces the ColorMatrix/ImageAttributes approach — that path silently
    // fails in several documented GDI+ ways, while writing the bytes ourselves
    // cannot. Works for black- or white-on-transparent artwork alike: the shape
    // lives in the alpha channel, the color comes from here.
    static Bitmap Tint(Image src, Color c)
    {
        // normalize whatever format the PNG was saved in into 32bpp straight alpha
        var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height),
                new Rectangle(0, 0, src.Width, src.Height), GraphicsUnit.Pixel);
        }

        // tint + find the artwork's bounds in one pass
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                byte* scan = (byte*)data.Scan0;
                for (int y = 0; y < data.Height; y++)
                {
                    byte* row = scan + y * data.Stride;
                    for (int x = 0; x < data.Width; x++)
                    {
                        row[x * 4 + 0] = c.B;
                        row[x * 4 + 1] = c.G;
                        row[x * 4 + 2] = c.R;
                        if (row[x * 4 + 3] != 0)          // alpha — the logo shape
                        {
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            if (y > maxY) maxY = y;
                        }
                    }
                }
            }
        }
        finally { bmp.UnlockBits(data); }

        // crop away transparent margins (no-op when the artwork fills the canvas)
        if (maxX >= 0 && maxY >= 0)
        {
            var region = new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
            if (region.Width < bmp.Width || region.Height < bmp.Height)
            {
                try
                {
                    var cropped = bmp.Clone(region, PixelFormat.Format32bppArgb);
                    bmp.Dispose();
                    return cropped;
                }
                catch { }
            }
        }
        return bmp;
    }

    // one-shot ground truth about what GDI+ actually loaded from the PNG —
    // written to indicator-debug.txt next to the exe (and the log ring) so a
    // misbehaving indicator can be diagnosed without guessing
    static void Diagnose(Image src)
    {
        try
        {
            using var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height),
                    new Rectangle(0, 0, src.Width, src.Height), GraphicsUnit.Pixel);
            }

            int opaque = 0, semi = 0, clear = 0, sr = -1, sg = 0, sb = 0;
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                unsafe
                {
                    byte* scan = (byte*)data.Scan0;
                    for (int y = 0; y < data.Height; y++)
                    {
                        byte* row = scan + y * data.Stride;
                        for (int x = 0; x < data.Width; x++)
                        {
                            int a = row[x * 4 + 3];
                            if (a == 255) opaque++;
                            else if (a == 0) clear++;
                            else semi++;
                            if (a > 128 && sr < 0)
                            {
                                sb = row[x * 4]; sg = row[x * 4 + 1]; sr = row[x * 4 + 2];
                            }
                        }
                    }
                }
            }
            finally { bmp.UnlockBits(data); }

            long total = (long)bmp.Width * bmp.Height;
            string line = $"indicator: {src.Width}x{src.Height} {src.PixelFormat}; " +
                          $"opaque {100L * opaque / total}%, semi {100L * semi / total}%, " +
                          $"transparent {100L * clear / total}%; first visible pixel RGB {sr},{sg},{sb}";
            Log.Enqueue(line);
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "indicator-debug.txt"), line); }
            catch { }
        }
        catch { }
    }

    // ---- the built-ins, as written to the themes folder on first run ----

    const string Dark = @"{
  ""bg"": ""#0A0A0A"",  ""text"": ""#FFFFFF"",  ""spoken"": ""#828282"",
  ""selBg"": ""#5F5F5F"", ""caretBg"": ""#FFFFFF"", ""caretFg"": ""#0A0A0A"",
  ""radWedge"": ""#AF2D2D2D"", ""radSel"": ""#D7737373"",
  ""radCenter"": ""#EB0E0E0E"", ""radNeedle"": ""#E6C8C8C8"",
  ""tagStream"": ""#6ED26E"", ""tagFull"": ""#EBAF5F"",
  ""scanlines"": false,
  ""indicator"": ""text""
}";

    const string Matrix = @"{
  ""bg"": ""#000A02"",  ""text"": ""#AAFFBE"",  ""spoken"": ""#3C8C4B"",
  ""selBg"": ""#145A23"", ""caretBg"": ""#AAFFBE"", ""caretFg"": ""#000C00"",
  ""radWedge"": ""#AF083C14"", ""radSel"": ""#D71E8C3C"",
  ""radCenter"": ""#EB041A09"", ""radNeedle"": ""#E68CFFAA"",
  ""tagStream"": ""#8CFFAA"", ""tagFull"": ""#FFDC78"",
  ""scanlines"": true,
  ""indicator"": ""text""
}";

    const string Portal = @"{
  ""bg"": ""#100A04"",  ""text"": ""#FFB052"",  ""spoken"": ""#96642A"",
  ""selBg"": ""#693E14"", ""caretBg"": ""#FFB052"", ""caretFg"": ""#100A04"",
  ""radWedge"": ""#AF48280A"", ""radSel"": ""#D7A04E16"",
  ""radCenter"": ""#EB160E06"", ""radNeedle"": ""#EBFFD678"",
  ""tagStream"": ""#FFCD82"", ""tagFull"": ""#FF7846"",
  ""scanlines"": true,
  ""indicator"": ""aperture.png""
}";

    const string Ice = @"{
  ""bg"": ""#060C14"",  ""text"": ""#D7EBFF"",  ""spoken"": ""#5F7DA0"",
  ""selBg"": ""#375A8C"", ""caretBg"": ""#D7EBFF"", ""caretFg"": ""#060C14"",
  ""radWedge"": ""#AF142D50"", ""radSel"": ""#D73C69AA"",
  ""radCenter"": ""#EB0A1626"", ""radNeedle"": ""#E6BEDCFF"",
  ""tagStream"": ""#A0E1FF"", ""tagFull"": ""#FFC88C"",
  ""scanlines"": false,
  ""indicator"": ""text""
}";
}