using System.Drawing.Drawing2D;

namespace PCLMCDL.Ui;

/// <summary>自绘圆角按钮。</summary>
public sealed class RoundButton : Control
{
    public int Radius { get; set; } = 8;
    public Color Fill { get; set; } = Theme.Primary;
    public Color FillHover { get; set; } = Theme.PrimaryDark;
    public Color FillDown { get; set; } = Color.FromArgb(28, 78, 168);
    public Color BorderColor { get; set; } = Color.Empty;
    public Color TextColor { get; set; } = Color.White;

    bool _hover, _down;
    GraphicsPath? _path;
    Size _pathSize;
    int _pathRadius = -1;

    public RoundButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        // 用父背景色而非真正透明，避免滚动时整条父链重绘
        BackColor = Theme.Card;
        Cursor = Cursors.Hand;
        Font = Theme.F(9.5f, FontStyle.Bold);
        Padding = Theme.Pad(20, 8, 20, 8);
    }

    GraphicsPath PathFor(Rectangle r)
    {
        if (_path is null || _pathSize != r.Size || _pathRadius != Radius)
        {
            _path?.Dispose();
            _path = Theme.RoundRect(r, Radius);
            _pathSize = r.Size;
            _pathRadius = Radius;
        }
        return _path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _path?.Dispose();
        base.Dispose(disposing);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var sz = TextRenderer.MeasureText(Text, Font);
        return new Size(sz.Width + Padding.Horizontal, sz.Height + Padding.Vertical);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var r = ClientRectangle;
        r.Width -= 1;
        r.Height -= 1;
        if (r.Width <= 0 || r.Height <= 0) return;

        var fill = !Enabled ? Color.FromArgb(206, 214, 226)
                 : _down ? FillDown
                 : _hover ? FillHover
                 : Fill;

        var path = PathFor(r);
        using (var brush = new SolidBrush(fill))
            g.FillPath(brush, path);

        if (BorderColor != Color.Empty)
        {
            using var pen = new Pen(BorderColor);
            g.DrawPath(pen, path);
        }

        var color = Enabled ? TextColor : Color.FromArgb(150, 160, 176);
        TextRenderer.DrawText(g, Text, Font, r, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>标题栏上的窗口控制按钮（─ □ ✕）。</summary>
public sealed class WindowButton : Control
{
    public enum Kind { Minimize, Maximize, Close }

    public Kind Style { get; set; } = Kind.Minimize;

    bool _hover;

    public WindowButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Theme.TitleBar;
        Cursor = Cursors.Hand;
        Size = Theme.Sz(44, 30);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (_hover)
        {
            var bg = Style == Kind.Close ? Theme.CloseHover : Theme.TitleBarHover;
            using var b = new SolidBrush(bg);
            g.FillRectangle(b, ClientRectangle);
        }

        using var pen = new Pen(Color.White, 1.8f);
        var cx = Width / 2f;
        var cy = Height / 2f;
        const float s = 7f;

        switch (Style)
        {
            case Kind.Minimize:
                g.DrawLine(pen, cx - s, cy + 1, cx + s, cy + 1);
                break;
            case Kind.Maximize:
                g.DrawRectangle(pen, cx - s, cy - s, s * 2, s * 2);
                break;
            default:
                g.DrawLine(pen, cx - s, cy - s, cx + s, cy + s);
                g.DrawLine(pen, cx + s, cy - s, cx - s, cy + s);
                break;
        }
    }
}

/// <summary>标题栏导航项（下载 / 设置）。</summary>
public sealed class NavButton : Control
{
    bool _hover;

    public bool Active { get; set; }

    public NavButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Theme.TitleBar;
        Cursor = Cursors.Hand;
        Font = Theme.F(10f, FontStyle.Bold);
        Size = Theme.Sz(88, 34);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnClick(EventArgs e) { base.OnClick(e); }

    public void SetActive(bool active)
    {
        Active = active;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var r = ClientRectangle;
        r.Width -= 1;
        r.Height -= 1;

        if (Active || _hover)
        {
            using var path = Theme.RoundRect(r, 8);
            using var b = new SolidBrush(Color.FromArgb(Active ? 60 : 34, 255, 255, 255));
            g.FillPath(b, path);
        }

        TextRenderer.DrawText(g, Text, Font, r, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>圆角白色卡片：标题与内容都由内部 TableLayoutPanel 排布。</summary>
public sealed class Card : Panel
{
    public int Radius { get; set; } = 10;
    public TableLayoutPanel Body { get; }

    public Card() : this(null) { }

    public Card(string? header)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Theme.Bg;
        Padding = Theme.Pad(20, 18, 20, 22);

        Body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
            Padding = new Padding(0),
            BackColor = Theme.Card,
        };
        Body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Body.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        if (!string.IsNullOrEmpty(header))
        {
            Body.RowCount = 2;
            Body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Body.Controls.Add(new Label
            {
                Text = header,
                AutoSize = true,
                Font = Theme.F(10f, FontStyle.Bold),
                ForeColor = Theme.TextPrimary,
                BackColor = Theme.Card,
                Margin = Theme.Pad(0, 0, 0, 14),
            }, 0, 0);
        }

        Controls.Add(Body);
    }

    /// <summary>把内容放进最后一行。</summary>
    public void AddContent(Control content) => Body.Controls.Add(content, 0, Body.RowCount - 1);

    /// <summary>在标题行右侧追加控件（如小按钮），仅对有标题的卡片有效。</summary>
    public void AddHeaderRight(params Control[] controls)
    {
        if (Body.RowCount < 2) return;
        var title = Body.GetControlFromPosition(0, 0);
        if (title is null) return;

        Body.SuspendLayout();
        Body.Controls.Remove(title);

        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 10),
            BackColor = Theme.Card,
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        title.Margin = new Padding(0);
        title.Anchor = AnchorStyles.Left;
        row.Controls.Add(title, 0, 0);

        var right = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0),
            Anchor = AnchorStyles.Right,
            BackColor = Theme.Card,
        };
        right.Controls.AddRange(controls);
        row.Controls.Add(right, 1, 0);

        Body.Controls.Add(row, 0, 0);
        Body.ResumeLayout(true);
    }

    // 缓存圆角路径，避免每次重绘都重建
    GraphicsPath? _path;
    Size _pathSize;
    int _pathRadius = -1;
    Bitmap? _cache;

    GraphicsPath PathFor(Rectangle r)
    {
        if (_path is null || _pathSize != r.Size || _pathRadius != Radius)
        {
            _path?.Dispose();
            _path = Theme.RoundRect(r, Radius);
            _pathSize = r.Size;
            _pathRadius = Radius;
        }
        return _path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _path?.Dispose();
            _cache?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>把圆角白底整张画到位图缓存，滚动时只做一次贴图。</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 0 || Height <= 0) return;

        if (_cache is null || _cache.Width != Width || _cache.Height != Height)
        {
            _cache?.Dispose();
            _cache = new Bitmap(Width, Height);
            using var g = Graphics.FromImage(_cache);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(Theme.Bg))
                g.FillRectangle(bg, 0, 0, Width, Height);

            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            var path = PathFor(r);
            using (var wb = new SolidBrush(Theme.Card))
                g.FillPath(wb, path);
            using (var pen = new Pen(Theme.Border))
                g.DrawPath(pen, path);
        }

        e.Graphics.DrawImageUnscaled(_cache, 0, 0);
    }
}

/// <summary>带缓动的滚动面板：滚轮不再一格一格跳，而是平滑滑过去。</summary>
public sealed class SmoothScrollPanel : Panel
{
    readonly System.Windows.Forms.Timer _anim = new() { Interval = 16 };
    float _current;
    float _target;

    public SmoothScrollPanel()
    {
        AutoScroll = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        _anim.Tick += (_, _) => Animate();
    }

    int MaxScroll
    {
        get
        {
            var max = VerticalScroll.Maximum - VerticalScroll.LargeChange + 1;
            return Math.Max(0, max);
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (!VerticalScroll.Visible || e.Delta == 0)
        {
            base.OnMouseWheel(e);
            return;
        }

        var step = Theme.S(72) * (e.Delta / 120f);
        if (_anim.Enabled)
        {
            _target = Math.Clamp(_target - step, 0, MaxScroll);
        }
        else
        {
            _current = -AutoScrollPosition.Y;
            _target = Math.Clamp(_current - step, 0, MaxScroll);
        }
        _anim.Start();
    }

    void Animate()
    {
        var diff = _target - _current;
        var done = Math.Abs(diff) < 1.5f;

        if (done) _current = _target;
        else _current += diff * 0.4f;

        SuspendLayout();
        if (done)
        {
            // 收尾：走完整流程把滚动条同步到最终位置
            AutoScrollPosition = new Point(0, (int)Math.Round(_current));
            _anim.Stop();
        }
        else
        {
            // 过程中只移动视口，不碰滚动条，省下每帧一次 SyncScrollbars
            SetDisplayRectLocation(0, -(int)Math.Round(_current));
        }
        ResumeLayout(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _anim.Stop();
            _anim.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>不吞滚轮的下拉框：滚轮交给外层滚动容器，而不是改选项。</summary>
public sealed class WheelThroughComboBox : ComboBox
{
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    protected override void WndProc(ref Message m)
    {
        const int WM_MOUSEWHEEL = 0x020A;
        if (m.Msg == WM_MOUSEWHEEL)
        {
            var p = Parent;
            while (p is not null && p is not SmoothScrollPanel) p = p.Parent;
            if (p is not null)
            {
                SendMessage(p.Handle, m.Msg, m.WParam, m.LParam);
                return;
            }
        }
        base.WndProc(ref m);
    }
}

/// <summary>不吞滚轮的数值框。</summary>
public sealed class WheelThroughNumeric : NumericUpDown
{
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    protected override void WndProc(ref Message m)
    {
        const int WM_MOUSEWHEEL = 0x020A;
        if (m.Msg == WM_MOUSEWHEEL)
        {
            var p = Parent;
            while (p is not null && p is not SmoothScrollPanel) p = p.Parent;
            if (p is not null)
            {
                SendMessage(p.Handle, m.Msg, m.WParam, m.LParam);
                return;
            }
        }
        base.WndProc(ref m);
    }
}

/// <summary>标题栏上的小徽标（如「测试版」）。</summary>
public sealed class TagBadge : Control
{
    public int Radius { get; set; } = 7;
    public Color Fill { get; set; } = Color.FromArgb(250, 173, 20);
    public Color TextColor { get; set; } = Color.FromArgb(86, 48, 0);

    GraphicsPath? _path;
    Size _pathSize;

    public TagBadge()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.TitleBar;
        Font = Theme.F(8.5f, FontStyle.Bold);
        Padding = Theme.Pad(11, 4, 11, 4);
        Size = Theme.Sz(66, 26);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var sz = TextRenderer.MeasureText(Text, Font);
        return new Size(sz.Width + Padding.Horizontal, sz.Height + Padding.Vertical);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 0 || Height <= 0) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        if (_path is null || _pathSize != r.Size)
        {
            _path?.Dispose();
            _path = Theme.RoundRect(r, Radius);
            _pathSize = r.Size;
        }

        using (var b = new SolidBrush(Fill))
            g.FillPath(b, _path);

        TextRenderer.DrawText(g, Text, Font, r, TextColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _path?.Dispose();
        base.Dispose(disposing);
    }
}
