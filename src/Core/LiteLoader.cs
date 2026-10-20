using System.Text.Json.Nodes;

namespace PCLMCDL.Core;

/// <summary>
/// 【已废弃，不参与编译】LiteLoader 支持做到一半即停：dl.liteloader.com 数据停更、官方下载站不可用，
/// 上游 PCL 也已放弃。此处仅保留实现备查，csproj 中已 &lt;Compile Remove&gt; 排除，
/// 相关调用点（Installer / MainForm / Net / Paths）均已摘除。
/// 若要重启该功能：删掉 csproj 里的 Compile Remove，再重新接回上述调用点。
/// </summary>
/// <remarks>LiteLoader 仅支持 1.5.2–1.12.2。</remarks>
public static class LiteLoader
{
    static async Task<JsonObject> RootAsync(Source src, CancellationToken ct)
    {
        // dl.liteloader.com 是唯一数据源且响应极慢（10–40 秒抖动），放宽到 60 秒
        var node = await src.GetJsonAsync(src.LiteLoaderListUrls(), ct, 2, 60).ConfigureAwait(false);
        return node["versions"] as JsonObject ?? new JsonObject();
    }

    public static async Task<List<string>> ListMcVersionsAsync(Source src, CancellationToken ct)
    {
        var root = await RootAsync(src, ct).ConfigureAwait(false);
        return root.Select(kv => kv.Key).OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public sealed record Entry(string Version, string File, string? Url, string? RepoUrl,
        string Tweak, List<(string Name, string? Url)> Libs);

    /// <summary>取某 MC 版本下可用的 LiteLoader 条目。</summary>
    static async Task<List<Entry>> EntriesAsync(Source src, string mc, CancellationToken ct)
    {
        var root = await RootAsync(src, ct).ConfigureAwait(false);
        var list = new List<Entry>();
        if (root[mc] is not JsonObject mcNode) return list;

        // SNAPSHOT 条目没有直链 url，下载时拿 repo（maven2 仓库基址）去查 maven-metadata
        var repoUrl = (mcNode["repo"] as JsonObject)?["url"]?.GetValue<string>()?.TrimEnd('/');

        foreach (var section in new[] { "artefacts", "snapshots" })
        {
            if (mcNode[section]?["com.mumfrey:liteloader"] is not JsonObject artifacts) continue;
            foreach (var kv in artifacts)
            {
                if (kv.Value is not JsonObject o) continue;
                var ver = o["version"]?.GetValue<string>() ?? kv.Key;
                var file = o["file"]?.GetValue<string>();
                if (file is null) continue;
                var url = o["url"]?.GetValue<string>();
                if (url is null && repoUrl is null) continue;
                var tweak = o["tweakClass"]?.GetValue<string>() ?? "com.mumfrey.liteloader.launch.LiteLoaderTweaker";
                var libs = new List<(string, string?)>();
                if (o["libraries"] is JsonArray arr)
                {
                    foreach (var l in arr)
                        if (l?["name"]?.GetValue<string>() is { } n)
                            libs.Add((n, l?["url"]?.GetValue<string>()));
                }
                list.Add(new Entry(ver, file, url, repoUrl, tweak, libs));
            }
        }
        // 同一版本在 artefacts / snapshots 里会出现多次（如 1.12.2 的 hash 键与 latest 键），去重
        return list.DistinctBy(x => x.Version).ToList();
    }

    public static async Task<List<string>> ListVersionsAsync(Source src, string mc, CancellationToken ct)
        => (await EntriesAsync(src, mc, ct).ConfigureAwait(false)).Select(x => x.Version).ToList();

    public static async Task InstallAsync(
        Source src, string mcDir, string mc, string loaderVer, string name,
        Action<long, long>? onBytes, Action<string>? onLog, int workers, bool force, CancellationToken ct)
    {
        void Log(string s) => onLog?.Invoke(s);
        var vdir = Path.Combine(mcDir, "versions", name);
        if (File.Exists(Path.Combine(vdir, name + ".json")) && !force)
        {
            Log($"版本 {name} 已存在，跳过");
            return;
        }

        var entries = await EntriesAsync(src, mc, ct).ConfigureAwait(false);
        var entry = entries.FirstOrDefault(e => e.Version == loaderVer)
            ?? throw new InvalidOperationException(
                $"LiteLoader 没有 {mc} 的 {loaderVer} 版本（可用：{string.Join("、", entries.Select(x => x.Version).Distinct())}）");

        // SNAPSHOT 条目无直链：查 maven-metadata 拿真实文件名（liteloader-1.12.2-20171128.144431-4.jar）
        var jarUrl = entry.Url;
        if (jarUrl is null)
        {
            var baseUri = entry.RepoUrl! + "/com/mumfrey/liteloader/" + entry.Version + "/";
            var xml = System.Text.Encoding.UTF8.GetString(
                await src.GetBytesAsync(new[] { baseUri + "maven-metadata.xml" }, ct, 2, 30).ConfigureAwait(false));
            var ts = System.Text.RegularExpressions.Regex.Match(xml, "<timestamp>([^<]+)</timestamp>").Groups[1].Value;
            var bn = System.Text.RegularExpressions.Regex.Match(xml, "<buildNumber>([^<]+)</buildNumber>").Groups[1].Value;
            if (ts.Length == 0 || bn.Length == 0)
                throw new IOException($"无法解析 LiteLoader maven-metadata（{entry.Version}）");
            jarUrl = $"{baseUri}liteloader-{entry.Version.Replace("-SNAPSHOT", "")}-{ts}-{bn}.jar";
            Log($"SNAPSHOT 实际文件：{System.IO.Path.GetFileName(jarUrl)}");
        }

        var libPath = Paths.MavenPath($"com.mumfrey:liteloader:{entry.Version}");
        await src.DownloadAsync(new DlTask(new[] { jarUrl },
            Path.Combine(mcDir, "libraries", libPath), null, 0), ct, 2, onLog).ConfigureAwait(false);

        // launchwrapper + 附带库（mixin 等带自定义 maven 仓库 URL 的，优先走它）
        var tasks = new List<DlTask>();
        foreach (var (libName, libUrl) in entry.Libs.Append(("net.minecraft:launchwrapper:1.12", null)).Distinct())
        {
            var path = Paths.MavenPath(libName);
            var urls = libUrl is { Length: > 0 }
                ? new[] { libUrl.TrimEnd('/') + "/" + path }
                : src.LibraryUrls(path);
            tasks.Add(new DlTask(urls, Path.Combine(mcDir, "libraries", path), null, 0));
        }
        var failed = await src.DownloadManyAsync(tasks, workers, onBytes, onLog, ct).ConfigureAwait(false);
        if (failed.Count > 0)
            throw new InvalidOperationException(
                $"依赖库 {failed.Count} 个下载失败（{string.Join("、", failed.Take(3).Select(x => x.Split(':')[0]))}），已中止安装");

        var libs = new JsonArray { new JsonObject { ["name"] = $"com.mumfrey:liteloader:{entry.Version}" } };
        foreach (var (n, _) in entry.Libs) libs.Add(new JsonObject { ["name"] = n });

        var vj = new JsonObject
        {
            ["id"] = name,
            ["inheritsFrom"] = mc,
            ["clientVersion"] = mc,
            ["type"] = "release",
            ["time"] = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
            ["releaseTime"] = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
            ["mainClass"] = "net.minecraft.launchwrapper.Launch",
            ["minimumLauncherVersion"] = 18,
            ["libraries"] = libs,
            ["arguments"] = new JsonObject
            {
                ["game"] = new JsonArray { "--tweakClass", entry.Tweak },
            },
        };

        Directory.CreateDirectory(vdir);
        await File.WriteAllTextAsync(Path.Combine(vdir, name + ".json"),
            vj.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }), ct).ConfigureAwait(false);
        Log($"已写入 LiteLoader 版本 json（tweakClass={entry.Tweak}）");
    }
}
