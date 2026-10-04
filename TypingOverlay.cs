using System;
using System.Drawing;
using System.Windows.Forms;

internal sealed class TypingOverlay : Form
{
    readonly KeyboardRouter _r;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    int _topTick;

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
        Font = new Font("Consolas", 14f);

        var b = Screen.PrimaryScreen!.Bounds;
        Width = Math.Min(880, b.Width - 60);
        Height = 48;
        Left = b.Left + (b.Width - Width) / 2;
        Top = b.Top + (int)(b.Height * 0.74);

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
        BackColor = Theme.Bg;

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

        int W(string s) => s.Length == 0 ? 0 :
            TextRenderer.MeasureText(g, s, Font, new Size(int.MaxValue, int.MaxValue), Tf).Width;

        int y = Math.Max(0, (ClientSize.Height - Font.Height) / 2);
        int x = 10;

        // mode indicator: the theme's PNG (stream mode = spoken-text tint, the
        // gray of words already spoken; full mode = live-text tint) or the
        // classic text tag. Drawn centered on the overlay — Tint already
        // cropped the artwork to its own bounds, so this centers the logo
        // itself, not the canvas it was exported on.
        if (Theme.IndicatorStream != null && Theme.IndicatorFull != null)
        {
            var img = _r.StreamWords ? Theme.IndicatorStream : Theme.IndicatorFull;
            int ih = Font.Height + 4;
            int iw = (int)Math.Round((double)img.Width * ih / img.Height);
            int iy = Math.Max(0, (ClientSize.Height - ih) / 2);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(img, x, iy, iw, ih);
            x += iw + 12;
        }
        else
        {
            string tag = _r.StreamWords ? "stream" : "full";
            Color tagColor = _r.StreamWords ? Theme.TagStream : Theme.TagFull;
            int tagW = TextRenderer.MeasureText(g, tag, TagFont, new Size(int.MaxValue, int.MaxValue), Tf).Width;
            TextRenderer.DrawText(g, tag, TagFont,
                new Rectangle(x, y + 5, tagW + 4, Font.Height), tagColor, Tf);
            x += tagW + 12;
        }

        int avail = ClientSize.Width - x - 90;
        int total = W(gray) + W(pre) + W(block) + W(rest);
        if (total > avail && gray.Length + pre.Length + (blockIsCaret ? 0 : block.Length) + rest.Length > 1)
        {
            int cell = Math.Max(4, W(new string('M', 10)) / 10);
            int over = (total - avail + cell - 1) / cell;
            over = TrimLeft(ref gray, over);
            over = TrimLeft(ref pre, over);
            if (!blockIsCaret) over = TrimLeft(ref block, over);
            TrimLeft(ref rest, over);
        }

        x = DrawSeg(g, gray, Theme.Spoken, null, x, y);
        x = DrawSeg(g, pre, Theme.Text, null, x, y);

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

        if (Theme.Scanlines)
        {
            using var pen = new Pen(Color.FromArgb(80, 0, 0, 0), 1);
            for (int sy = 0; sy < Height; sy += 3)
                g.DrawLine(pen, 0, sy, Width, sy);
        }
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