using System.Text.Json.Nodes;

namespace PCLMCDL.Core;

/// <summary>Fabric：meta API 取 loader 列表与 profile json。</summary>
public static class Fabric
{
    public sealed record LoaderEntry(string Version, bool Stable);

    public static async Task<List<LoaderEntry>> ListLoadersAsync(Source src, string mc, CancellationToken ct)
    {
        var node = await src.GetJsonAsync(src.FabricMetaUrls($"v2/versions/loader/{mc}"), ct).ConfigureAwait(false);
        var list = new List<LoaderEntry>();
        if (node is not JsonArray arr) return list;
        foreach (var n in arr)
        {
            if (n is not JsonObject o) continue;
            var ver = o["loader"]?["version"]?.GetValue<string>();
            if (ver is null) continue;
            var stable = o["loader"]?["stable"]?.GetValue<bool>() ?? false;
            list.Add(new LoaderEntry(ver, stable));
        }
        return list;
    }

    /// <summary>取 profile json，转成对原版版本定义的补丁。</summary>
    public static async Task<LoaderPatch> GetPatchAsync(Source src, string mc, string loaderVersion, CancellationToken ct)
    {
        var node = await src.GetJsonAsync(
            src.FabricMetaUrls($"v2/versions/loader/{mc}/{loaderVersion}/profile/json"), ct).ConfigureAwait(false);
        var prof = node.AsObject();
        var patch = new LoaderPatch
        {
            MainClass = prof["mainClass"]?.GetValue<string>(),
        };

        if (prof["libraries"] is JsonArray libs)
        {
            foreach (var ln in libs)
            {
                if (ln is not JsonObject lib) continue;
                var name = lib["name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(name)) continue;
                var baseUrl = lib["url"]?.GetValue<string>() ?? "https://maven.fabricmc.net/";
                var path = Paths.MavenPath(name);
                var copy = new JsonObject
                {
                    ["name"] = name,
                    ["downloads"] = new JsonObject
                    {
                        ["artifact"] = new JsonObject
                        {
                            ["path"] = path,
                            ["url"] = baseUrl.TrimEnd('/') + "/" + path,
                        },
                    },
                };
                // Fabric 的库会带 rules（如仅服务端），原样保留
                if (lib["rules"] is JsonArray rules) copy["rules"] = rules.DeepClone();
                patch.Libraries.Add(copy);
            }
        }

        if (prof["arguments"] is JsonObject args)
        {
            foreach (var kv in args)
            {
                if (kv.Value is JsonArray a)
                    patch.Arguments[kv.Key] = a.Select(x => x?.DeepClone()!).ToList();
            }
        }
        return patch;
    }

    public static async Task DownloadLibrariesAsync(Source src, string mcDir, LoaderPatch patch,
        Action<long, long>? onBytes, Action<string>? onLog, int workers, CancellationToken ct)
    {
        var tasks = new List<DlTask>();
        foreach (var ln in patch.Libraries)
        {
            if (ln is not JsonObject lib) continue;
            if (!Vanilla.RulesOk(lib["rules"] as JsonArray)) continue;
            var art = lib["downloads"]?["artifact"] as JsonObject;
            var path = art?["path"]?.GetValue<string>();
            var url = art?["url"]?.GetValue<string>();
            if (path is null || url is null) continue;
            tasks.Add(new DlTask(src.FabricMavenUrls(path, url), Path.Combine(mcDir, "libraries", path), null, 0));
        }
        if (tasks.Count > 0) onLog?.Invoke($"加载器库 {tasks.Count} 个（官方 maven 慢时自动走镜像）");
        var failed = await src.DownloadManyAsync(tasks, Math.Min(workers, 4), onBytes, onLog, ct)
            .ConfigureAwait(false);
        if (failed.Count > 0)
            throw new InvalidOperationException(
                $"加载器库 {failed.Count} 个下载失败（{string.Join("、", failed.Take(3).Select(x => x.Split(':')[0]))}），已中止安装");
    }
}
