using System.Text.Json.Nodes;

namespace PCLMCDL.Core;

public sealed class InstallRequest
{
    public string McVersion { get; set; } = "";
    public string Loader { get; set; } = "vanilla";
    public string LoaderVersion { get; set; } = "";
    public string OptiFinePatch { get; set; } = "";
    public OptiFine.Entry? OptiFineEntry { get; set; }
    public bool Force { get; set; }
}

/// <summary>统一调度：按加载器类型分派到对应安装器。</summary>
public static class Installer
{
    /// <summary>最终版本名（含 OptiFine 后缀）。</summary>
    public static string BuildName(InstallRequest r)
    {
        var of = r.Loader == "optifine" ? "" : r.OptiFinePatch;
        var ver = r.Loader == "optifine"
            ? r.OptiFineEntry?.Patch ?? r.LoaderVersion
            : r.LoaderVersion;
        return Paths.VersionName(r.McVersion, r.Loader, ver, of);
    }

    /// <summary>加载器本身的版本名（不含 OptiFine 后缀），作为叠加时的继承基座。</summary>
    static string BaseName(InstallRequest r)
    {
        var ver = r.Loader == "optifine"
            ? r.OptiFineEntry?.Patch ?? r.LoaderVersion
            : r.LoaderVersion;
        return Paths.VersionName(r.McVersion, r.Loader, ver, "");
    }

    public static async Task<string> RunAsync(
        Source src,
        string mcDir,
        InstallRequest req,
        Action<long, long>? onBytes,
        Action<string>? onLog,
        int workers,
        CancellationToken ct)
    {
        void Log(string s) => onLog?.Invoke(s);
        var hasOf = req.Loader != "optifine" && !string.IsNullOrEmpty(req.OptiFinePatch)
                    && req.OptiFineEntry is not null;
        var baseName = BaseName(req);
        Log($"目标版本名：{BuildName(req)}");

        // Forge / NeoForge / 新版 OptiFine 靠跑 Java 安装器收尾，缺 Java 时别等下载了几百 MB 才报错
        var needJava =
            (req.Loader is "forge" or "neoforge" && Forge.NeedsJava(req.LoaderVersion, req.Loader == "neoforge"))
            || ((req.Loader == "optifine" || hasOf) && !OptiFine.IsLegacyFor(req.McVersion));
        if (needJava && Paths.FindJava(8, Paths.LoadConfig().JavaPath) is null)
        {
            var what = req.Loader switch { "neoforge" => "NeoForge", "forge" => "Forge", _ => "OptiFine" };
            throw new InvalidOperationException(
                $"{what} 需要本机安装 Java 8 或更高版本，当前未检测到可用的 Java。" +
                "请装好 Java 后在「设置」里指定 java.exe；原版和 Fabric 不需要 Java，可先用它们。");
        }

        switch (req.Loader)
        {
            case "fabric":
            {
                var patch = await Fabric.GetPatchAsync(src, req.McVersion, req.LoaderVersion, ct).ConfigureAwait(false);
                await Fabric.DownloadLibrariesAsync(src, mcDir, patch, onBytes, onLog, workers, ct).ConfigureAwait(false);
                await Vanilla.InstallAsync(src, mcDir, req.McVersion, baseName,
                    new[] { patch }, onBytes, onLog, workers, req.Force, ct).ConfigureAwait(false);
                break;
            }
            case "forge":
            case "neoforge":
            {
                await EnsureVanillaAsync(src, mcDir, req.McVersion, onBytes, onLog, workers, ct).ConfigureAwait(false);
                await Forge.InstallAsync(src, mcDir, req.McVersion, req.LoaderVersion, baseName,
                    req.Loader == "neoforge", onBytes, onLog, workers, req.Force, ct).ConfigureAwait(false);
                break;
            }
            case "optifine":
            {
                await EnsureVanillaAsync(src, mcDir, req.McVersion, onBytes, onLog, workers, ct).ConfigureAwait(false);
                var entry = req.OptiFineEntry ?? throw new InvalidOperationException("未选择 OptiFine 版本");
                await OptiFine.InstallAsync(src, mcDir, req.McVersion, entry, baseName,
                    onBytes, onLog, workers, req.Force, ct).ConfigureAwait(false);
                break;
            }
            default:
            {
                await Vanilla.InstallAsync(src, mcDir, req.McVersion, baseName,
                    null, onBytes, onLog, workers, req.Force, ct).ConfigureAwait(false);
                break;
            }
        }

        // OptiFine 叠加：独立版本，继承链指向已装好的加载器版本
        if (hasOf)
        {
            var finalName = BuildName(req);
            Log($"叠加 OptiFine {req.OptiFinePatch}，继承自 {baseName}");
            await OptiFine.InstallAsync(src, mcDir, req.McVersion, req.OptiFineEntry!, finalName,
                onBytes, onLog, workers, req.Force, ct, inheritFrom: baseName).ConfigureAwait(false);
            return finalName;
        }

        return baseName;
    }

    /// <summary>
    /// 把已安装的版本目录换名，并同步 json 里的 id。返回最终的名字。
    /// move=false 时改为复制：本次安装其实是「跳过」旧版本，不能把用户原来的版本搬走。
    /// </summary>
    public static string RenameVersion(string mcDir, string from, string to, Action<string>? log = null, bool move = true)
    {
        if (string.IsNullOrEmpty(to) || string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return from;

        var versions = Path.Combine(mcDir, "versions");
        var srcDir = Path.Combine(versions, from);
        var dstDir = Path.Combine(versions, to);
        if (!Directory.Exists(srcDir)) return from;

        if (Directory.Exists(dstDir)) Directory.Delete(dstDir, true);
        if (move) Directory.Move(srcDir, dstDir);
        else CopyDir(srcDir, dstDir);

        var oldJson = Path.Combine(dstDir, from + ".json");
        if (File.Exists(oldJson))
        {
            var newJson = Path.Combine(dstDir, to + ".json");
            var vj = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(oldJson))!.AsObject();
            vj["id"] = to;
            File.WriteAllText(newJson, vj.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
            if (!string.Equals(newJson, oldJson, StringComparison.OrdinalIgnoreCase)) File.Delete(oldJson);
        }

        log?.Invoke($"{(move ? "已重命名为" : "已复制为新版本")} {to}");
        return to;
    }

    static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest, true);
        }
    }

    /// <summary>并行探测某 MC 版本可用的加载器。单个接口 8 秒超时，超时按「可用」处理，避免误隐藏。</summary>
    public static async Task<(List<string> Available, List<OptiFine.Entry> OptiFine)> ProbeLoadersAsync(
        Source src, string mc, CancellationToken ct)
    {
        async Task<bool> ProbeAsync(Func<CancellationToken, Task<int>> countFn)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                return await countFn(cts.Token).ConfigureAwait(false) > 0;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return true; // 超时不下结论，先显示出来
            }
            catch
            {
                return false;
            }
        }

        async Task<List<OptiFine.Entry>> OptiFineAsync()
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            try { return await OptiFine.ListAsync(src, mc, cts.Token).ConfigureAwait(false); }
            catch { return new List<OptiFine.Entry>(); }
        }

        var ofTask = OptiFineAsync();
        var fabricTask = FabricLikely(mc)
            ? ProbeAsync(async t => (await Fabric.ListLoadersAsync(src, mc, t).ConfigureAwait(false)).Count)
            : Task.FromResult(false);
        var forgeTask = ProbeAsync(async t => (await Forge.ListForgeAsync(src, mc, t).ConfigureAwait(false)).Count);
        var neoTask = NeoForgeLikely(mc)
            ? ProbeAsync(async t => (await Forge.ListNeoForgeAsync(src, mc, t).ConfigureAwait(false)).Count)
            : Task.FromResult(false);

        await Task.WhenAll(fabricTask, forgeTask, neoTask, ofTask).ConfigureAwait(false);

        var of = ofTask.Result;
        var list = new List<string>();
        if (fabricTask.Result) list.Add("Fabric");
        if (forgeTask.Result) list.Add("Forge");
        if (neoTask.Result) list.Add("NeoForge");
        if (of.Count > 0) list.Add("OptiFine");
        return (list, of);
    }

    /// <summary>Fabric 从 1.14 开始支持。</summary>
    static bool FabricLikely(string mc)
    {
        var p = mc.Split('.');
        if (p[0] != "1") return true; // 26.x 这类新命名
        return int.TryParse(p[1], out var minor) && minor >= 14;
    }

    /// <summary>NeoForge 从 1.20.1 开始支持。</summary>
    static bool NeoForgeLikely(string mc)
    {
        var p = mc.Split('.');
        if (p[0] != "1") return true;
        if (!int.TryParse(p[1], out var minor)) return false;
        if (minor > 20) return true;
        if (minor < 20) return false;
        return p.Length >= 3 && int.TryParse(p[2], out var patch) && patch >= 1;
    }

    /// <summary>确保原版版本目录存在（Forge/OptiFine 安装器的前置条件）。</summary>
    static async Task EnsureVanillaAsync(Source src, string mcDir, string mc,
        Action<long, long>? onBytes, Action<string>? onLog, int workers, CancellationToken ct)
    {
        var vdir = Path.Combine(mcDir, "versions", mc);
        if (File.Exists(Path.Combine(vdir, mc + ".json")) && File.Exists(Path.Combine(vdir, mc + ".jar")))
            return;
        onLog?.Invoke($"原版 {mc} 尚未安装，先补全");
        await Vanilla.InstallAsync(src, mcDir, mc, mc, null, onBytes, onLog, workers, false, ct)
            .ConfigureAwait(false);
    }
}
