using System.Text;
using PCLMCDL.Core;

namespace PCLMCDL;

/// <summary>命令行模式：PCLMCDL.exe install &lt;mc&gt; [--loader fabric] [--ver 0.19.3] [--of E4] [--dir 路径] [--force]</summary>
public static class Cli
{
    static readonly StringBuilder Buf = new();
    static readonly object Gate = new();

    public static void Log(string s)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {s}";
        lock (Gate) Buf.AppendLine(line);
        Console.Error.WriteLine(line);
    }

    public static int Run(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 无控制台时忽略 */ }
        try
        {
            return RunAsync(args).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Log("[失败] " + e.Message);
            return 1;
        }
    }

    static async Task<int> RunAsync(string[] args)
    {
        var cmd = args[0].ToLowerInvariant();
        if (cmd is "-h" or "--help" or "help")
        {
            PrintHelp();
            return 0;
        }
        if (cmd is "list")
        {
            var src0 = new Source();
            var man = await Vanilla.FetchManifestAsync(src0, default, Log);
            var arr = man["versions"] as System.Text.Json.Nodes.JsonArray ?? new();
            foreach (var n in arr.Take(30))
                Log($"{n?["id"]}  {n?["type"]}");
            return 0;
        }
        if (cmd != "install")
        {
            PrintHelp();
            return 2;
        }

        var req = new InstallRequest();
        string? dir = null;
        string? customName = null;
        var mode = Paths.LoadConfig().Mode;
        var workers = 0;

        for (var i = 1; i < args.Length; i++)
        {
            var a = args[i];
            string Value()
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"选项 {a} 缺少参数值");
                return args[++i];
            }
            switch (a)
            {
                case "--loader": req.Loader = Value().ToLowerInvariant(); break;
                case "--ver": req.LoaderVersion = Value(); break;
                case "--of":
                {
                    var patch = Value();
                    if (req.Loader == "optifine") req.LoaderVersion = patch;
                    else req.OptiFinePatch = patch;
                    break;
                }
                case "--dir": dir = Value(); break;
                case "--name": customName = Value(); break;
                case "--force": req.Force = true; break;
                case "--workers":
                {
                    var w = Value();
                    if (!int.TryParse(w, out workers) || workers <= 0)
                        throw new ArgumentException($"--workers 需要正整数，收到：{w}");
                    break;
                }
                case "--source": mode = Value().ToLowerInvariant() switch { "official" => SourceMode.OfficialOnly, "mirror" => SourceMode.MirrorFirst, _ => SourceMode.Auto }; break;
                case "--no-mirror": mode = SourceMode.OfficialOnly; break;
                default:
                    if (req.McVersion.Length == 0) req.McVersion = a;
                    break;
            }
        }

        if (req.McVersion.Length == 0)
        {
            PrintHelp();
            return 2;
        }

        if (workers <= 0) workers = Paths.LoadConfig().Workers;
        var mcDir = Paths.FindMcDir(dir);
        if (mcDir is null)
        {
            if (dir is null)
                Log("找不到 .minecraft 目录，请用 --dir 指定");
            else
                Log($"指定的目录不存在或无法访问：{dir}\n请检查路径，或省略 --dir 让程序自动探测");
            return 3;
        }
        Log($"游戏目录：{mcDir}");

        var src = new Source { Mode = mode };

        // OptiFine 需要解析具体条目
        if (req.Loader == "optifine" || req.OptiFinePatch.Length > 0)
        {
            var list = await OptiFine.ListAsync(src, req.McVersion, default);
            var patch = req.Loader == "optifine" ? req.LoaderVersion : req.OptiFinePatch;
            if (patch.Length == 0)
            {
                // 未指定版本时取列表第一项（最新正式版）
                var first = list.FirstOrDefault()
                    ?? throw new InvalidOperationException($"OptiFine 没有 {req.McVersion} 的版本");
                req.OptiFineEntry = first;
                if (req.Loader == "optifine") req.LoaderVersion = first.Patch;
                else req.OptiFinePatch = first.Patch;
                Log($"未指定 OptiFine 版本，自动选择 {first.Patch}");
            }
            else
            {
                req.OptiFineEntry = list.FirstOrDefault(x => x.Patch == patch)
                    ?? throw new InvalidOperationException($"OptiFine 没有 {req.McVersion} 的 {patch}");
            }
        }

        var name = Installer.BuildName(req);

        // 同名版本已存在时，命令行里给三个选项：--force 覆盖 / --name 另存 / 直接退出
        var finalName = string.IsNullOrWhiteSpace(customName) ? name : customName.Trim();
        var existingDir = Path.Combine(mcDir, "versions", finalName);
        if (Directory.Exists(existingDir) && !req.Force)
        {
            throw new InvalidOperationException(
                $"版本 {finalName} 已存在：\n" +
                $"  --force              删除现有版本后覆盖安装\n" +
                $"  --name <新名字>       另存为新名字，原版本保留\n" +
                "  不加参数              什么都不做（当前）");
        }
        Log($"目标版本：{finalName}" + (req.Force && Directory.Exists(existingDir) ? "（覆盖安装）" : ""));

        // 记录默认名目录是否本来就存在：已存在说明本次会「跳过」，改名只能复制
        var defaultExisted = Directory.Exists(Path.Combine(mcDir, "versions", name));

        long last = 0;
        var name2 = await Installer.RunAsync(src, mcDir, req,
            (d, t) =>
            {
                if (t > 0 && d - last > 20 * 1048576)
                {
                    last = d;
                    Log($"  {d / 1048576.0:F1} / {t / 1048576.0:F1} MB");
                }
            },
            Log, workers, default);

        if (finalName != name2)
            name2 = Installer.RenameVersion(mcDir, name2, finalName, Log, move: !defaultExisted);
        Log($"完成：{name2}");
        return 0;
    }

    static void PrintHelp()
    {
        Log("""
            PCL 社区MCDL版 命令行（PCLMCDL.exe）
              PCLMCDL.exe install <MC版本> [选项]
                --loader  vanilla|fabric|forge|neoforge|optifine
                --ver     <加载器版本>
                --of      <OptiFine 补丁号，如 E4>
                --dir     <.minecraft 或 PCL 目录>
                --name    <自定义版本名>
                --force   同名时删除现有版本后覆盖安装
                --workers 并发数（默认 24）
                --no-mirror  仅用官方源
              PCLMCDL.exe list     列出前 30 个版本
            """);
    }
}
