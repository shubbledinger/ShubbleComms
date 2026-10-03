using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

// one wheel entry: either a spoken phrase (File == null) or a sound file
internal readonly record struct Slot(string Label, string? File);

// The radial soundboard overlay: hold the radial bind (default X2) and steer.
// Renders with a per-pixel-alpha layered window drawn through a DIB section,
// keeps itself topmost, locks the game camera while open (the router counter-
// moves the physical mouse), and re-reads soundboard.txt whenever it changes.
internal sealed class RadialOverlay : Form
{
    readonly KeyboardRouter _router;
    readonly ITts _tts;
    readonly SoundFiles _sounds;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    int _topTick;
    bool _wasOpen;
    DateTime _stamp;
    int _presetIdx;
    List<(string Name, List<Slot> Slots)> _presets = new();

    double _dirX, _dirY, _lastVX, _lastVY;   // smoothed steering direction
    double _selAngle;
    bool _hasSel;

    int Radius, Deadzone;
    static readonly Font LabelFont = new("Consolas", 13f);
    static readonly Font CenterFont = new("Consolas", 14f, FontStyle.Bold);

    // 32bpp DIB rendered with GDI+ and pushed via UpdateLayeredWindow
    IntPtr _dib, _mem, _old, _bits;
    Native.BITMAPINFOHEADER _bmi;

    static readonly string[] AudioExts = { ".wav", ".mp3", ".m4a", ".aac", ".wma", ".aif", ".aiff" };

    public static string BoardPath => Path.Combine(AppData.Folder, "soundboard.txt");

    public RadialOverlay(KeyboardRouter router, ITts tts, AudioOut audio)
    {
        _router = router; _tts = tts;
        _sounds = new SoundFiles(audio);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;

        var b = Screen.PrimaryScreen!.Bounds;
        Radius = Math.Min(300, Math.Min(b.Width, b.Height) / 2 - 80);
        Deadzone = Math.Max(36, Radius / 7);
        Width = Height = 2 * (Radius + 30);

        _bmi = new Native.BITMAPINFOHEADER
        {
            biSize = 40,
            biWidth = (uint)Width,
            biHeight = unchecked((uint)-Height),   // top-down
            biPlanes = 1,
            biBitCount = 32
        };
        _dib = Native.CreateDIBSection(IntPtr.Zero, ref _bmi, 0, out _bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero)
            Log.Enqueue("radial: DIB section creation failed — wheel rendering disabled");
        _mem = Native.CreateCompatibleDC(IntPtr.Zero);
        _old = Native.SelectObject(_mem, _dib);

        ReloadIfChanged();

        _timer.Tick += (s, e) => Tick();
        _timer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_old != IntPtr.Zero) Native.SelectObject(_mem, _old);
            if (_mem != IntPtr.Zero) Native.DeleteDC(_mem);
            if (_dib != IntPtr.Zero) Native.DeleteObject(_dib);
        }
        base.Dispose(disposing);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // topmost, no taskbar, click-through-safe layering, always-on-top toolwindow
            cp.ExStyle |= 0x08000000 | 0x00000020 | 0x00000080 | 0x00080000;
            return cp;
        }
    }

    List<Slot> CurrentSlots =>
        _presets.Count > 0 ? _presets[Math.Clamp(_presetIdx, 0, _presets.Count - 1)].Slots : new List<Slot>();

    void Tick()
    {
        ReloadIfChanged();

        bool open = _router.RadialOpen;

        if (!_wasOpen && open)
        {
            var sb = Screen.PrimaryScreen!.Bounds;
            Left = Math.Clamp(_router.RadialAnchorX - Width / 2, sb.Left, sb.Right - Width);
            Top = Math.Clamp(_router.RadialAnchorY - Height / 2, sb.Top, sb.Bottom - Height);
            _topTick = 0;
            _dirX = _dirY = 0; _lastVX = _lastVY = 0;
            _hasSel = false;
            _router.RadialFireRequest = false;
        }

        // toggle mode: LMB fires the hovered wedge while the wheel stays open
        if (open && _router.RadialFireRequest)
        {
            _router.RadialFireRequest = false;
            var slots = CurrentSlots;
            int idx = SelectedWedge(slots.Count);
            if (idx >= 0 && idx < slots.Count) Fire(slots[idx]);
        }

        if (open)
        {
            // steering: smoothed accumulated virtual-cursor movement
            double dx = _router.RadialVirtX - _lastVX;
            double dy = _router.RadialVirtY - _lastVY;
            _lastVX = _router.RadialVirtX; _lastVY = _router.RadialVirtY;
            _dirX = _dirX * 0.72 + dx;
            _dirY = _dirY * 0.72 + dy;
            if (_dirX * _dirX + _dirY * _dirY > 9.0)
            {
                _selAngle = Math.Atan2(_dirY, _dirX);
                _hasSel = true;
            }
        }

        // wheel notches switch presets
        int notches = Interlocked.Exchange(ref _router.RadialWheelNotches, 0);
        if (notches != 0 && open && _presets.Count > 0)
            _presetIdx = ((_presetIdx + notches) % _presets.Count + _presets.Count) % _presets.Count;

        // release of the trigger (hold mode) fires the selected wedge
        if (_wasOpen && !open)
        {
            if (!_router.RadialCancelled)
            {
                var slots = CurrentSlots;
                int idx = SelectedWedge(slots.Count);
                if (idx >= 0 && idx < slots.Count) Fire(slots[idx]);
            }
        }
        _wasOpen = open;

        if (Visible != open)
        {
            Visible = open;
            if (open) ReassertTop();
        }
        if (!open) return;

        // topmost is a band, not a throne — re-assert periodically
        if (++_topTick >= 30) { _topTick = 0; ReassertTop(); }

        Render();
    }

    void Fire(Slot slot)
    {
        if (slot.File != null) _sounds.Play(slot.File);
        else _tts.Speak(slot.Label);
    }

    // wedge index for the current steering angle; -1 = no selection.
    // 12 slots = clock face (12 at top), which is why the math starts at -90°.
    int SelectedWedge(int n)
    {
        if (n <= 0 || !_hasSel) return -1;
        double sweep = 360.0 / n;
        double deg = _selAngle * 180.0 / Math.PI;
        double rel = ((deg + 90.0) % 360.0 + 360.0) % 360.0 + sweep / 2.0;
        return (int)(rel / sweep) % n;
    }

    void Render()
    {
        if (_dib == IntPtr.Zero || _bits == IntPtr.Zero) return;

        using (var g = Graphics.FromHdc(_mem))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.FromArgb(0, 0, 0, 0));

            int cx = Width / 2, cy = Height / 2;
            var slots = CurrentSlots;
            int n = slots.Count;
            int sel = SelectedWedge(n);

            if (n > 0)
            {
                double sweep = 360.0 / n;
                var pie = new RectangleF(cx - Radius, cy - Radius, 2 * Radius, 2 * Radius);

                for (int i = 0; i < n; i++)
                {
                    using var b = new SolidBrush(i == sel ? Theme.RadSel : Theme.RadWedge);
                    g.FillPie(b, pie, (float)(-90 - sweep / 2 + i * sweep), (float)sweep);
                }
                using (var p = new Pen(Theme.Bg, 3f)) g.DrawEllipse(p, pie);

                // crisp wedge separators from the deadzone ring out to the rim
                using (var sp = new Pen(Theme.Bg, 3f))
                {
                    for (int i = 0; i < n; i++)
                    {
                        double a = (-90.0 - sweep / 2 + i * sweep) * Math.PI / 180.0;
                        float x1 = cx + (float)Math.Cos(a) * (Deadzone + 4);
                        float y1 = cy + (float)Math.Sin(a) * (Deadzone + 4);
                        float x2 = cx + (float)Math.Cos(a) * (Radius - 2);
                        float y2 = cy + (float)Math.Sin(a) * (Radius - 2);
                        g.DrawLine(sp, x1, y1, x2, y2);
                    }
                }

                for (int i = 0; i < n; i++)
                {
                    double rad = (-90.0 - sweep / 2 + (i + 0.5) * sweep) * Math.PI / 180.0;
                    float lx = cx + (float)Math.Cos(rad) * Radius * 0.68f;
                    float ly = cy + (float)Math.Sin(rad) * Radius * 0.68f;
                    string label = Trunc(slots[i].Label, 20);
                    var sz = g.MeasureString(label, LabelFont);
                    using var b = new SolidBrush(Theme.Text);
                    g.DrawString(label, LabelFont, b, lx - sz.Width / 2f, ly - sz.Height / 2f);
                }

                if (_hasSel)
                {
                    float nx = cx + (float)Math.Cos(_selAngle) * (Deadzone + 50);
                    float ny = cy + (float)Math.Sin(_selAngle) * (Deadzone + 50);
                    using var p = new Pen(Theme.RadNeedle, 4f);
                    g.DrawLine(p, cx, cy, nx, ny);
                }
            }

            using (var b = new SolidBrush(Theme.RadCenter))
                g.FillEllipse(b, cx - Deadzone, cy - Deadzone, 2 * Deadzone, 2 * Deadzone);
            using (var p = new Pen(Theme.RadWedge, 2f))
                g.DrawEllipse(p, cx - Deadzone, cy - Deadzone, 2 * Deadzone, 2 * Deadzone);

            string center = _presets.Count == 0 ? "no presets"
                : $"{Trunc(_presets[Math.Clamp(_presetIdx, 0, _presets.Count - 1)].Name, 16)}\n{_presetIdx + 1}/{_presets.Count}";
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var tb = new SolidBrush(Theme.Text);
            g.DrawString(center, CenterFont, tb, new RectangleF(cx - 90, cy - 36, 180, 72), fmt);

            // scanlines — CLIPPED to the wheel, or lines blended into the
            // transparent corners would become visible pixels
            if (Theme.Scanlines)
            {
                using (var clip = new GraphicsPath())
                {
                    clip.AddEllipse(cx - Radius, cy - Radius, 2 * Radius, 2 * Radius);
                    g.SetClip(clip);
                    using var pen = new Pen(Color.FromArgb(70, 0, 0, 0), 1);
                    for (int sy = 0; sy < Height; sy += 3)
                        g.DrawLine(pen, 0, sy, Width, sy);
                }
                g.ResetClip();
            }

            g.Flush();
        }

        // alpha channel must be premultiplied for UpdateLayeredWindow
        PremultiplyBits(_bits, Width, Height);

        IntPtr screen = Native.GetDC(IntPtr.Zero);
        var pos = new Native.POINT { x = Left, y = Top };
        var size = new Native.SIZE { cx = Width, cy = Height };
        var src = new Native.POINT { x = 0, y = 0 };
        var blend = new Native.BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
        Native.UpdateLayeredWindow(Handle, screen, ref pos, ref size, _mem, ref src, 0, ref blend, 2);
        Native.ReleaseDC(IntPtr.Zero, screen);
    }

    static unsafe void PremultiplyBits(IntPtr bits, int w, int h)
    {
        byte* p = (byte*)bits;
        int end = w * h * 4;
        for (int i = 0; i < end; i += 4)
        {
            byte a = p[i + 3];
            if (a == 255) continue;
            p[i] = (byte)(p[i] * a / 255);
            p[i + 1] = (byte)(p[i + 1] * a / 255);
            p[i + 2] = (byte)(p[i + 2] * a / 255);
        }
    }

    static string Trunc(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    // ---- soundboard file ----

    // slot parsing: "@path" = sound file, anything else = spoken phrase. A bare
    // line with an audio extension that exists as a file also plays.
    static Slot? ParseSlot(string line)
    {
        if (line.StartsWith('@'))
        {
            string spec = line[1..].Trim();
            string path = ResolveSoundPath(spec);
            if (File.Exists(path)) return new Slot(Path.GetFileNameWithoutExtension(spec), path);
            Log.Enqueue($"soundboard: file not found: {spec}");
            return null;
        }
        string ext = Path.GetExtension(line).ToLowerInvariant();
        if (AudioExts.Contains(ext))
        {
            string path = ResolveSoundPath(line);
            if (File.Exists(path)) return new Slot(Path.GetFileNameWithoutExtension(line), path);
            Log.Enqueue($"soundboard: \"{line}\" looks like a sound file but wasn't found — spoken instead");
        }
        return new Slot(line, null);
    }

    // ~ = user folder, rooted = as-is, relative = next to soundboard.txt
    static string ResolveSoundPath(string spec)
    {
        if (spec.StartsWith('~'))
            spec = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                spec[1..].TrimStart('\\', '/'));
        if (Path.IsPathRooted(spec)) return spec;
        return Path.Combine(Path.GetDirectoryName(BoardPath)!, spec);
    }

    void ReloadIfChanged()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BoardPath)!);
            if (!File.Exists(BoardPath)) File.WriteAllText(BoardPath, DefaultBoard);

            var fi = new FileInfo(BoardPath);
            if (fi.LastWriteTime == _stamp) return;
            _stamp = fi.LastWriteTime;

            // [Name] starts a preset; every other line is one slot
            var presets = new List<(string, List<Slot>)>();
            foreach (var raw in File.ReadAllLines(fi.FullName))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    presets.Add((line[1..^1], new List<Slot>()));
                    continue;
                }
                if (presets.Count > 0 && ParseSlot(line) is { } slot)
                    presets[^1].Item2.Add(slot);
            }
            if (presets.Count > 0)
            {
                _presets = presets;
                _presetIdx = Math.Clamp(_presetIdx, 0, presets.Count - 1);
                // pre-render everything so first fire is instant
                foreach (var p in presets)
                    foreach (var slot in p.Item2)
                    {
                        if (slot.File != null) _sounds.Preload(slot.File);
                        else _tts.Preload(slot.Label);
                    }
                Log.Enqueue($"soundboard: {presets.Count} presets, {presets.Sum(p => p.Item2.Count)} slots loaded");
            }
        }
        catch (Exception ex) { Log.Enqueue("soundboard error: " + ex.Message); }
    }

    void ReassertTop()
    {
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOMOVE | Native.SWP_NOACTIVATE);
    }

    const string DefaultBoard = @"# Radial soundboard — [Name] starts a preset, one entry per line.
# Plain lines are spoken through the TTS (abbreviations apply, pre-rendered, instant).
# SOUND FILES: prefix the path with @
#   @airhorn.wav              (relative to this file — 'open config folder' gets you there)
#   @sounds\bruh.mp3          (subfolders fine)
#   @C:\absolute\path.wav
#   @~\Music\ding.wav         (~ = your user folder)
#   wav / mp3 / m4a / aac / wma / aif — a bare path (no @) also plays if the file exists
# Files route to the same outputs as the voice (tick them in settings).
# TYPING A PHRASE IN ALL CAPS MAKES IT SHOUT — higher pitch, louder.
# Live-reloads when you save. Keep to 8 or fewer slots for readable wedges.
# A 12-slot preset = clock face (12 centered at top) for bearings.

[Infantry]
Contact, north!
Contact, east!
Contact, south!
Contact, west!
Reloading!
Man down!
Need ammo!
Move up!

[Crew]
Driver, advance!
Driver, stop!
Driver, reverse!
Gunner, traverse left!
Gunner, traverse right!
Load AP!
Load HE!
Bail out!

[Quick]
Say again?
Hold fire!
Taking fire!
Frag out!
";
}