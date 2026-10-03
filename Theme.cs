using System;
using System.Drawing;

internal static class Theme
{
    public static Color Bg, Text, Spoken, SelBg, CaretBg, CaretFg;
    public static Color RadWedge, RadSel, RadCenter, RadNeedle;
    public static Color TagStream, TagFull;
    public static bool Scanlines;

    public static string Current = "Dark";
    public static readonly string[] Names = { "Dark", "Matrix", "Portal", "Ice" };

    public static void Apply(string name)
    {
        Current = name;
        Scanlines = false;
        switch (name)
        {
            case "Matrix":
                Bg = Color.FromArgb(0, 10, 2); Text = Color.FromArgb(170, 255, 190);
                Spoken = Color.FromArgb(60, 140, 75); SelBg = Color.FromArgb(20, 90, 35);
                CaretBg = Color.FromArgb(170, 255, 190); CaretFg = Color.FromArgb(0, 12, 0);
                RadWedge = Color.FromArgb(175, 8, 60, 20); RadSel = Color.FromArgb(215, 30, 140, 60);
                RadCenter = Color.FromArgb(235, 4, 26, 9); RadNeedle = Color.FromArgb(230, 140, 255, 170);
                TagStream = Color.FromArgb(140, 255, 170); TagFull = Color.FromArgb(255, 220, 120);
                Scanlines = true;
                break;
            case "Portal":   // the end-credits look: warm black, amber glow
                Bg = Color.FromArgb(16, 10, 4); Text = Color.FromArgb(255, 176, 82);
                Spoken = Color.FromArgb(150, 100, 42); SelBg = Color.FromArgb(105, 62, 20);
                CaretBg = Color.FromArgb(255, 176, 82); CaretFg = Color.FromArgb(16, 10, 4);
                RadWedge = Color.FromArgb(175, 72, 40, 10); RadSel = Color.FromArgb(215, 160, 78, 22);
                RadCenter = Color.FromArgb(235, 22, 14, 6); RadNeedle = Color.FromArgb(235, 255, 214, 120);
                TagStream = Color.FromArgb(255, 205, 130); TagFull = Color.FromArgb(255, 120, 70);
                Scanlines = true;
                break;
            case "Ice":
                Bg = Color.FromArgb(6, 12, 20); Text = Color.FromArgb(215, 235, 255);
                Spoken = Color.FromArgb(95, 125, 160); SelBg = Color.FromArgb(55, 90, 140);
                CaretBg = Color.FromArgb(215, 235, 255); CaretFg = Color.FromArgb(6, 12, 20);
                RadWedge = Color.FromArgb(175, 20, 45, 80); RadSel = Color.FromArgb(215, 60, 105, 170);
                RadCenter = Color.FromArgb(235, 10, 22, 38); RadNeedle = Color.FromArgb(230, 190, 220, 255);
                TagStream = Color.FromArgb(160, 225, 255); TagFull = Color.FromArgb(255, 200, 140);
                break;
            default:
                Current = "Dark";
                Bg = Color.FromArgb(10, 10, 10); Text = Color.White;
                Spoken = Color.FromArgb(130, 130, 130); SelBg = Color.FromArgb(95, 95, 95);
                CaretBg = Color.White; CaretFg = Color.FromArgb(10, 10, 10);
                RadWedge = Color.FromArgb(175, 45, 45, 45); RadSel = Color.FromArgb(215, 115, 115, 115);
                RadCenter = Color.FromArgb(235, 14, 14, 14); RadNeedle = Color.FromArgb(230, 200, 200, 200);
                TagStream = Color.FromArgb(110, 210, 110); TagFull = Color.FromArgb(235, 175, 95);
                break;
        }
    }
}