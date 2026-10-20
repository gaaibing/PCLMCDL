using PCLMCDL.Core;

namespace PCLMCDL;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        HookExceptions();
        Paths.CleanupTemp();

        // 开发用：给对话框截图，检查排版
        if (args.Length > 0 && args[0] == "--dialog")
        {
            var kind = args.Length > 1 ? args[1] : "conflict";
            var png = args.Length > 2 ? args[2] : "dialog.png";
            var ver = args.Length > 3 ? args[3] : "1.20.4-Forge_49.0.9";
            PCLMCDL.Ui.Dialogs.Preview(kind, png, ver, @"C:\minecraft\.minecraft\versions");
            return;
        }

        // 开发用：启动后截取窗口自身，便于检查布局
        if (args.Length > 0 && args[0] == "--shot")
        {
            Shot(args.Length > 1 ? args[1] : "shot.png",
                 args.Length > 2 && args[2] == "settings",
                 args.Length > 2 && args[2] == "pick");
            return;
        }

        if (args.Length > 0)
        {
            Environment.Exit(Cli.Run(args));
            return;
        }

        Application.Run(new MainForm());
    }

    /// <summary>兜住所有未处理异常，给出提示并落盘，避免无声退出。</summary>
    static void HookExceptions()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Report(e.Exception);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Report(ex, fatal: true);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            System.Diagnostics.Debug.WriteLine(e.Exception);
        };
    }

    static void Report(Exception ex, bool fatal = false)
    {
        var log = WriteCrashLog(ex);
        var tip = log is null ? "详细信息无法写入磁盘。" : "详细信息已写入：\n" + log;
        MessageBox.Show(
            $"程序遇到未处理的问题：\n{ex.Message}\n\n{tip}",
            "PCLMCDL",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
        if (fatal) Environment.Exit(1);
    }

    static string? WriteCrashLog(Exception ex)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            var p = Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(p, ex.ToString());
            return p;
        }
        catch
        {
            try
            {
                var p = Path.Combine(Path.GetTempPath(), $"pclmcdl-crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                File.WriteAllText(p, ex.ToString());
                return p;
            }
            catch
            {
                return null;
            }
        }
    }

    static void Shot(string path, bool settings = false, bool pick = false)
    {
        var form = new MainForm { TopMost = true };
        if (settings) form.Shown += (_, _) => form.ShowSettings();
        if (pick) form.Shown += async (_, _) => { await Task.Delay(1800); form.AutoPickFirstVersion(); };
        form.Shown += async (_, _) =>
        {
            form.Activate();
            await Task.Delay(pick ? 9000 : 4200);
            try
            {
                form.Refresh();
                using var bmp = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                bmp.Save(Path.GetFullPath(path), System.Drawing.Imaging.ImageFormat.Png);
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message, "截图失败");
            }
            form.Close();
        };
        Application.Run(form);
    }
}
