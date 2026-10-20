using System.Drawing.Drawing2D;

namespace PCLMCDL.Ui;

/// <summary>配色与字体。</summary>
public static class Theme
{
    /// <summary>系统 DPI 缩放系数（96 DPI = 1.0）。</summary>
    public static readonly float DpiScale;

    static Theme()
    {
        try
        {
            using var g = Graphics.FromHwnd(IntPtr.Zero);
            DpiScale = Math.Clamp(g.DpiX / 96f, 1f, 4f);
        }
        catch
        {
            DpiScale = 1f;
        }
    }

    /// <summary>把 96 DPI 下的设计值换算成当前 DPI 的像素。</summary>
    public static int S(int v) => (int)Math.Round(v * DpiScale);

    public static Size Sz(int w, int h) => new(S(w), S(h));

    public static Padding Pad(int all) => new(S(all));
    public static Padding Pad(int l, int t, int r, int b) => new(S(l), S(t), S(r), S(b));

    public static readonly Color Primary = Color.FromArgb(45, 110, 222);
    public static readonly Color PrimaryDark = Color.FromArgb(34, 92, 194);
    public static readonly Color PrimaryLight = Color.FromArgb(232, 241, 254);
    public static readonly Color TitleBar = Color.FromArgb(43, 105, 214);
    public static readonly Color TitleBarHover = Color.FromArgb(60, 124, 232);
    public static readonly Color Bg = Color.FromArgb(237, 244, 252);
    public static readonly Color Card = Color.White;
    public static readonly Color Border = Color.FromArgb(214, 227, 245);
    public static readonly Color TextPrimary = Color.FromArgb(32, 40, 52);
    public static readonly Color TextSecondary = Color.FromArgb(114, 126, 145);
    public static readonly Color Success = Color.FromArgb(40, 155, 96);
    public static readonly Color Danger = Color.FromArgb(228, 72, 68);
    public static readonly Color CloseHover = Color.FromArgb(224, 68, 62);

    public const string Family = "Microsoft YaHei UI";

    public static Font F(float size, FontStyle style = FontStyle.Regular) => new(Family, size, style);

    public static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        if (radius <= 0)
        {
            p.AddRectangle(r);
            return p;
        }
        var d = radius * 2;
        if (r.Width < d || r.Height < d)
        {
            p.AddRectangle(r);
            return p;
        }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
