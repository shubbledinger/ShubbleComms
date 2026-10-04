using System;
using System.Drawing;
using System.Windows.Forms;

internal sealed class TypingOverlay : Form
{
    readonly KeyboardRouter _r;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    int _topTick;

    const int BandHeight = 48;    // the text line, at the bottom of the window
    const int PopupRoom = 80;     // transparent room above it for the suggestion popup

    static readonly Font TagFont = new("Consolas", 8.5f);

    const TextFormatFlags Tf =
        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

    public TypingOverlay(KeyboardRouter r)
    {
        _r = r;
        DoubleBuffered = true;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Opacity = 0.85;
        // everything not explicitly painted is see-through; the band and the
        // suggestion popup are painted islands floating over the game
        TransparencyKey = Color.FromArgb(255, 1, 0, 1);
        BackColor = TransparencyKey;
        Font = new Font("Consolas", 14f);

        var b = Screen.PrimaryScreen!.Bounds;
        Width = Math.Min(880, b.Width - 60);
        Height = BandHeight + PopupRoom;
        Left = b.Left + (b.Width - Width) / 2;
        Top = b.Top + (int)(b.Height * 0.74) - PopupRoom;   // band stays at the old position

        _timer.Tick += (s, e) => Tick();
        _timer.Start();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x00000020 | 0x00000080;
            return cp;
        }
    }

    void Tick()
    {
        bool on = _r.Mode == Mode.Typing;
        if (on != Visible)
        {
            Visible = on;
            _topTick = 0;
            if (on) ReassertTop();
        }
        if (!on) return;

        if (++_topTick >= 30) { _topTick = 0; ReassertTop(); }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;

        string full = _r.TypedText, unsp = _r.UnspokenText;
        int spokenLen = Math.Max(0, full.Length - unsp.Length);
        int len = full.Length;
        int ss = Math.Clamp(Math.Min(_r.SelStart, _r.SelEnd), spokenLen, len);
        int se = Math.Clamp(Math.Max(_r.SelStart, _r.SelEnd), spokenLen, len);
        int cur = Math.Clamp(_r.Cursor, spokenLen, len);
        bool hasSel = ss != se;

        string gray = full.Substring(0, spokenLen);
        string white = full.Substring(spokenLen);
        int wss = Math.Clamp(ss - spokenLen, 0, white.Length);
        int wse = Math.Clamp(se - spokenLen, 0, white.Length);
        int wcur = Math.Clamp(cur - spokenLen, 0, white.Length);

        string pre, block, rest;
        bool blockIsCaret;
        if (hasSel)
        {
            pre = white[..wss];
            block = white[wss..wse];
            rest = white[wse..];
            blockIsCaret = false;
        }
        else
        {
            pre = white[..wcur];
            block = wcur < white.Length ? white[wcur].ToString() : " ";
            rest = wcur < white.Length ? white[(wcur + 1)..] : "";
            blockIsCaret = true;
        }

        // pending Tab completion: the fragment is the user's text, the appended
        // letters came from the suggestion
        string? pendSuf = null;
        if (!hasSel && _r.PendingStart >= 0 && _r.SuggestionIndex >= 0)
        {
            int split = Math.Clamp(_r.PendingStart + _r.PendingLen - spokenLen, 0, wcur);
            if (split < wcur)
            {
                pendSuf = pre[split..];
                pre = pre[..split];
            }
        }

        int W(string s) => s.Length == 0 ? 0 :
            TextRenderer.MeasureText(g, s, Font, new Size(int.MaxValue, int.MaxValue), Tf).Width;

        var sugs = _r.Suggestions;
        bool popup = (sugs != null && sugs.Length > 0) || _r.AddWordHint != null;

        // the text band
        int bandY = PopupRoom;
        using (var fill = new SolidBrush(Theme.Bg))
            g.FillRectangle(fill, 0, bandY, Width, BandHeight);
        int y = bandY + Math.Max(0, (BandHeight - Font.Height) / 2);
        int x = 10;

        // mode indicator: the theme's PNG (stream = spoken tint, full = live
        // tint) or the text tag in the same two colors
        if (Theme.IndicatorStream != null && Theme.IndicatorFull != null)
        {
            var img = _r.StreamWords ? Theme.IndicatorStream : Theme.IndicatorFull;
            int ih = Font.Height + 4;
            int iw = (int)Math.Round((double)img.Width * ih / img.Height);
            int iy = bandY + Math.Max(0, (BandHeight - ih) / 2);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(img, x, iy, iw, ih);
            x += iw + 12;
        }
        else
        {
            string tag = _r.StreamWords ? "stream" : "full";
            Color tagColor = _r.StreamWords ? Theme.Spoken : Theme.Text;
            int tagW = TextRenderer.MeasureText(g, tag, TagFont, new Size(int.MaxValue, int.MaxValue), Tf).Width;
            TextRenderer.DrawText(g, tag, TagFont,
                new Rectangle(x, y + 5, tagW + 4, Font.Height), tagColor, Tf);
            x += tagW + 12;
        }

        // autocorrect flash: the corrected word lights up and fades (~350ms)
        if (_r.CorrectionStart >= 0)
        {
            int age = Environment.TickCount - _r.CorrectionTime;
            if (age >= 0 && age < 350)
            {
                int cs = Math.Clamp(_r.CorrectionStart, 0, full.Length);
                int cl = Math.Min(_r.CorrectionLen, full.Length - cs);
                if (cl > 0)
                {
                    int alpha = 255 - age * 255 / 350;
                    using var flash = new SolidBrush(Color.FromArgb(alpha, Theme.SelBg));
                    g.FillRectangle(flash, x + W(full[..cs]), y, W(full.Substring(cs, cl)), Font.Height);
                }
            }
        }

        int total = W(gray) + W(pre) + (pendSuf != null ? W(pendSuf) : 0) + W(block) + W(rest);
        int avail = ClientSize.Width - x - 90;
        if (total > avail && gray.Length + pre.Length + (blockIsCaret ? 0 : block.Length) + rest.Length > 1)
        {
            int cell = Math.Max(4, W(new string('M', 10)) / 10);
            int over = (total - avail + cell - 1) / cell;
            over = TrimLeft(ref gray, over);
            over = TrimLeft(ref pre, over);
            if (pendSuf != null) over = TrimLeft(ref pendSuf, over);
            if (!blockIsCaret) over = TrimLeft(ref block, over);
            TrimLeft(ref rest, over);
        }

        x = DrawSeg(g, gray, Theme.Spoken, null, x, y);
        x = DrawSeg(g, pre, Theme.Text, null, x, y);
        if (pendSuf != null)
        {
            // underline the appended letters — a shape, not a color, so it reads
            // in every theme (grayscale themes stay grayscale)
            using (var fill = new SolidBrush(Theme.Text))
                g.FillRectangle(fill, x, y + Font.Height - 2, W(pendSuf), 2);
            x = DrawSeg(g, pendSuf, Theme.Text, null, x, y);
        }
        int caretX = x;

        if (blockIsCaret)
        {
            int cellW = Math.Max(4, W(new string('M', 10)) / 10);
            using (var fill = new SolidBrush(Theme.CaretBg))
                g.FillRectangle(fill, x, y, cellW + 2, Font.Height);
            if (block != " ")
                TextRenderer.DrawText(g, block, Font,
                    new Rectangle(x, y, cellW + 4, Font.Height + 2), Theme.CaretFg, Tf);
            x += cellW + 2;
        }
        else
        {
            x = DrawSeg(g, block, Theme.Text, Theme.SelBg, x, y);
        }

        DrawSeg(g, rest, Theme.Text, null, x, y);

        // scanlines — CLIPPED to the band; lines over the transparent area
        // would become visible pixels
        if (Theme.Scanlines)
        {
            using var pen = new Pen(Color.FromArgb(80, 0, 0, 0), 1);
            for (int sy = bandY; sy < Height; sy += 3)
                g.DrawLine(pen, 0, sy, Width, sy);
        }

        // the suggestion popup, floating above the band at the caret
        if (popup) DrawPopup(g, sugs, _r.AddWordHint, caretX);
    }

    // a small bordered box above the text band, horizontally at the caret —
    // Minecraft / IntelliSense style. Selected entry (Tab pending) highlighted.
    void DrawPopup(Graphics g, string[]? sugs, string? hint, int caretX)
    {
        const int RowH = 18;

        int rows, w = 70;
        string[] items;
        if (sugs != null && sugs.Length > 0)
        {
            items = sugs;
            rows = sugs.Length;
            foreach (var s in sugs)
                w = Math.Max(w, TextRenderer.MeasureText(g, s, TagFont,
                    new Size(int.MaxValue, int.MaxValue), Tf).Width + 18);
        }
        else
        {
            items = Array.Empty<string>();
            rows = 1;
            if (hint != null)
                w = Math.Max(w, TextRenderer.MeasureText(g, hint, TagFont,
                    new Size(int.MaxValue, int.MaxValue), Tf).Width + 18);
        }
        w = Math.Min(w, ClientSize.Width - 16);

        int h = rows * RowH + 4;
        int maxX = Math.Max(8, ClientSize.Width - w - 8);
        int x = Math.Clamp(caretX - 6, 8, maxX);
        int top = PopupRoom - h - 6;

        using (var fill = new SolidBrush(Theme.Bg))
            g.FillRectangle(fill, x, top, w, h);
        using (var pen = new Pen(Theme.Spoken, 1f))
            g.DrawRectangle(pen, x + 0.5f, top + 0.5f, w - 1, h - 1);

        for (int i = 0; i < items.Length; i++)
        {
            int ry = top + 2 + i * RowH;
            if (i == _r.SuggestionIndex)
            {
                using var sel = new SolidBrush(Theme.SelBg);
                g.FillRectangle(sel, x + 2, ry, w - 4, RowH - 1);
            }
            TextRenderer.DrawText(g, items[i], TagFont,
                new Rectangle(x + 8, ry + 2, w - 12, RowH - 2),
                i == _r.SuggestionIndex ? Theme.Text : Theme.Spoken, Tf);
        }
        if (items.Length == 0 && hint != null)
            TextRenderer.DrawText(g, hint, TagFont,
                new Rectangle(x + 8, top + 3, w - 12, RowH - 2), Theme.Spoken, Tf);
    }

    int DrawSeg(Graphics g, string s, Color fg, Color? bg, int x, int y)
    {
        if (s.Length == 0) return x;
        int w = TextRenderer.MeasureText(g, s, Font, new Size(int.MaxValue, int.MaxValue), Tf).Width;
        if (bg != null)
        {
            using var fill = new SolidBrush(bg.Value);
            g.FillRectangle(fill, x, y, w + 2, Font.Height);
        }
        TextRenderer.DrawText(g, s, Font, new Rectangle(x, y, w + 4, Font.Height + 2), fg, Tf);
        return x + w;
    }

    void ReassertTop()
    {
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOMOVE | Native.SWP_NOACTIVATE);
    }

    static int TrimLeft(ref string s, int over)
    {
        if (over <= 0 || s.Length == 0) return over;
        if (over >= s.Length) { int n = s.Length; s = ""; return over - n; }
        s = "…" + s[over..];
        return 0;
    }
}