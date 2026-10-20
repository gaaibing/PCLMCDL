using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using PCLMCDL.Core;
using PCLMCDL.Ui;

namespace PCLMCDL;

public sealed class MainForm : Form
{
    // 设计基准（96 DPI），实际值统一经 Theme.S() 换算
    const int TitleH = 58;

    readonly Panel _titleBar = new();
    readonly Panel _content = new();
    readonly NavButton _navDl = new() { Text = "下载" };
    readonly NavButton _navSet = new() { Text = "设置" };
    readonly Panel _pageDl = new() { Dock = DockStyle.Fill };
    readonly SmoothScrollPanel _pageSet = new() { Dock = DockStyle.Fill, Visible = false };

    readonly ComboBox _cmbDir = new WheelThroughComboBox();
    readonly Label _lblDetect = new();
    readonly ComboBox _cmbType = new WheelThroughComboBox();
    readonly ComboBox _cmbMc = new WheelThroughComboBox();
    readonly ComboBox _cmbLoader = new WheelThroughComboBox();
    readonly ComboBox _cmbLoaderVer = new WheelThroughComboBox();
    readonly ComboBox _cmbOf = new WheelThroughComboBox();
    readonly TextBox _txtName = new();
    readonly RoundButton _btnInstall = new();
    readonly RoundButton _btnCancel = new();
    readonly ProgressBar _bar = new();
    readonly Label _lblStatus = new();
    readonly TextBox _txtLog = new();

    readonly RadioButton _rbAuto = new();
    readonly RadioButton _rbOfficial = new();
    readonly RadioButton _rbMirror = new();
    readonly NumericUpDown _numWorkers = new WheelThroughNumeric();
    readonly ComboBox _cmbJava = new WheelThroughComboBox();
    readonly Label _lblJavaState = new();
    readonly ComboBox _cmbDirSet = new WheelThroughComboBox();
    readonly Label _lblDirState = new();

    readonly ConcurrentQueue<string> _logQueue = new();
    readonly System.Windows.Forms.Timer _logTimer = new() { Interval = 150 };
    readonly System.Windows.Forms.Timer _mcTextTimer = new() { Interval = 600 };
    string _lastProbedMc = "";
    string _probingMc = "";

    List<Paths.McCandidate> _dirCands = new();
    List<Paths.JavaCandidate> _javaCands = new();
    bool _syncingDir;
    bool _settingsEntered;
    bool _loadingLoaderVer;
    bool _rebuildingLoader;
    bool _closingAfterCancel;

    JsonObject? _manifest;
    string? _mcDir;
    List<OptiFine.Entry> _ofEntries = new();
    CancellationTokenSource? _cts;
    bool _busy;

    public MainForm()
    {
        // 所有尺寸已由 Theme.S() 按 DPI 换算，禁止 WinForms 再缩放一次
        AutoScaleMode = AutoScaleMode.None;
        Text = "PCLMCDL";
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Theme.Bg;
        Font = Theme.F(9.5f);
        DoubleBuffered = true;
        StartPosition = FormStartPosition.CenterScreen;

        ApplyInitialSize();
        ApplyIcon();

        BuildTitleBar();
        BuildContent();
        BuildShell();

        UpdateEnabledStates();

        _logTimer.Tick += (_, _) => FlushLog();
        _logTimer.Start();
        // 手动输入 MC 版本的防抖探测：停笔 600ms 后查一次可用加载器
        _mcTextTimer.Tick += (_, _) =>
        {
            _mcTextTimer.Stop();
            var mc = _cmbMc.Text.Trim();
            if (_busy || mc.Length == 0 || mc == "获取中…" || mc == _lastProbedMc) return;
            _ = OnMcChangedAsync();
        };
        Load += async (_, _) => await InitAsync();
        Shown += (_, _) => WindowChrome.ApplyRoundCorner(this);
    }

    /// <summary>先按 DPI 估算一个尺寸，OnShown 后再用真实工作区校正。</summary>
    void ApplyInitialSize()
    {
        var designH = Theme.DpiScale >= 1.5 ? 748 : 900;
        ClientSize = new Size(Theme.S(1020), Theme.S(designH));
        MinimumSize = new Size(Theme.S(820), Theme.S(600));
    }

    /// <summary>窗体/任务栏图标：取内置的多尺寸 app.ico，取不到就让系统用 exe 自带的。</summary>
    void ApplyIcon()
    {
        try
        {
            var asm = typeof(MainForm).Assembly;
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("app.ico", StringComparison.OrdinalIgnoreCase));
            if (name is null) return;
            using var s = asm.GetManifestResourceStream(name);
            if (s is not null) Icon = new Icon(s);
        }
        catch { /* 图标异常不影响启动 */ }
    }

    /// <summary>用显示器物理工作区兜底，保证窗口一定完整可见。</summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        FitToWorkArea();
    }

    void FitToWorkArea()
    {
        try
        {
            var area = Screen.FromControl(this).WorkingArea;
            if (area.Width <= 0 || area.Height <= 0) return;

            var w = Math.Min(ClientSize.Width, (int)(area.Width * 0.96));
            var h = Math.Min(ClientSize.Height, (int)(area.Height * 0.96));
            if (w != ClientSize.Width || h != ClientSize.Height)
            {
                ClientSize = new Size(w, h);
                CenterToScreen();
            }
        }
        catch { /* 取不到就保持原尺寸 */ }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    // ============================================================ 标题栏

    /// <summary>程序版本，取自 csproj &lt;Version&gt;，形如 0.5.0。</summary>
    static string AppVersion()
    {
        var v = typeof(MainForm).Assembly.GetName().Version;
        return v is null ? "0.0" : $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
    }

    void BuildTitleBar()
    {
        _titleBar.Dock = DockStyle.Fill;
        _titleBar.BackColor = Theme.TitleBar;
        _titleBar.Margin = new Padding(0);

        var tbl = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0),
            BackColor = Theme.TitleBar,
        };
        tbl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        tbl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var left = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Theme.Pad(30, 0, 0, 0),
            BackColor = Theme.TitleBar,
        };

        var title = new Label
        {
            Text = "PCLMCDL",
            AutoSize = true,
            Margin = Theme.Pad(0, 15, 14, 15),
            Font = Theme.F(15f, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = Theme.TitleBar,
        };
        var sub = new Label
        {
            Text = "Minecraft Downloader",
            AutoSize = true,
            Margin = Theme.Pad(0, 21, 34, 19),
            Font = Theme.F(9.5f),
            ForeColor = Color.FromArgb(182, 209, 255),
            BackColor = Theme.TitleBar,
        };

        _navDl.Size = Theme.Sz(100, 38);
        _navDl.Font = Theme.F(10.5f, FontStyle.Bold);
        _navDl.Margin = Theme.Pad(0, 10, 12, 10);
        _navSet.Size = Theme.Sz(100, 38);
        _navSet.Font = Theme.F(10.5f, FontStyle.Bold);
        _navSet.Margin = Theme.Pad(0, 10, 0, 10);
        _navDl.SetActive(true);
        _navDl.Click += (_, _) => SwitchPage(false);
        _navSet.Click += (_, _) => SwitchPage(true);

        var badge = new TagBadge
        {
            Text = "v" + AppVersion(),
            Margin = Theme.Pad(6, 16, 0, 16),
        };
        left.Controls.AddRange(new Control[] { title, sub, _navDl, _navSet, badge });
        tbl.Controls.Add(left, 0, 0);

        var right = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0),
            BackColor = Theme.TitleBar,
        };
        var btnMin = new WindowButton { Style = WindowButton.Kind.Minimize, Size = Theme.Sz(58, TitleH), Margin = new Padding(0) };
        var btnMax = new WindowButton { Style = WindowButton.Kind.Maximize, Size = Theme.Sz(58, TitleH), Margin = new Padding(0) };
        var btnClose = new WindowButton { Style = WindowButton.Kind.Close, Size = Theme.Sz(58, TitleH), Margin = new Padding(0) };
        btnMin.Click += (_, _) => WindowState = FormWindowState.Minimized;
        btnMax.Click += (_, _) => ToggleMaximize();
        btnClose.Click += (_, _) => Close();
        right.Controls.AddRange(new Control[] { btnMin, btnMax, btnClose });
        tbl.Controls.Add(right, 2, 0);

        var spacer = new Panel { Dock = DockStyle.Fill, BackColor = Theme.TitleBar, Margin = new Padding(0) };
        tbl.Controls.Add(spacer, 1, 0);

        void Drag(object? s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            WindowChrome.DragMove(this);
        }
        foreach (var c in new Control[] { _titleBar, tbl, left, spacer, title, sub })
        {
            c.MouseDown += Drag;
            c.DoubleClick += (_, _) => ToggleMaximize();
        }

        _titleBar.Controls.Add(tbl);
    }

    public void ToggleMaximize()
        => WindowState = WindowState == FormWindowState.Maximized
            ? FormWindowState.Normal
            : FormWindowState.Maximized;

    public void SwitchPage(bool settings)
    {
        if (_pageDl.Visible == !settings && _pageSet.Visible == settings) return;
        SuspendLayout();
        _pageDl.Visible = !settings;
        _pageSet.Visible = settings;
        _navDl.SetActive(!settings);
        _navSet.SetActive(settings);
        ResumeLayout(true);
        // 首次进入设置页时等布局完成再回顶，否则会被焦点控件拽到中间
        if (settings && !_settingsEntered)
        {
            _settingsEntered = true;
            BeginInvoke(() =>
            {
                try { _pageSet.AutoScrollPosition = new Point(0, 0); } catch { /* ignore */ }
            });
        }
    }



    public void ShowSettings() => SwitchPage(true);

    /// <summary>开发自检用：自动选中第一个 MC 版本，触发加载器探测。</summary>
    public void AutoPickFirstVersion()
    {
        if (_cmbMc.Items.Count > 0) _cmbMc.SelectedIndex = 0;
    }

    // ============================================================ 外壳

    void BuildContent()
    {
        _content.Dock = DockStyle.Fill;
        _content.BackColor = Theme.Bg;
        _content.Padding = new Padding(0);
        _content.Margin = new Padding(0);
        _content.Controls.Add(_pageSet);
        _content.Controls.Add(_pageDl);

        BuildDownloadPage();
        BuildSettingsPage();
    }

    void BuildShell()
    {
        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(TitleH)));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.Controls.Add(_titleBar, 0, 0);
        shell.Controls.Add(_content, 0, 1);
        Controls.Add(shell);
    }

    // ---------------------------------------------------------- 下载页

    void BuildDownloadPage()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 5,
            Padding = Theme.Pad(28, 24, 28, 28),
            BackColor = Theme.Bg,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // --- 目录卡片 ---
        var cardDir = new Card("游戏目录")
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            Margin = Theme.Pad(0, 0, 0, 16),
        };
        var dirGrid = UiLayout.Grid(3, 2);
        dirGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        dirGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        dirGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        dirGrid.Controls.Add(UiLayout.Label(".minecraft 路径"), 0, 0);
        StyleCombo(_cmbDir, ComboBoxStyle.DropDownList, 12);
        _cmbDir.SelectedIndexChanged += (_, _) =>
        {
            if (!_syncingDir && _cmbDir.SelectedIndex >= 0) SelectDir(_cmbDir.SelectedIndex);
        };
        dirGrid.Controls.Add(_cmbDir, 1, 0);

        var dirBtns = UiLayout.Row();
        var btnDetect = UiLayout.Secondary("自动检测");
        btnDetect.Click += async (_, _) => await DetectAllAsync(true);
        var btnBrowse = UiLayout.Secondary("浏览…");
        btnBrowse.Click += (_, _) => BrowseDir();
        dirBtns.Controls.AddRange(new Control[] { btnDetect, btnBrowse });
        dirGrid.Controls.Add(dirBtns, 2, 0);

        _lblDetect.AutoSize = true;
        _lblDetect.ForeColor = Theme.TextSecondary;
        _lblDetect.Margin = Theme.Pad(0, 10, 0, 0);
        dirGrid.Controls.Add(_lblDetect, 0, 1);
        dirGrid.SetColumnSpan(_lblDetect, 3);

        cardDir.AddContent(dirGrid);
        root.Controls.Add(cardDir, 0, 0);

        // --- 选择版本卡片 ---
        var cardSel = new Card("选择版本")
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            Margin = Theme.Pad(0, 0, 0, 16),
        };
        var sel = UiLayout.Grid(4, 3);
        sel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(210)));
        sel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        sel.Controls.Add(UiLayout.Label("版本类型"), 0, 0);
        StyleCombo(_cmbType, ComboBoxStyle.DropDownList);
        _cmbType.Items.AddRange(new object[] { "正式版", "快照", "旧版", "全部" });
        _cmbType.SelectedIndex = 0;
        _cmbType.SelectedIndexChanged += (_, _) => FillMcVersions();
        sel.Controls.Add(_cmbType, 1, 0);

        sel.Controls.Add(UiLayout.Label("MC 版本"), 2, 0);
        StyleCombo(_cmbMc, ComboBoxStyle.DropDown, 0);
        _cmbMc.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _cmbMc.AutoCompleteSource = AutoCompleteSource.ListItems;
        _cmbMc.SelectedIndexChanged += async (_, _) => await OnMcChangedAsync();
        _cmbMc.TextChanged += (_, _) =>
        {
            // 手动输入（非下拉选择）也要触发探测，防抖交给定时器
            var mc = _cmbMc.Text.Trim();
            if (_busy || mc.Length == 0 || mc == "获取中…" || mc == _lastProbedMc) return;
            _mcTextTimer.Stop();
            _mcTextTimer.Start();
        };
        sel.Controls.Add(_cmbMc, 3, 0);

        sel.Controls.Add(UiLayout.Label("加载器"), 0, 1);
        StyleCombo(_cmbLoader, ComboBoxStyle.DropDownList);
        _cmbLoader.Items.AddRange(new object[] { "无（原版）", "Fabric", "Forge", "NeoForge", "OptiFine" });
        _cmbLoader.SelectedIndex = 0;
        _cmbLoader.SelectedIndexChanged += async (_, _) =>
        {
            if (!_rebuildingLoader) await OnLoaderChangedAsync();
        };
        sel.Controls.Add(_cmbLoader, 1, 1);

        sel.Controls.Add(UiLayout.Label("加载器版本"), 2, 1);
        StyleCombo(_cmbLoaderVer, ComboBoxStyle.DropDown, 0);
        _cmbLoaderVer.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _cmbLoaderVer.AutoCompleteSource = AutoCompleteSource.ListItems;
        _cmbLoaderVer.SelectedIndexChanged += (_, _) => UpdateName();
        _cmbLoaderVer.TextChanged += (_, _) =>
        {
            if (!_loadingLoaderVer) UpdateName();
        };
        sel.Controls.Add(_cmbLoaderVer, 3, 1);

        sel.Controls.Add(UiLayout.Label("额外 OptiFine"), 0, 2);
        StyleCombo(_cmbOf);
        _cmbOf.SelectedIndexChanged += (_, _) => UpdateName();
        sel.Controls.Add(_cmbOf, 1, 2);

        sel.Controls.Add(UiLayout.Label("版本名"), 2, 2);
        StyleInput(_txtName);
        sel.Controls.Add(_txtName, 3, 2);

        cardSel.AddContent(sel);
        root.Controls.Add(cardSel, 0, 1);

        // --- 操作行 ---
        var act = UiLayout.Grid(3, 1);
        act.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        act.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        act.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        act.Margin = Theme.Pad(0, 0, 0, 10);

        _btnInstall.Text = "开始下载安装";
        _btnInstall.Font = Theme.F(10.5f, FontStyle.Bold);
        _btnInstall.Padding = Theme.Pad(32, 13, 32, 13);
        _btnInstall.AutoSize = true;
        _btnInstall.Margin = Theme.Pad(0, 0, 12, 0);
        _btnInstall.Click += async (_, _) => await InstallAsync();
        act.Controls.Add(_btnInstall, 0, 0);

        _btnCancel.Text = "取消";
        _btnCancel.Fill = Color.White;
        _btnCancel.FillHover = Color.FromArgb(245, 248, 253);
        _btnCancel.FillDown = Color.FromArgb(232, 240, 252);
        _btnCancel.TextColor = Theme.Primary;
        _btnCancel.BorderColor = Theme.Border;
        _btnCancel.Font = Theme.F(10f, FontStyle.Bold);
        _btnCancel.Padding = Theme.Pad(24, 13, 24, 13);
        _btnCancel.AutoSize = true;
        _btnCancel.Enabled = false;
        _btnCancel.Margin = Theme.Pad(0, 0, 18, 0);
        _btnCancel.Click += (_, _) => _cts?.Cancel();
        act.Controls.Add(_btnCancel, 1, 0);

        _bar.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _bar.Height = Theme.S(20);
        _bar.Maximum = 1000;
        _bar.Margin = Theme.Pad(0, 11, 0, 11);
        act.Controls.Add(_bar, 2, 0);

        root.Controls.Add(act, 0, 2);

        // --- 状态 ---
        _lblStatus.Text = "就绪";
        _lblStatus.AutoSize = true;
        _lblStatus.ForeColor = Theme.TextSecondary;
        _lblStatus.Margin = Theme.Pad(2, 0, 0, 12);
        root.Controls.Add(_lblStatus, 0, 3);

        // --- 日志 ---
        var cardLog = new Card("日志") { Height = Theme.S(162), Dock = DockStyle.Top, Margin = new Padding(0) };
        cardLog.Body.AutoSize = false;
        cardLog.Body.RowStyles[1] = new RowStyle(SizeType.Percent, 100);

        _txtLog.Multiline = true;
        _txtLog.ReadOnly = true;
        _txtLog.ScrollBars = ScrollBars.Vertical;
        _txtLog.Dock = DockStyle.Fill;
        _txtLog.BorderStyle = BorderStyle.None;
        _txtLog.BackColor = Color.FromArgb(250, 251, 253);
        _txtLog.ForeColor = Color.FromArgb(52, 60, 74);
        _txtLog.Font = new Font("Consolas", 9.5F);
        _txtLog.WordWrap = false;
        _txtLog.Margin = Theme.Pad(0, 4, 0, 0);
        cardLog.AddContent(_txtLog);

        var btnScrollEnd = UiLayout.Secondary("滚动到底");
        btnScrollEnd.Font = Theme.F(8.5f);
        btnScrollEnd.Padding = Theme.Pad(12, 5, 12, 5);
        btnScrollEnd.Click += (_, _) => ScrollLogToEnd();

        var btnClearLog = UiLayout.Secondary("清空");
        btnClearLog.Font = Theme.F(8.5f);
        btnClearLog.Padding = Theme.Pad(12, 5, 12, 5);
        btnClearLog.Margin = Theme.Pad(8, 2, 0, 2);
        btnClearLog.Click += (_, _) => ClearLog();

        cardLog.AddHeaderRight(btnScrollEnd, btnClearLog);
        root.Controls.Add(cardLog, 0, 4);

        var scroll = new SmoothScrollPanel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
        scroll.Controls.Add(root);
        _pageDl.Controls.Add(scroll);
    }

    // ---------------------------------------------------------- 设置页

    void BuildSettingsPage()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = Theme.Pad(28, 24, 28, 28),
            BackColor = Theme.Bg,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // --- 下载源 ---
        var cardSrc = MakeCard("下载源");
        var srcWrap = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(0),
            BackColor = Theme.Card,
        };
        _rbAuto.Text = "自动（官方优先，失败回退 BMCLAPI 镜像）";
        _rbMirror.Text = "镜像优先（BMCLAPI 加速，国内推荐）";
        _rbOfficial.Text = "仅官方源（不使用任何镜像）";
        foreach (var rb in new[] { _rbAuto, _rbMirror, _rbOfficial })
        {
            rb.AutoSize = true;
            rb.BackColor = Theme.Card;
            rb.ForeColor = Theme.TextPrimary;
            rb.Margin = Theme.Pad(0, 3, 0, 9);
            srcWrap.Controls.Add(rb);
        }
        _rbAuto.Checked = true;
        cardSrc.AddContent(srcWrap);
        root.Controls.Add(cardSrc);

        // --- 性能 ---
        var cardPerf = MakeCard("下载性能");
        var perfGrid = UiLayout.Grid(3, 1);
        perfGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        perfGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(110)));
        perfGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        perfGrid.Controls.Add(UiLayout.Label("并发线程数"), 0, 0);
        _numWorkers.Minimum = 4;
        _numWorkers.Maximum = 64;
        _numWorkers.Value = 24;
        _numWorkers.Anchor = AnchorStyles.Left;
        _numWorkers.Font = Theme.F(11f);
        _numWorkers.Margin = Theme.Pad(0, 5, 20, 5);
        perfGrid.Controls.Add(_numWorkers, 1, 0);

        perfGrid.Controls.Add(new Label
        {
            Text = "镜像站对高并发有限流，建议 8–24。",
            AutoSize = true,
            ForeColor = Theme.TextSecondary,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0),
        }, 2, 0);
        cardPerf.AddContent(perfGrid);
        root.Controls.Add(cardPerf);

        // --- Java ---
        var cardJava = MakeCard("Java 运行环境");
        var javaGrid = UiLayout.Grid(3, 2);
        javaGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        javaGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        javaGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        javaGrid.Controls.Add(UiLayout.Label("java.exe"), 0, 0);
        StyleCombo(_cmbJava, ComboBoxStyle.DropDownList, 12);
        javaGrid.Controls.Add(_cmbJava, 1, 0);

        var javaBtns = UiLayout.Row();
        var btnJavaAuto = UiLayout.Secondary("自动检测");
        btnJavaAuto.Click += async (_, _) => await DetectAllAsync(true);
        var btnJavaBrowse = UiLayout.Secondary("浏览…");
        btnJavaBrowse.Click += (_, _) => BrowseJava();
        javaBtns.Controls.AddRange(new Control[] { btnJavaAuto, btnJavaBrowse });
        javaGrid.Controls.Add(javaBtns, 2, 0);

        _lblJavaState.AutoSize = true;
        _lblJavaState.ForeColor = Theme.TextSecondary;
        _lblJavaState.Margin = Theme.Pad(0, 10, 0, 0);
        javaGrid.Controls.Add(_lblJavaState, 0, 1);
        javaGrid.SetColumnSpan(_lblJavaState, 3);

        cardJava.AddContent(javaGrid);
        root.Controls.Add(cardJava);

        // --- 游戏目录 ---
        var cardDir = MakeCard("游戏目录");
        var dirGrid = UiLayout.Grid(3, 2);
        dirGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        dirGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        dirGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        dirGrid.Controls.Add(UiLayout.Label(".minecraft"), 0, 0);
        StyleCombo(_cmbDirSet, ComboBoxStyle.DropDownList, 12);
        _cmbDirSet.SelectedIndexChanged += (_, _) =>
        {
            if (!_syncingDir && _cmbDirSet.SelectedIndex >= 0) SelectDir(_cmbDirSet.SelectedIndex);
        };
        dirGrid.Controls.Add(_cmbDirSet, 1, 0);

        var dirBtns = UiLayout.Row();
        var btnDirAuto = UiLayout.Secondary("自动检测");
        btnDirAuto.Click += async (_, _) => await DetectAllAsync(true);
        var btnDirBrowse = UiLayout.Secondary("浏览…");
        btnDirBrowse.Click += (_, _) =>
        {
            BrowseDir();
        };
        dirBtns.Controls.AddRange(new Control[] { btnDirAuto, btnDirBrowse });
        dirGrid.Controls.Add(dirBtns, 2, 0);

        _lblDirState.AutoSize = true;
        _lblDirState.ForeColor = Theme.TextSecondary;
        _lblDirState.Margin = Theme.Pad(0, 10, 0, 0);
        dirGrid.Controls.Add(_lblDirState, 0, 1);
        dirGrid.SetColumnSpan(_lblDirState, 3);

        cardDir.AddContent(dirGrid);
        root.Controls.Add(cardDir);

        // --- 关于 ---
        var cardAbout = MakeCard("关于");
        var about = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(0),
            BackColor = Theme.Card,
        };
        about.Controls.Add(AboutLine($"版本 {AppVersion()}", false));
        about.Controls.Add(AboutLine("PCL 作者：龙腾猫跃", true));
        about.Controls.Add(AboutLink("赞助 PCL 作者（爱发电）：https://afdian.com/a/LTCat",
            "https://afdian.com/a/LTCat"));
        about.Controls.Add(AboutLink("PCL 项目主页：https://github.com/Meloong-Git/PCL",
            "https://github.com/Meloong-Git/PCL"));
        about.Controls.Add(AboutLine("PCLMCDL 是第三方作品，与 PCL 官方无关。", false));
        about.Controls.Add(AboutLine("下载流程参考 PCL 的实现，并复用其内置的 headless 安装器。", false));
        about.Controls.Add(AboutLine("遵循《PCL 存储库合理使用指南》，源码随程序一同提供。", false));
        cardAbout.AddContent(about);
        root.Controls.Add(cardAbout);

        // --- 保存 ---
        var saveRow = UiLayout.Row();
        saveRow.Anchor = AnchorStyles.Left;
        saveRow.Margin = Theme.Pad(0, 6, 0, 0);
        var btnSave = new RoundButton
        {
            Text = "保存设置",
            AutoSize = true,
            Font = Theme.F(10.5f, FontStyle.Bold),
            Padding = Theme.Pad(32, 13, 32, 13),
        };
        btnSave.Click += (_, _) => SaveSettings();
        saveRow.Controls.Add(btnSave);
        root.Controls.Add(saveRow);

        _pageSet.Controls.Add(root);
    }

    static Card MakeCard(string header) => new(header)
    {
        Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Margin = Theme.Pad(0, 0, 0, 16),
    };

    /// <summary>输入框与下拉框共用同一套边距和字号，保证四列严格对齐。</summary>
    static void StyleInput(TextBox t)
    {
        t.Dock = DockStyle.Fill;
        t.Margin = Theme.Pad(0, 5, 0, 5);
        t.Font = Theme.F(11f);
    }

    static void StyleCombo(ComboBox c, ComboBoxStyle style = ComboBoxStyle.DropDownList, int rightGap = 24)
    {
        c.DropDownStyle = style;
        c.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        c.Margin = Theme.Pad(0, 5, rightGap, 5);
        c.Font = Theme.F(11f);
    }

    static Label AboutLine(string text, bool strong) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(Theme.S(860), 0),
        ForeColor = strong ? Theme.TextPrimary : Theme.TextSecondary,
        Font = strong ? Theme.F(10f, FontStyle.Bold) : Theme.F(9.5f),
        Margin = Theme.Pad(0, 3, 0, 3),
    };

    static LinkLabel AboutLink(string text, string url)
    {
        var l = new LinkLabel
        {
            Text = text,
            AutoSize = true,
            LinkColor = Theme.Primary,
            ActiveLinkColor = Theme.PrimaryDark,
            Font = Theme.F(9.5f),
            Margin = Theme.Pad(0, 3, 0, 3),
        };
        l.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* 打不开浏览器时忽略 */ }
        };
        return l;
    }

    // ============================================================ 初始化

    async Task InitAsync()
    {
        try
        {
            var cfg = Paths.LoadConfig();
            _numWorkers.Value = Math.Clamp(cfg.Workers, 4, 64);
            (_rbAuto.Checked, _rbMirror.Checked, _rbOfficial.Checked) = cfg.Source switch
            {
                "mirror" => (false, true, false),
                "official" => (false, false, true),
                _ => (true, false, false),
            };

            await DetectAllAsync(false);

            _cmbMc.Text = "获取中…";
            _cmbMc.ForeColor = Theme.TextSecondary;
            Log("正在获取版本清单…");
            try
            {
                var src = cfg.NewSource();
                _manifest = await Vanilla.FetchManifestAsync(src, CancellationToken.None);
                // 后台预热 Forge 版本清单，避免用户点开 Forge 时干等十几秒
                _ = Task.Run(() => Forge.WarmUpAsync(src));
                FillMcVersions();
                Log($"清单已加载，共 {(_manifest?["versions"] as JsonArray)?.Count ?? 0} 个版本（源：{SourceLabel(cfg.Source)}）");
            }
            catch (Exception e)
            {
                Log("[错误] 获取版本清单失败：" + e.Message);
                _cmbMc.Text = "";
                _cmbMc.ForeColor = Theme.TextPrimary;
                var tip = SuggestFor(e);
                _lblStatus.Text = "版本清单获取失败：" + tip;
                Log("[建议] " + tip);
            }
        }
        catch (Exception e)
        {
            Log("[错误] 初始化异常：" + e);
            ShowFatal("初始化失败", e);
        }
    }

    static string SourceLabel(string s) => s switch
    {
        "official" => "仅官方",
        "mirror" => "镜像优先",
        _ => "自动",
    };

    /// <summary>把常见异常翻译成可操作的建议。</summary>
    static bool HasInner<T>(Exception e) where T : Exception
    {
        for (var x = e.InnerException; x is not null; x = x.InnerException)
            if (x is T) return true;
        return false;
    }

    static bool IsForbidden(Exception e)
    {
        for (var x = e; x is not null; x = x.InnerException)
            if (x is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden }) return true;
        return false;
    }

    static string SuggestFor(Exception e) => e switch
    {
        HttpRequestException => "网络不可用或被拦截，请检查网络，或在「设置」里换成「镜像优先」。",
        TaskCanceledException => "请求超时，网络较慢，建议在「设置」里换成「镜像优先」。",
        UnauthorizedAccessException => "没有写入权限，请换一个可写的游戏目录，或以管理员身份运行。",
        DirectoryNotFoundException => "目录不存在，请在「设置」里重新检测 .minecraft 路径。",
        IOException io when IsForbidden(io)
            => "镜像站临时限流（403）。等 1–2 分钟再试，或在「设置」里切换下载源。",
        IOException io when HasInner<HttpRequestException>(io) || HasInner<System.Net.Sockets.SocketException>(io)
            => "网络请求失败。检查网络连接，或在「设置」里换成「镜像优先」。",
        IOException io when HasInner<TimeoutException>(io)
            => "网络响应超时。建议在「设置」里换成「镜像优先」。",
        IOException io when io.Message.Contains("space", StringComparison.OrdinalIgnoreCase)
            => "磁盘空间不足，请清理后重试。",
        IOException => "读写文件失败，可能是磁盘或权限问题，请检查目录是否可写。",
        _ => "请重试；若持续失败，可在「设置」里切换下载源，或查看日志。",
    };

    void ShowFatal(string title, Exception e)
        => MessageBox.Show($"{e.Message}\n\n{SuggestFor(e)}", title, MessageBoxButtons.OK, MessageBoxIcon.Error);

    // ============================================================ 自动检测

    static string DirLabel(Paths.McCandidate d) => d.Source switch
    {
        "PCL" => "PCL 目录",
        "官启" => "官启目录",
        _ => d.Source,
    };

    void FillDirCombos()
    {
        _syncingDir = true;
        foreach (var cb in new[] { _cmbDir, _cmbDirSet })
        {
            cb.BeginUpdate();
            cb.Items.Clear();
            foreach (var d in _dirCands)
                cb.Items.Add($"{d.Path}    〔{d.VersionCount} 个版本 · {DirLabel(d)}〕");
            cb.EndUpdate();
        }
        _syncingDir = false;
    }

    void SelectDir(int index)
    {
        if (index < 0 || index >= _dirCands.Count) return;
        _syncingDir = true;
        if (_cmbDir.Items.Count > index) _cmbDir.SelectedIndex = index;
        if (_cmbDirSet.Items.Count > index) _cmbDirSet.SelectedIndex = index;
        _syncingDir = false;

        var d = _dirCands[index];
        _mcDir = d.Path;
        var msg = $"✓ 共 {_dirCands.Count} 个 .minecraft，已选 {d.Path}（{d.VersionCount} 个版本 · {DirLabel(d)}）";
        _lblDetect.Text = msg;
        _lblDetect.ForeColor = Theme.Success;
        _lblDirState.Text = msg;
        _lblDirState.ForeColor = Theme.Success;
    }

    void FillJavaCombo()
    {
        _cmbJava.BeginUpdate();
        _cmbJava.Items.Clear();
        foreach (var j in _javaCands)
            _cmbJava.Items.Add($"Java {j.Major} · {j.Exe}    〔{j.Source}〕");
        if (_cmbJava.Items.Count > 0) _cmbJava.SelectedIndex = 0;
        _cmbJava.EndUpdate();

        if (_javaCands.Count == 0)
        {
            _lblJavaState.Text = "✗ 未找到 Java。仅 Forge / NeoForge / 新版 OptiFine 需要，原版与 Fabric 不受影响。";
            _lblJavaState.ForeColor = Theme.Danger;
        }
        else
        {
            _lblJavaState.Text = $"✓ 找到 {_javaCands.Count} 个 Java，最高 Java {_javaCands[0].Major}";
            _lblJavaState.ForeColor = Theme.Success;
        }
    }

    Paths.JavaCandidate? SelectedJava()
        => _cmbJava.SelectedIndex >= 0 && _cmbJava.SelectedIndex < _javaCands.Count
            ? _javaCands[_cmbJava.SelectedIndex]
            : null;

    /// <summary>并行扫描所有 .minecraft 与 Java，通常几十毫秒完成。</summary>
    async Task DetectAllAsync(bool toast)
    {
        try
        {
            _lblDetect.Text = "正在扫描…";
            _lblDetect.ForeColor = Theme.TextSecondary;
            var t0 = DateTime.Now;

            var dirs = await Task.Run(() => Paths.FindAllMcDirs(_mcDir));
            _dirCands = dirs;
            FillDirCombos();

            if (dirs.Count == 0)
            {
                _lblDetect.Text = "✗ 未找到 .minecraft，请点「浏览…」手动指定";
                _lblDetect.ForeColor = Theme.Danger;
                _lblDirState.Text = "✗ 未找到 .minecraft";
                _lblDirState.ForeColor = Theme.Danger;
            }
            else
            {
                // 保留用户当前选中的目录，别被「版本数最多」挤掉
                var idx = _mcDir is null
                    ? -1
                    : dirs.FindIndex(x => string.Equals(x.Path, _mcDir, StringComparison.OrdinalIgnoreCase));
                SelectDir(idx >= 0 ? idx : 0);
            }

            var javas = await Task.Run(() => Paths.FindAllJavas(_mcDir));
            _javaCands = javas;
            FillJavaCombo();

            var ms = (int)(DateTime.Now - t0).TotalMilliseconds;
            Log($"扫描完成：{dirs.Count} 个 .minecraft、{javas.Count} 个 Java，用时 {ms} ms");
            foreach (var d in dirs) Log($"  · {d.Path}（{d.VersionCount} 个版本 · {DirLabel(d)}）");
            foreach (var j in javas) Log($"  · Java {j.Major}  {j.Exe}（{j.Source}）");

            if (toast && _mcDir is not null)
            {
                var cfg = Paths.LoadConfig();
                cfg.McDir = _mcDir;
                Paths.SaveConfig(cfg);
            }
        }
        catch (Exception e)
        {
            _lblDetect.Text = "✗ 扫描失败：" + e.Message;
            _lblDetect.ForeColor = Theme.Danger;
            Log("[错误] 扫描失败：" + e.Message);
        }
    }

    void BrowseDir()
    {
        try
        {
            using var dlg = new FolderBrowserDialog { Description = "选择 .minecraft 目录（或包含它的 PCL 目录）" };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            var found = Paths.FindMcDir(dlg.SelectedPath);
            _mcDir = found ?? dlg.SelectedPath;
            Log("已设置游戏目录：" + _mcDir);
            _ = DetectAllAsync(true);
        }
        catch (Exception e)
        {
            MessageBox.Show(e.Message, "选择目录失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void BrowseJava()
    {
        try
        {
            using var dlg = new OpenFileDialog
            {
                Title = "选择 java.exe",
                Filter = "java.exe|java.exe|所有文件|*.*",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            var major = Paths.JavaMajorPublic(dlg.FileName);
            if (major is null)
            {
                _lblJavaState.Text = "✗ 该文件不是可用的 Java";
                _lblJavaState.ForeColor = Theme.Danger;
                return;
            }
            _javaCands.Insert(0, new Paths.JavaCandidate(dlg.FileName, major.Value, "手动指定"));
            FillJavaCombo();
            _cmbJava.SelectedIndex = 0;
            _lblJavaState.Text = $"✓ Java {major.Value}（{dlg.FileName}）";
            _lblJavaState.ForeColor = Theme.Success;
        }
        catch (Exception e)
        {
            MessageBox.Show(e.Message, "选择 Java 失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void SaveSettings()
    {
        try
        {
            var cfg = Paths.LoadConfig();
            cfg.Source = _rbMirror.Checked ? "mirror" : _rbOfficial.Checked ? "official" : "auto";
            cfg.Workers = (int)_numWorkers.Value;
            cfg.JavaPath = SelectedJava()?.Exe;
            if (_mcDir is not null) cfg.McDir = _mcDir;
            Paths.SaveConfig(cfg);
            _lblStatus.Text = "设置已保存";
            Log($"设置已保存：源={SourceLabel(cfg.Source)}，线程={cfg.Workers}");
            MessageBox.Show("设置已保存。", "PCLMCDL");
        }
        catch (Exception e)
        {
            MessageBox.Show($"保存失败：{e.Message}\n\n{SuggestFor(e)}", "PCLMCDL",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ============================================================ 版本清单

    void FillMcVersions()
    {
        try
        {
            if (_manifest?["versions"] is not JsonArray arr) return;
            var kind = _cmbType.SelectedIndex switch
            {
                0 => "release",
                1 => "snapshot",
                2 => "old",
                _ => "all",
            };
            var list = new List<string>();
            foreach (var n in arr)
            {
                if (n is not JsonObject o) continue;
                var id = o["id"]?.GetValue<string>();
                var type = o["type"]?.GetValue<string>() ?? "";
                if (id is null) continue;
                var ok = kind == "all"
                         || (kind == "old" ? type is "old_alpha" or "old_beta" : type == kind);
                if (ok) list.Add(id);
            }
            var keep = _cmbMc.Text;
            _cmbMc.BeginUpdate();
            _cmbMc.Items.Clear();
            _cmbMc.Items.AddRange(list.Cast<object>().ToArray());
            _cmbMc.EndUpdate();
            _cmbMc.ForeColor = Theme.TextPrimary;
            // 不自动选第一个，让用户主动挑版本后再解锁后面的选项
            if (!string.IsNullOrEmpty(keep) && list.Contains(keep)) _cmbMc.Text = keep;
            else _cmbMc.Text = "";
            _cmbMc.SelectionLength = 0;
            UpdateEnabledStates();
        }
        catch (Exception e)
        {
            Log("[错误] 版本列表填充失败：" + e.Message);
        }
    }

    // ============================================================ 联动

    async Task OnMcChangedAsync()
    {
        var mc = "";
        try
        {
            UpdateName();
            _cmbLoaderVer.Items.Clear();
            SetLoaderVerText("");
            _cmbOf.Items.Clear();
            _cmbOf.Items.Add("（不使用）");
            _cmbOf.SelectedIndex = 0;
            _ofEntries.Clear();

            mc = _cmbMc.Text.Trim();
            if (string.IsNullOrEmpty(mc))
            {
                _lastProbedMc = "";
                ResetLoaderItems(new List<string>());
                UpdateEnabledStates();
                return;
            }

            // 同一版本已在探测中：下拉选择 + 文本变化会各触发一次，别打两遍请求
            if (_probingMc == mc) return;
            _probingMc = mc;

            // 先清空加载器列表，探测期间不让选
            ResetLoaderItems(new List<string>());
            _cmbLoader.Enabled = false;
            UpdateEnabledStates();
            _lblStatus.Text = "正在检查可用的加载器…";

            var src = Paths.LoadConfig().NewSource();
            var (available, of) = await Installer.ProbeLoadersAsync(src, mc, CancellationToken.None);

            // 探测期间用户已切换到别的版本，丢弃这份过期结果
            if (_cmbMc.Text.Trim() != mc)
            {
                if (_probingMc == mc) _probingMc = "";
                return;
            }
            _lastProbedMc = mc;

            _ofEntries = of;
            _cmbOf.BeginUpdate();
            _cmbOf.Items.Clear();
            _cmbOf.Items.Add("（不使用）");
            foreach (var e in of) _cmbOf.Items.Add($"OptiFine {e.Patch}（{e.Type}）");
            _cmbOf.SelectedIndex = 0;
            _cmbOf.EndUpdate();

            ResetLoaderItems(available);

            _lblStatus.Text = available.Count > 0
                ? $"该版本可用：{string.Join(" / ", available)}"
                : "该版本没有可用加载器，只能装原版";
            Log(available.Count > 0
                ? $"{mc} 可用加载器：{string.Join("、", available)}"
                : $"{mc} 没有可用的加载器（不影响原版安装）");

            UpdateEnabledStates();
        }
        catch (Exception e)
        {
            Log("[错误] 切换版本时异常：" + e.Message);
        }
        finally
        {
            // 只清自己设的标记，避免误清新版本的探测状态
            if (mc.Length > 0 && _probingMc == mc) _probingMc = "";
        }
    }

    /// <summary>重建加载器下拉：原版恒在，其余只保留探测到可用的。</summary>
    void ResetLoaderItems(List<string> available)
    {
        _rebuildingLoader = true;
        _cmbLoader.BeginUpdate();
        _cmbLoader.Items.Clear();
        _cmbLoader.Items.Add("无（原版）");
        foreach (var name in available) _cmbLoader.Items.Add(name);
        _cmbLoader.SelectedIndex = 0;
        _cmbLoader.EndUpdate();
        _rebuildingLoader = false;
    }

    async Task OnLoaderChangedAsync()
    {
        var mc = _cmbMc.Text.Trim();
        var key = LoaderKey(_cmbLoader.SelectedItem?.ToString());
        _cmbLoaderVer.Items.Clear();
        if (key == "vanilla" || string.IsNullOrEmpty(mc))
        {
            SetLoaderVerText("");
            UpdateName();
            return;
        }

        SetLoaderVerText("获取中…");
        UpdateEnabledStates();
        _lblStatus.Text = "正在获取加载器版本列表…";
        try
        {
            var src = Paths.LoadConfig().NewSource();
            List<string> vers = key switch
            {
                "fabric" => (await Fabric.ListLoadersAsync(src, mc, CancellationToken.None)).Select(x => x.Version).ToList(),
                "forge" => await Forge.ListForgeAsync(src, mc, CancellationToken.None),
                "neoforge" => await Forge.ListNeoForgeAsync(src, mc, CancellationToken.None),
                "optifine" => _ofEntries.Select(x => x.Patch).ToList(),
                _ => new List<string>(),
            };
            _cmbLoaderVer.Items.AddRange(vers.Cast<object>().ToArray());
            if (vers.Count > 0)
            {
                SetLoaderVerText("");
                _cmbLoaderVer.SelectedIndex = 0;
            }
            else
            {
                SetLoaderVerText("");
            }
            _lblStatus.Text = vers.Count > 0 ? $"共 {vers.Count} 个可用版本" : "该 MC 版本没有此加载器";
            if (vers.Count == 0) Log($"[提示] {mc} 没有可用的 {_cmbLoader.Text}，可换个 MC 版本或用「额外 OptiFine」叠加");
        }
        catch (Exception e)
        {
            SetLoaderVerText("");
            _lblStatus.Text = "获取加载器版本失败";
            Log("[错误] " + e.Message);
            Log("[建议] " + SuggestFor(e));
        }
        UpdateName();
    }

    /// <summary>设置版本下拉框的提示文本（灰色），不会触发版本名重算。</summary>
    void SetLoaderVerText(string text)
    {
        _loadingLoaderVer = true;
        _cmbLoaderVer.Text = text;
        _cmbLoaderVer.ForeColor = text.Length > 0 ? Theme.TextSecondary : Theme.TextPrimary;
        _loadingLoaderVer = false;
    }

    /// <summary>按当前选择状态启用/禁用控件：没选 MC 版本时，加载器及其后的部分保持灰。</summary>
    /// <summary>下拉显示名映射到内部标识。列表是动态的，不能按下标判断。</summary>
    static string LoaderKey(string? display) => display switch
    {
        "Fabric" => "fabric",
        "Forge" => "forge",
        "NeoForge" => "neoforge",
        "OptiFine" => "optifine",
        _ => "vanilla",
    };

    void UpdateEnabledStates()
    {
        var mc = _cmbMc.Text.Trim();
        var hasMc = mc.Length > 0 && mc != "获取中…";

        _cmbType.Enabled = !_busy;
        _cmbMc.Enabled = !_busy;
        _cmbLoader.Enabled = !_busy && hasMc;
        _cmbOf.Enabled = !_busy && hasMc;
        _cmbLoaderVer.Enabled = !_busy && hasMc && _cmbLoader.SelectedIndex > 0;
        _btnInstall.Enabled = !_busy && hasMc;
        // 版本名始终可编辑，不参与灰化
    }

    void UpdateName()
    {
        var mc = _cmbMc.Text.Trim();
        if (string.IsNullOrEmpty(mc))
        {
            _txtName.Text = "";
            return;
        }
        var loader = LoaderKey(_cmbLoader.SelectedItem?.ToString());
        var ver = _cmbLoaderVer.Text.Trim();
        var of = "";
        if (loader != "optifine" && _cmbOf.SelectedIndex > 0 && _cmbOf.SelectedIndex - 1 < _ofEntries.Count)
            of = _ofEntries[_cmbOf.SelectedIndex - 1].Patch;
        _txtName.Text = Paths.VersionName(mc, loader, ver, of);
    }

    // ============================================================ 安装

    async Task InstallAsync()
    {
        if (_busy) return;

        string? targetDir = null;
        var targetExistedBefore = true;

        try
        {
            var mc = _cmbMc.Text.Trim();
            if (string.IsNullOrEmpty(mc))
            {
                MessageBox.Show("请先选择 Minecraft 版本。", "PCLMCDL");
                return;
            }
            if (_mcDir is null || !Directory.Exists(_mcDir))
            {
                MessageBox.Show("游戏目录无效。请点「自动检测」，或用「浏览…」重新选择。", "PCLMCDL");
                return;
            }

            var loader = LoaderKey(_cmbLoader.SelectedItem?.ToString());
            var req = new InstallRequest
            {
                McVersion = mc,
                Loader = loader,
                LoaderVersion = _cmbLoaderVer.Text.Trim(),
            };
            if (loader is not ("vanilla" or "optifine") && string.IsNullOrEmpty(req.LoaderVersion))
            {
                MessageBox.Show("请选择加载器版本。", "PCLMCDL");
                return;
            }
            if (loader == "optifine")
            {
                var e = _ofEntries.FirstOrDefault(x => x.Patch == req.LoaderVersion);
                if (e is null)
                {
                    MessageBox.Show("请选择 OptiFine 版本。", "PCLMCDL");
                    return;
                }
                req.OptiFineEntry = e;
            }
            if (loader != "optifine" && _cmbOf.SelectedIndex > 0 && _cmbOf.SelectedIndex - 1 < _ofEntries.Count)
            {
                var e = _ofEntries[_cmbOf.SelectedIndex - 1];
                req.OptiFinePatch = e.Patch;
                req.OptiFineEntry = e;
            }

            // Java 前置校验，避免跑到一半才失败
            var needJava = loader is "forge" or "neoforge"
                           || (loader == "optifine" && !OptiFine.IsLegacyFor(mc));
            if (needJava && Paths.FindJava(8, Paths.LoadConfig().JavaPath) is null)
            {
                MessageBox.Show(
                    "未找到 Java 8 及以上版本。\n\n" +
                    "安装 Forge / NeoForge / 新版 OptiFine 需要本机已有 Java。\n" +
                    "请先安装 JDK 或 JRE，然后在「设置」里指定 java.exe。",
                    "缺少 Java", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var wanted = _txtName.Text.Trim();
            var cfg = Paths.LoadConfig();
            var planned = Installer.BuildName(req);

            // 同名版本已存在：覆盖 / 重命名 / 取消
            var versionsRoot = Path.Combine(_mcDir!, "versions");
            var desired = wanted.Length > 0 ? wanted : planned;
            var existingDir = Path.Combine(versionsRoot, desired);
            if (Directory.Exists(existingDir))
            {
                var choice = Ui.Dialogs.AskConflict(this, desired, existingDir);
                if (choice == ConflictChoice.Cancel)
                {
                    _lblStatus.Text = "已取消";
                    Log($"[已取消] 版本 {desired} 已存在，未做任何改动");
                    return;
                }

                if (choice == ConflictChoice.Rename)
                {
                    var renamed = Ui.Dialogs.AskName(this, versionsRoot, desired);
                    if (renamed is null)
                    {
                        _lblStatus.Text = "已取消";
                        Log("[已取消] 没有输入新的版本名");
                        return;
                    }
                    wanted = renamed;
                    desired = renamed;
                    _txtName.Text = renamed;
                    Log($"版本 {desired} 将另存为新名字，原版本保留");
                }
                else
                {
                    try
                    {
                        Directory.Delete(existingDir, true);
                        Log($"已删除旧的版本目录 {desired}，开始覆盖安装");
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(
                            $"现有版本目录删不掉（可能被其他程序占用）：\n{ex.Message}",
                            "覆盖失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    req.Force = true;
                }
            }

            _busy = true;
            _btnCancel.Enabled = true;
            UpdateEnabledStates();
            _bar.Value = 0;
            _cts = new CancellationTokenSource();

            IProgress<(long, long)> bytes = new Progress<(long, long)>(p =>
            {
                if (p.Item2 <= 0) return;
                var pct = (int)Math.Min(1000, p.Item1 * 1000 / p.Item2);
                if (pct > _bar.Value) _bar.Value = pct;
                _lblStatus.Text = $"下载中 {p.Item1 / 1048576.0:F1} / {p.Item2 / 1048576.0:F1} MB";
            });
            IProgress<string> log = new Progress<string>(Log);

            targetDir = Path.Combine(_mcDir!, "versions", planned);
            targetExistedBefore = Directory.Exists(targetDir);
            Log($"==== 开始安装 {planned}（源：{SourceLabel(cfg.Source)}，线程 {cfg.Workers}）====");

            var result = await Task.Run(() => Installer.RunAsync(
                cfg.NewSource(), _mcDir!, req,
                (long d, long t) => bytes.Report((d, t)),
                (string s) => log.Report(s),
                cfg.Workers, _cts.Token), _cts.Token);

            var finalName = result;
            if (!string.IsNullOrEmpty(wanted) && wanted != result)
            {
                // 本次其实是「跳过」旧版本时只能复制，不能把用户原来的版本搬走
                RenameVersion(result, wanted, move: !targetExistedBefore);
                finalName = wanted;
            }

            _bar.Value = 1000;
            _lblStatus.Text = "安装完成：" + finalName;
            Log($"==== 完成：{finalName} ====");
            _ = DetectAllAsync(false);
            MessageBox.Show($"版本 {finalName} 已安装。\n打开 PCL 刷新版本列表即可看到。", "PCLMCDL");
        }
        catch (OperationCanceledException)
        {
            _lblStatus.Text = "已取消";
            Log("[已取消] 安装被用户中断");
            if (!targetExistedBefore && targetDir is not null) RollbackDir(targetDir);
            Paths.CleanupTemp();
            Log("[清理] 已清理未下载完成文件");
        }
        catch (Exception e)
        {
            _lblStatus.Text = "安装失败";
            if (!targetExistedBefore && targetDir is not null) RollbackDir(targetDir);
            Log("[错误] " + e);
            Log("[建议] " + SuggestFor(e));
            MessageBox.Show(
                $"安装失败：{e.Message}\n\n{SuggestFor(e)}\n\n详细过程见日志窗口。",
                "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
            _btnCancel.Enabled = false;
            UpdateEnabledStates();
            _cts?.Dispose();
            _cts = null;
        }
    }

    void RenameVersion(string from, string to, bool move = true)
    {
        if (_mcDir is null) return;
        try
        {
            Installer.RenameVersion(_mcDir, from, to, Log, move);
        }
        catch (Exception e)
        {
            Log($"[警告] 重命名失败（版本仍可用，名字为 {from}）：{e.Message}");
        }
    }

    // ============================================================ 日志

    void Log(string s) => _logQueue.Enqueue($"[{DateTime.Now:HH:mm:ss}] {s}");

    void FlushLog()
    {
        if (_logQueue.IsEmpty) return;
        try
        {
            var sb = new System.Text.StringBuilder();
            var n = 0;
            while (n < 300 && _logQueue.TryDequeue(out var line))
            {
                sb.AppendLine(line);
                n++;
            }
            if (sb.Length == 0) return;
            _txtLog.AppendText(sb.ToString());
            if (_txtLog.TextLength > 400_000) _txtLog.Text = _txtLog.Text[^200_000..];
            _txtLog.SelectionStart = _txtLog.TextLength;
            _txtLog.ScrollToCaret();
        }
        catch { /* 日志写失败不影响主流程 */ }
    }

    void ScrollLogToEnd()
    {
        try
        {
            _txtLog.SelectionStart = _txtLog.TextLength;
            _txtLog.ScrollToCaret();
        }
        catch { /* ignore */ }
    }

    void ClearLog()
    {
        try
        {
            while (_logQueue.TryDequeue(out _)) { }
            _txtLog.Clear();
            Log("日志已清空");
        }
        catch { /* ignore */ }
    }

    // ============================================================ 关闭确认

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_busy && !_closingAfterCancel)
        {
            var r = MessageBox.Show(
                "下载任务正在进行。\n\n关闭会中断下载，并清理未完成的文件。确定关闭吗？",
                "PCLMCDL", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (r != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            e.Cancel = true;
            _closingAfterCancel = true;
            _cts?.Cancel();
            _lblStatus.Text = "正在中断并清理…";
            Log("[退出] 用户确认关闭，正在中断任务…");
            _ = CloseAfterCancelAsync();
            return;
        }
        base.OnFormClosing(e);
    }

    async Task CloseAfterCancelAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (_busy && sw.ElapsedMilliseconds < 6000) await Task.Delay(60);
        try { Paths.CleanupTemp(); } catch { /* ignore */ }
        Log("[退出] 已清理临时文件");
        Close();
    }

    void RollbackDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Log($"[清理] 已删除未完成的版本目录 {Path.GetFileName(dir)}");
        }
        catch { /* ignore */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _logTimer.Dispose();
            _mcTextTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    // ============================================================ 无边框窗口

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x0084;
        if (m.Msg == WM_NCHITTEST && WindowState == FormWindowState.Normal)
        {
            base.WndProc(ref m);
            if ((int)m.Result != 1) return;

            var lp = m.LParam.ToInt64();
            var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
            var g = Theme.S(6);
            var left = p.X <= g;
            var right = p.X >= ClientSize.Width - g;
            var top = p.Y <= g;
            var bottom = p.Y >= ClientSize.Height - g;

            m.Result = (IntPtr)((left, top, right, bottom) switch
            {
                (true, true, _, _) => 13,
                (_, true, true, _) => 14,
                (true, _, _, true) => 16,
                (_, _, true, true) => 17,
                (true, _, _, _) => 10,
                (_, _, true, _) => 11,
                (_, true, _, _) => 12,
                (_, _, _, true) => 15,
                _ => 1,
            });
            return;
        }
        base.WndProc(ref m);
    }
}

/// <summary>无边框窗口辅助。</summary>
internal static class WindowChrome
{
    [DllImport("user32.dll")]
    static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void DragMove(Form form)
    {
        ReleaseCapture();
        SendMessage(form.Handle, 0x00A1, 0x0002, 0);
    }

    public static void ApplyRoundCorner(Form form)
    {
        try
        {
            const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
            const int DWMWCP_ROUND = 2;
            var v = DWMWCP_ROUND;
            DwmSetWindowAttribute(form.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref v, sizeof(int));
        }
        catch { /* 旧系统忽略 */ }
    }
}

/// <summary>布局小工具。</summary>
internal static class UiLayout
{
    public static TableLayoutPanel Grid(int cols, int rows)
    {
        var g = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = cols,
            RowCount = rows,
            Margin = new Padding(0),
            BackColor = Theme.Card,
        };
        for (var i = 0; i < rows; i++) g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return g;
    }

    public static FlowLayoutPanel Row() => new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = false,
        Margin = new Padding(0),
        Anchor = AnchorStyles.Right,
        BackColor = Theme.Card,
    };

    public static Label Label(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = Theme.Pad(0, 0, 14, 0),
        ForeColor = Theme.TextSecondary,
        BackColor = Theme.Card,
    };

    public static RoundButton Secondary(string text) => new()
    {
        Text = text,
        Fill = Color.White,
        FillHover = Color.FromArgb(240, 246, 255),
        FillDown = Color.FromArgb(226, 238, 253),
        TextColor = Theme.Primary,
        BorderColor = Theme.Border,
        Font = Theme.F(9.5f),
        Padding = Theme.Pad(18, 9, 18, 9),
        AutoSize = true,
        Margin = Theme.Pad(10, 2, 0, 2),
    };
}
