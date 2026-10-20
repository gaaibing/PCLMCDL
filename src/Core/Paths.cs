using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCLMCDL.Core;

public sealed class Config
{
    public string? McDir { get; set; }
    public string? PclRoot { get; set; }
    public string? JavaPath { get; set; }
    public int Workers { get; set; } = 24;
    /// <summary>auto | official | mirror</summary>
    public string Source { get; set; } = "auto";

    public SourceMode Mode => Source switch
    {
        "official" => SourceMode.OfficialOnly,
        "mirror" => SourceMode.MirrorFirst,
        _ => SourceMode.Auto,
    };

    public Source NewSource() => new() { Mode = Mode };
}

public static class Paths
{
    public static string AppDir => AppContext.BaseDirectory;
    public static string ConfigPath => Path.Combine(AppDir, "pclmcdl.config.json");

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Config LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath)) ?? new Config();
        }
        catch { /* 配置损坏则重置 */ }
        return new Config();
    }

    public static void SaveConfig(Config cfg)
    {
        try { File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, JsonOpts)); }
        catch { /* 只读目录下忽略 */ }
    }

    /// <summary>清理上次运行中途退出留下的临时目录，避免残留堆积。</summary>
    public static void CleanupTemp()
    {
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "pclmcdl");
            if (!Directory.Exists(root)) return;
            foreach (var d in Directory.GetDirectories(root))
            {
                try { Directory.Delete(d, true); } catch { /* 被占用则跳过 */ }
            }
            foreach (var f in Directory.GetFiles(root, "*.part"))
            {
                try { File.Delete(f); } catch { /* ignore */ }
            }
        }
        catch { /* 清理失败不影响运行 */ }
    }

    static bool LooksLikeMc(string? p)
        => !string.IsNullOrEmpty(p) && Directory.Exists(Path.Combine(p, "versions"))
           && Directory.Exists(Path.Combine(p, "libraries"));

    /// <summary>目录下是否放着 PCL 主程序（任一常见文件名）。</summary>
    internal static bool HasPclExe(string dir)
    {
        foreach (var n in new[] { "Plain Craft Launcher 2.exe", "PCL.exe", "Plain Craft Launcher.exe" })
        {
            try { if (File.Exists(Path.Combine(dir, n))) return true; }
            catch { /* 无权限忽略 */ }
        }
        return false;
    }

    /// <summary>
    /// 探测 .minecraft 目录。给了 hint 就严格按 hint 走，不参与自动探测的竞争：
    /// 命中含 versions 的目录直接返回；目录存在但还没有 versions 就补建一个；路径不存在返回 null，
    /// 由调用方报错——绝不静默回落到别的实例（那会把版本装进用户没指定的目录）。
    /// 不给 hint 时取已安装版本最多的那个。
    /// </summary>
    public static string? FindMcDir(string? hint = null)
    {
        if (!string.IsNullOrWhiteSpace(hint))
        {
            string full;
            try { full = System.IO.Path.GetFullPath(hint!); }
            catch { return null; }

            // 用户可能给 .minecraft 本身，也可能给包含它的 PCL 根目录
            foreach (var cand in new[] { full, System.IO.Path.Combine(full, ".minecraft") })
                if (Directory.Exists(System.IO.Path.Combine(cand, "versions"))) return cand;

            // 半自动：目录存在但不是 .minecraft，补建 versions 直接装进去
            if (Directory.Exists(full))
            {
                try
                {
                    // PCL / 官方启动器都读 <根>\.minecraft：只要它存在、或根下放着 PCL 主程序，
                    // 就把 versions 建进 .minecraft，否则版本会装到启动器看不到的地方
                    var root = Directory.Exists(System.IO.Path.Combine(full, ".minecraft")) || HasPclExe(full)
                        ? System.IO.Path.Combine(full, ".minecraft")
                        : full;
                    Directory.CreateDirectory(System.IO.Path.Combine(root, "versions"));
                    return root;
                }
                catch { return null; }
            }

            return null;
        }

        var all = FindAllMcDirs(null);
        return all.Count > 0 ? all[0].Path : null;
    }

    public static string? FindPclExe(string? hint = null)
    {
        string[] names = { "Plain Craft Launcher 2.exe", "PCL.exe", "Plain Craft Launcher.exe" };
        var roots = new List<string?>();
        if (!string.IsNullOrWhiteSpace(hint))
        {
            roots.Add(hint);
            roots.Add(Path.GetDirectoryName(hint.TrimEnd(Path.DirectorySeparatorChar)));
        }
        roots.Add(LoadConfig().PclRoot);
        roots.Add(@"C:\minecraft");

        foreach (var r in roots)
        {
            if (string.IsNullOrEmpty(r)) continue;
            foreach (var n in names)
            {
                var p = Path.Combine(r, n);
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }

    /// <summary>按 PCL 习惯拼版本目录名，例如 1.21.8-NeoForge_21.8.53。</summary>
    public static string VersionName(string mc, string loader = "vanilla", string version = "", string optifine = "")
    {
        var name = loader switch
        {
            "fabric" => $"{mc}-Fabric {version}",
            "forge" => $"{mc}-Forge_{version}",
            // NeoForge 1.20.1 的版本号自带 MC 前缀（1.20.1-47.1.105），目录名里去掉
            "neoforge" => $"{mc}-NeoForge_{(version.StartsWith(mc + "-", StringComparison.Ordinal) ? version[(mc.Length + 1)..] : version)}",
            "optifine" => $"{mc}-OptiFine_{version}",
            _ => mc,
        };
        if (!string.IsNullOrEmpty(optifine) && loader != "optifine")
            name += $"-OptiFine_{optifine}";
        return name;
    }

    /// <summary>列出已安装版本目录名。</summary>
    public static List<string> InstalledVersions(string mcDir)
    {
        var dir = Path.Combine(mcDir, "versions");
        if (!Directory.Exists(dir)) return new List<string>();
        return Directory.GetDirectories(dir)
            .Select(Path.GetFileName)
            .Where(n => n is not null && File.Exists(Path.Combine(dir, n!, n + ".json")))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---------------------------------------------------------- Java

    public static int? JavaMajorPublic(string exe) => JavaMajor(exe);

    static int? JavaMajor(string exe)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, "-version")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var text = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            foreach (var line in text.Split('\n'))
            {
                var i = line.IndexOf('"');
                if (i < 0) continue;
                var j = line.IndexOf('"', i + 1);
                if (j < 0) continue;
                var v = line[(i + 1)..j];
                var parts = v.Split('.');
                if (parts[0] == "1" && parts.Length > 1) return int.TryParse(parts[1], out var m1) ? m1 : null;
                return int.TryParse(parts[0], out var m2) ? m2 : null;
            }
        }
        catch { /* ignore */ }
        return null;
    }

    /// <summary>找一个满足最低版本要求的 java.exe。preferred 指定的路径优先。</summary>
    public static (string Exe, int Major)? FindJava(int minMajor = 8, string? preferred = null)
    {
        if (!string.IsNullOrWhiteSpace(preferred) && File.Exists(preferred))
        {
            var m = JavaMajor(preferred);
            if (m is not null && m >= minMajor) return (preferred!, m.Value);
        }
        var hit = FindAllJavas().FirstOrDefault(x => x.Major >= minMajor);
        return hit is null ? null : (hit.Exe, hit.Major);
    }

    public static string? FindJavac(string javaExe)
    {
        var p = Path.Combine(Path.GetDirectoryName(javaExe)!, "javac.exe");
        return File.Exists(p) ? p : null;
    }

    /// <summary>把 maven 坐标 group:artifact:version[:classifier][@ext] 转成仓库相对路径。</summary>
    public static string MavenPath(string name)
    {
        var ext = "jar";
        var at = name.IndexOf('@');
        if (at >= 0)
        {
            ext = name[(at + 1)..];
            name = name[..at];
        }
        var parts = name.Split(':');
        if (parts.Length < 3) return name;
        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 && parts[3].Length > 0 ? "-" + parts[3] : "";
        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.{ext}";
    }

    // ---------------------------------------------------------- 多目录扫描

    public sealed record McCandidate(string Path, string Source, int VersionCount);
    public sealed record JavaCandidate(string Exe, int Major, string Source);

    static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "Users",
        "$Recycle.Bin", "System Volume Information", "Recovery", "PerfLogs",
        "node_modules", ".git", "AppData", "Windows.old", "Config.Msi",
    };

    /// <summary>并行扫描所有已知位置，返回按版本数降序的 .minecraft 列表。毫秒级。</summary>
    public static List<McCandidate> FindAllMcDirs(string? hint = null)
    {
        var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localapp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var probes = new List<(string Path, string Source)>();

        void Probe(string? p, string source)
        {
            if (!string.IsNullOrWhiteSpace(p)) probes.Add((p!, source));
        }

        Probe(hint, "当前设置");
        // 允许直接传 PCL 根目录（其下才是 .minecraft），自动补一层
        if (!string.IsNullOrWhiteSpace(hint))
            Probe(System.IO.Path.Combine(hint!, ".minecraft"), "当前设置");
        Probe(LoadConfig().McDir, "上次使用");
        Probe(System.IO.Path.Combine(AppDir, ".minecraft"), "程序目录");
        Probe(System.IO.Path.Combine(Environment.CurrentDirectory, ".minecraft"), "工作目录");
        Probe(System.IO.Path.Combine(appdata, ".minecraft"), "官方启动器");
        Probe(System.IO.Path.Combine(home, ".minecraft"), "用户目录");
        Probe(System.IO.Path.Combine(appdata, "Minecraft"), "官方启动器(旧)");
        Probe(System.IO.Path.Combine(localapp, "Minecraft"), "官方启动器(旧)");

        // 各固定磁盘：根目录 + 一层子目录
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                var root = drive.RootDirectory.FullName;
                var tag = drive.Name.TrimEnd('\\', '/');
                Probe(System.IO.Path.Combine(root, ".minecraft"), $"{tag} 根目录");

                foreach (var sub in Directory.EnumerateDirectories(root))
                {
                    var name = System.IO.Path.GetFileName(sub);
                    if (SkipNames.Contains(name)) continue;
                    Probe(System.IO.Path.Combine(sub, ".minecraft"), $"{tag} / {name}");
                }
            }
            catch { /* 无权限的盘跳过 */ }
        }

        // 启动器实例目录
        foreach (var (baseDir, label) in new[]
                 {
                     (System.IO.Path.Combine(appdata, "PrismLauncher", "instances"), "PrismLauncher"),
                     (System.IO.Path.Combine(appdata, "MultiMC", "instances"), "MultiMC"),
                     (System.IO.Path.Combine(appdata, "ATLauncher", "instances"), "ATLauncher"),
                     (System.IO.Path.Combine(appdata, "gdlauncher_next", "instances"), "GDLauncher"),
                     (System.IO.Path.Combine(localapp, "Programs", "PrismLauncher", "instances"), "PrismLauncher"),
                     (System.IO.Path.Combine(home, "curseforge", "minecraft", "Instances"), "CurseForge"),
                     (@"C:\minecraft\PrismLauncher\instances", "PrismLauncher"),
                     (@"C:\minecraft\MultiMC\instances", "MultiMC"),
                 })
        {
            try
            {
                if (!Directory.Exists(baseDir)) continue;
                foreach (var inst in Directory.EnumerateDirectories(baseDir))
                {
                    var name = System.IO.Path.GetFileName(inst);
                    foreach (var sub in new[] { ".minecraft", "minecraft", "game" })
                        Probe(System.IO.Path.Combine(inst, sub), $"{label} / {name}");
                    Probe(inst, $"{label} / {name}");
                }
            }
            catch { /* ignore */ }
        }

        var results = new List<McCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gate = new object();

        Parallel.ForEach(probes, p =>
        {
            string full;
            try { full = System.IO.Path.GetFullPath(p.Path); }
            catch { return; }

            lock (gate) { if (!seen.Add(full)) return; }

            if (!Directory.Exists(full)) return;

            var versionsDir = System.IO.Path.Combine(full, "versions");
            if (!Directory.Exists(versionsDir) && !IsEmptyGameDir(full, p.Source)) return;

            var count = 0;
            try { count = Directory.GetDirectories(versionsDir).Length; } catch { /* ignore */ }

            var label = DetectLauncher(full) ?? p.Source;
            lock (gate) results.Add(new McCandidate(full, label, count));
        });

        return results.OrderByDescending(x => x.VersionCount).ThenBy(x => x.Path).ToList();
    }

    /// <summary>
    /// 没有 versions 的空目录是否仍算候选。只在三种情况下放行：名字就是游戏目录、
    /// 来自显式设置、带 PCL 特征——否则全盘每个文件夹都会被列进来。
    /// </summary>
    static bool IsEmptyGameDir(string dir, string source)
    {
        var name = System.IO.Path.GetFileName(dir.TrimEnd('\\', '/'));
        if (name.Equals(".minecraft", StringComparison.OrdinalIgnoreCase)
            || name.Equals("minecraft", StringComparison.OrdinalIgnoreCase)
            || name.Equals("game", StringComparison.OrdinalIgnoreCase)) return true;

        if (source is "当前设置" or "上次使用" or "程序目录" or "工作目录") return true;

        try
        {
            return HasPclExe(dir)
                   || File.Exists(System.IO.Path.Combine(dir, "PCL.ini"))
                   || Directory.Exists(System.IO.Path.Combine(dir, "PCL"));
        }
        catch { return false; }
    }

    /// <summary>按目录特征判断它属于哪个启动器，识别不出返回 null。</summary>
    static string? DetectLauncher(string mcDir)
    {
        try
        {
            var dir = mcDir.TrimEnd('\\', '/');
            var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            // 官方启动器：精确路径
            if (string.Equals(dir, System.IO.Path.Combine(appdata, ".minecraft"),
                    StringComparison.OrdinalIgnoreCase))
                return "官启";

            // PCL：上级放着 PCL.ini / PCL 主程序 / PCL 文件夹，或游戏目录内有 PCL.ini、PCL 文件夹
            var parent = System.IO.Path.GetDirectoryName(dir);
            if (!string.IsNullOrEmpty(parent)
                && (File.Exists(System.IO.Path.Combine(parent, "PCL.ini"))
                    || File.Exists(System.IO.Path.Combine(parent, "Plain Craft Launcher 2.exe"))
                    || File.Exists(System.IO.Path.Combine(parent, "Plain Craft Launcher.exe"))
                    || File.Exists(System.IO.Path.Combine(parent, "PCL.exe"))
                    || Directory.Exists(System.IO.Path.Combine(parent, "PCL"))))
                return "PCL";

            if (File.Exists(System.IO.Path.Combine(dir, "PCL.ini"))
                || Directory.Exists(System.IO.Path.Combine(dir, "PCL")))
                return "PCL";

            // 其他启动器按路径特征识别
            if (dir.Contains("PrismLauncher", StringComparison.OrdinalIgnoreCase)) return "Prism";
            if (dir.Contains("MultiMC", StringComparison.OrdinalIgnoreCase)) return "MultiMC";
            if (dir.Contains("ATLauncher", StringComparison.OrdinalIgnoreCase)) return "ATLauncher";
            if (dir.Contains("gdlauncher", StringComparison.OrdinalIgnoreCase)) return "GDLauncher";
            if (dir.Contains("curseforge", StringComparison.OrdinalIgnoreCase)) return "CurseForge";
        }
        catch { /* ignore */ }
        return null;
    }

    /// <summary>并行扫描所有 Java，按主版本降序。</summary>
    public static List<JavaCandidate> FindAllJavas(string? mcDir = null)
    {
        var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localapp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var probes = new List<(string Path, string Source)>();

        void Probe(string? p, string source)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            try { if (File.Exists(p)) probes.Add((p!, source)); } catch { /* ignore */ }
        }

        var cfg = LoadConfig();
        Probe(cfg.JavaPath, "手动指定");

        var jh = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(jh)) Probe(System.IO.Path.Combine(jh, "bin", "java.exe"), "JAVA_HOME");

        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(';', StringSplitOptions.RemoveEmptyEntries))
            Probe(System.IO.Path.Combine(d.Trim('"'), "java.exe"), "PATH");

        foreach (var (baseDir, label) in new[]
                 {
                     (@"C:\Program Files\Java", "Oracle"),
                     (@"C:\Program Files\Eclipse Adoptium", "Adoptium"),
                     (@"C:\Program Files\Microsoft", "Microsoft"),
                     (@"C:\Program Files\Zulu", "Zulu"),
                     (@"C:\Program Files\Amazon Corretto", "Corretto"),
                     (@"C:\Program Files\BellSoft", "Liberica"),
                     (@"C:\Program Files\Semeru", "Semeru"),
                     (@"C:\Program Files\JetBrains\Runtime", "JetBrains"),
                     (@"C:\Program Files\AdoptOpenJDK", "AdoptOpenJDK"),
                     (System.IO.Path.Combine(localapp, "Programs", "Eclipse Adoptium"), "Adoptium"),
                     (System.IO.Path.Combine(localapp, "Programs", "Microsoft"), "Microsoft"),
                     (System.IO.Path.Combine(localapp, "JetBrains", "Toolbox", "apps"), "JetBrains"),
                     (System.IO.Path.Combine(appdata, "PCL"), "PCL 内置"),
                 })
        {
            try
            {
                if (!Directory.Exists(baseDir)) continue;
                foreach (var d in Directory.EnumerateDirectories(baseDir))
                    Probe(System.IO.Path.Combine(d, "bin", "java.exe"), label);
            }
            catch { /* ignore */ }
        }

        // 官方启动器自带的 JRE：.minecraft\runtime\java-runtime-*\windows-x64\*\bin\java.exe
        if (!string.IsNullOrWhiteSpace(mcDir))
        {
            try
            {
                var rt = System.IO.Path.Combine(mcDir, "runtime");
                if (Directory.Exists(rt))
                {
                    foreach (var f in Directory.EnumerateFiles(rt, "java.exe", SearchOption.AllDirectories))
                        Probe(f, "游戏内置");
                }
            }
            catch { /* ignore */ }
        }

        var results = new List<JavaCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gate = new object();

        var unique = new List<(string Path, string Source)>();
        foreach (var p in probes)
        {
            // javapath / java8path 只是转发器，会与真实 JDK 重复
            if (p.Path.Contains("javapath", StringComparison.OrdinalIgnoreCase)) continue;
            if (p.Path.Contains("java8path", StringComparison.OrdinalIgnoreCase)) continue;
            string full;
            try { full = System.IO.Path.GetFullPath(p.Path); }
            catch { continue; }
            lock (gate) { if (seen.Add(full)) unique.Add((full, p.Source)); }
        }

        Parallel.ForEach(unique, u =>
        {
            var major = JavaMajor(u.Path);
            if (major is null) return;
            lock (gate) results.Add(new JavaCandidate(u.Path, major.Value, u.Source));
        });

        return results.OrderByDescending(x => x.Major).ThenBy(x => x.Exe).ToList();
    }
}
