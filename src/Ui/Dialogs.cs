namespace PCLMCDL.Ui;

/// <summary>同名版本已存在时的处理方式。</summary>
public enum ConflictChoice
{
    /// <summary>删除现有版本目录后重装。</summary>
    Overwrite,
    /// <summary>另存为新名字，原版本保留。</summary>
    Rename,
    /// <summary>什么都不做。</summary>
    Cancel,
}

/// <summary>安装前的简单模态对话框：同名冲突选择、版本重命名输入。</summary>
public static class Dialogs
{
    /// <summary>对话框结果容器（自绘按钮不实现 IButtonControl，只能靠闭包回传）。</summary>
    sealed class Box<T>
    {
        public T? Value;
    }

    static Form Shell(string title, Size size) => new()
    {
        Text = title,
        FormBorderStyle = FormBorderStyle.FixedDialog,
        StartPosition = FormStartPosition.CenterParent,
        MinimizeBox = false,
        MaximizeBox = false,
        ShowIcon = false,
        ShowInTaskbar = false,
        BackColor = Theme.Bg,
        ClientSize = size,
        Font = Theme.F(9.5f),
        AutoScaleMode = AutoScaleMode.None,
    };

    static TableLayoutPanel Root(Form form)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = Theme.Pad(24, 20, 24, 18),
            BackColor = Theme.Bg,
            Margin = new Padding(0),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        form.Controls.Add(root);
        return root;
    }

    static FlowLayoutPanel Column()
    {
        return new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Theme.Bg,
            Margin = new Padding(0),
        };
    }

    static Label Line(string text, float size, Color color, FontStyle style = FontStyle.Regular)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            Font = Theme.F(size, style),
            ForeColor = color,
            BackColor = Theme.Bg,
            Margin = new Padding(0),
        };
    }

    static Label Paragraph(string text, Color color, int topGap = 10)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(Theme.S(500), 0),
            Font = Theme.F(9.5f),
            ForeColor = color,
            BackColor = Theme.Bg,
            Margin = Theme.Pad(0, topGap, 0, 0),
        };
    }

    static FlowLayoutPanel ButtonRow()
    {
        return new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            BackColor = Theme.Bg,
            Margin = Theme.Pad(0, 16, 0, 0),
        };
    }

    static RoundButton Primary(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Padding = Theme.Pad(24, 10, 24, 10),
        Margin = Theme.Pad(10, 0, 0, 0),
        BackColor = Theme.Bg,
    };

    static RoundButton Secondary(string text)
    {
        var b = UiLayout.Secondary(text);
        b.BackColor = Theme.Bg;
        b.Padding = Theme.Pad(22, 10, 22, 10);
        b.Margin = Theme.Pad(10, 0, 0, 0);
        return b;
    }

    // ---------------------------------------------------------- 同名冲突

    static Form BuildConflict(string versionName, string path, Box<ConflictChoice> box)
    {
        var form = Shell("版本已存在", Theme.Sz(560, 250));
        var root = Root(form);

        var content = Column();
        content.Controls.Add(Line("这个版本名已经用过了", 12f, Theme.TextPrimary, FontStyle.Bold));
        content.Controls.Add(Paragraph(
            $"“{versionName}” 已存在于：{path}\n\n" +
            "· 覆盖安装：删掉现有版本目录后重新安装\n" +
            "· 重命名：另存为新名字，原版本保留", Theme.TextSecondary));
        root.Controls.Add(content, 0, 0);

        var row = ButtonRow();
        var btnCancel = Secondary("取消");
        var btnRename = Secondary("重命名");
        var btnOver = Primary("覆盖安装");
        btnCancel.Click += (_, _) => { box.Value = ConflictChoice.Cancel; form.Close(); };
        btnRename.Click += (_, _) => { box.Value = ConflictChoice.Rename; form.Close(); };
        btnOver.Click += (_, _) => { box.Value = ConflictChoice.Overwrite; form.Close(); };
        row.Controls.Add(btnCancel);
        row.Controls.Add(btnRename);
        row.Controls.Add(btnOver);
        root.Controls.Add(row, 0, 1);

        form.KeyPreview = true;
        form.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { box.Value = ConflictChoice.Cancel; form.Close(); }
            else if (e.KeyCode == Keys.Enter) { box.Value = ConflictChoice.Overwrite; form.Close(); }
        };
        return form;
    }

    /// <summary>版本名已存在：覆盖 / 重命名 / 取消。</summary>
    public static ConflictChoice AskConflict(IWin32Window owner, string versionName, string path)
    {
        var box = new Box<ConflictChoice>();
        using var form = BuildConflict(versionName, path, box);
        form.ShowDialog(owner);
        return box.Value;
    }

    // ---------------------------------------------------------- 重命名

    static string SuggestName(string versionsRoot, string baseName)
    {
        for (var i = 2; i < 1000; i++)
        {
            var cand = $"{baseName}-{i}";
            if (!Directory.Exists(Path.Combine(versionsRoot, cand))) return cand;
        }
        return $"{baseName}-{DateTime.Now:HHmmss}";
    }

    static Form BuildName(string versionsRoot, string baseName, Box<string> box)
    {
        var form = Shell("重命名版本", Theme.Sz(560, 214));
        var root = Root(form);

        var content = Column();
        content.Controls.Add(Line("给新版本起个名字", 12f, Theme.TextPrimary, FontStyle.Bold));

        var txt = new TextBox
        {
            Text = SuggestName(versionsRoot, baseName),
            Width = Theme.S(500),
            Font = Theme.F(11f),
            Margin = Theme.Pad(0, 12, 0, 0),
        };
        content.Controls.Add(txt);

        var err = Line("", 9f, Theme.Danger);
        err.Margin = Theme.Pad(0, 8, 0, 0);
        content.Controls.Add(err);
        root.Controls.Add(content, 0, 0);

        var row = ButtonRow();
        var btnCancel = Secondary("取消");
        var btnOk = Primary("确定");
        row.Controls.Add(btnCancel);
        row.Controls.Add(btnOk);
        root.Controls.Add(row, 0, 1);

        void Accept()
        {
            var name = txt.Text.Trim();
            if (name.Length == 0)
            {
                err.Text = "名字不能为空。";
                return;
            }
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Path.GetFileName(name) != name)
            {
                err.Text = "名字里不能包含 \\ / : * ? \" < > | 这类字符。";
                return;
            }
            if (Directory.Exists(Path.Combine(versionsRoot, name)))
            {
                err.Text = "这个名字也已经存在了，换一个。";
                return;
            }
            box.Value = name;
            form.Close();
        }

        btnOk.Click += (_, _) => Accept();
        btnCancel.Click += (_, _) => form.Close();
        form.KeyPreview = true;
        form.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) form.Close();
            else if (e.KeyCode == Keys.Enter) { e.Handled = true; Accept(); }
        };
        txt.SelectAll();
        form.Shown += (_, _) => txt.Focus();
        return form;
    }

    /// <summary>输入新版本名，返回 null 表示取消。</summary>
    public static string? AskName(IWin32Window owner, string versionsRoot, string baseName)
    {
        var box = new Box<string>();
        using var form = BuildName(versionsRoot, baseName, box);
        form.ShowDialog(owner);
        return box.Value;
    }

    // ---------------------------------------------------------- 自检截图

    /// <summary>开发用：把对话框画到位图，便于检查排版（--dialog 参数）。</summary>
    public static void Preview(string kind, string pngPath, string versionName, string versionsRoot)
    {
        Form form;
        if (kind == "rename")
        {
            form = BuildName(versionsRoot, versionName, new Box<string>());
        }
        else
        {
            form = BuildConflict(versionName, Path.Combine(versionsRoot, versionName), new Box<ConflictChoice>());
        }

        using (form)
        {
            form.StartPosition = FormStartPosition.Manual;
            form.TopMost = true;
            form.Show();
            Application.DoEvents();
            using var bmp = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
            bmp.Save(Path.GetFullPath(pngPath), System.Drawing.Imaging.ImageFormat.Png);
            form.Close();
        }
    }
}
