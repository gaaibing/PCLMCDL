using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace PCLMCDL.Core;

/// <summary>加载器对版本定义的覆盖：额外库、主类、启动参数。</summary>
public sealed class LoaderPatch
{
    public List<JsonNode> Libraries { get; } = new();
    public string? MainClass { get; set; }
    public string? Jar { get; set; }
    public Dictionary<string, List<JsonNode>> Arguments { get; } = new();
}

/// <summary>原版安装：manifest → 版本 json → client.jar / libraries / natives / assets。</summary>
public static class Vanilla
{
    public static string Arch => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => "x86_64",
    };

    static string? Str(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static long Lng(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0;

    static bool Bool(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    public static async Task<JsonObject> FetchManifestAsync(Source src, CancellationToken ct, Action<string>? log = null)
    {
        var node = await src.GetJsonAsync(src.ManifestUrls(), ct, 2).ConfigureAwait(false);
        var obj = node.AsObject();
        log?.Invoke($"版本清单已获取：{(obj["versions"] as JsonArray)?.Count ?? 0} 个版本");
        return obj;
    }

    public static JsonObject? FindEntry(JsonObject manifest, string id)
    {
        if (manifest["versions"] is not JsonArray arr) return null;
        foreach (var v in arr)
        {
            if (v is JsonObject o && Str(o["id"]) == id) return o;
        }
        return null;
    }

    public static async Task<JsonObject> FetchVersionJsonAsync(Source src, JsonObject entry, CancellationToken ct)
    {
        var id = Str(entry["id"]) ?? throw new InvalidOperationException("entry 缺少 id");
        var url = Str(entry["url"]) ?? throw new InvalidOperationException("entry 缺少 url");
        var node = await src.GetJsonAsync(src.VersionJsonUrls(id, url), ct).ConfigureAwait(false);
        return node.AsObject();
    }

    // ---------------------------------------------------------- rules

    public static bool RulesOk(JsonArray? rules, IReadOnlyDictionary<string, bool>? features = null)
    {
        if (rules is null || rules.Count == 0) return true;
        var allow = false;
        foreach (var rn in rules)
        {
            if (rn is not JsonObject r) continue;
            var ok = true;

            if (r["os"] is JsonObject os)
            {
                var name = Str(os["name"]);
                if (name is not null && name != "windows") ok = false;
                var arch = Str(os["arch"]);
                if (arch is not null && arch != Arch) ok = false;
            }

            if (r["features"] is JsonObject feats)
            {
                foreach (var kv in feats)
                {
                    var want = Bool(kv.Value);
                    var has = features is not null && features.TryGetValue(kv.Key, out var b) && b;
                    if (want != has) ok = false;
                }
            }

            if (ok) allow = Str(r["action"]) == "allow";
        }
        return allow;
    }

    /// <summary>Mojang 的 natives 库不写 arch 到 rules，靠 name 后缀区分。</summary>
    public static bool NativeArchOk(string name)
    {
        var low = name.ToLowerInvariant();
        var i = low.IndexOf(":natives-", StringComparison.Ordinal);
        if (i < 0) return true;
        var tail = low[(i + 9)..];
        if (tail.EndsWith("-arm64")) return Arch == "arm64";
        if (tail.EndsWith("-x86")) return Arch == "x86";
        if (tail.EndsWith("-arm")) return Arch is "arm" or "arm64";
        return Arch is "x86" or "x86_64";
    }

    static readonly HashSet<string> NativeExt =
        new(StringComparer.OrdinalIgnoreCase) { ".dll", ".so", ".dylib", ".jnilib" };

    // ---------------------------------------------------------- 任务构建

    public sealed class Plan
    {
        public List<DlTask> Tasks { get; } = new();
        public List<(string Jar, string Exclude)> Natives { get; } = new();
        public List<JsonNode> Libraries { get; } = new();
    }

    public static Plan BuildPlan(Source src, JsonObject vj, string mcDir, string name)
    {
        var plan = new Plan();
        if (vj["libraries"] is JsonArray libs)
        {
            foreach (var ln in libs)
            {
                if (ln is not JsonObject lib) continue;
                if (!RulesOk(lib["rules"] as JsonArray)) continue;
                var libName = Str(lib["name"]) ?? "";
                if (!NativeArchOk(libName)) continue;

                plan.Libraries.Add(lib);
                var downloads = lib["downloads"] as JsonObject;

                // 老式：natives + classifiers。注意这类库（lwjgl-platform、text2speech）
                // 往往同时带有主 artifact，不能 continue，主 jar 必须在下面一并下载
                var natKey = Str((lib["natives"] as JsonObject)?["windows"]);
                if (natKey is not null)
                {
                    string? npath = null, nsha = null;
                    long nsize = 0;
                    if ((downloads?["classifiers"] as JsonObject)?[natKey] is JsonObject cls)
                    {
                        npath = Str(cls["path"]);
                        nsha = Str(cls["sha1"]);
                        nsize = Lng(cls["size"]);
                    }
                    else if (downloads is null)
                    {
                        // 远古版本（如 1.4.7）的库没有 downloads 段，按 maven 坐标拼路径
                        npath = Paths.MavenPath($"{libName}:{natKey}");
                    }
                    if (npath is not null)
                    {
                        plan.Tasks.Add(new DlTask(src.LibraryUrls(npath), Path.Combine(mcDir, "libraries", npath),
                            nsha, nsize));
                        plan.Natives.Add((Path.Combine(mcDir, "libraries", npath), "META-INF/"));
                    }
                }

                // 新式：name 里带 :natives-windows
                if (libName.Contains(":natives-", StringComparison.Ordinal)
                    && libName.Contains("windows", StringComparison.Ordinal))
                {
                    if (downloads?["artifact"] is JsonObject art0)
                    {
                        var path = Str(art0["path"])!;
                        plan.Tasks.Add(new DlTask(src.LibraryUrls(path), Path.Combine(mcDir, "libraries", path),
                            Str(art0["sha1"]), Lng(art0["size"])));
                        plan.Natives.Add((Path.Combine(mcDir, "libraries", path), "META-INF/"));
                    }
                    continue;
                }

                if (downloads?["artifact"] is JsonObject art && Str(art["path"]) is { } p2)
                {
                    plan.Tasks.Add(new DlTask(src.LibraryUrls(p2), Path.Combine(mcDir, "libraries", p2),
                        Str(art["sha1"]), Lng(art["size"])));
                }
                else if (downloads is null && libName.Length > 0)
                {
                    // 远古版本无 downloads 段，主 jar 同样按坐标拼
                    var p3 = Paths.MavenPath(libName);
                    plan.Tasks.Add(new DlTask(src.LibraryUrls(p3), Path.Combine(mcDir, "libraries", p3), null, 0));
                }
            }
        }

        var vdir = Path.Combine(mcDir, "versions", name);
        if (vj["downloads"]?["client"] is JsonObject client)
        {
            var vid = Str(vj["id"]) ?? name;
            plan.Tasks.Add(new DlTask(src.ClientUrls(vid, Str(client["url"])!),
                Path.Combine(vdir, name + ".jar"), Str(client["sha1"]), Lng(client["size"])));
        }

        if (vj["assetIndex"] is JsonObject ai)
        {
            plan.Tasks.Add(new DlTask(new[] { Str(ai["url"])! },
                Path.Combine(mcDir, "assets", "indexes", Str(ai["id"]) + ".json"),
                Str(ai["sha1"]), Lng(ai["size"])));
        }
        return plan;
    }

    public static List<DlTask> BuildAssetTasks(Source src, string mcDir, JsonObject index)
    {
        var tasks = new List<DlTask>();
        if (index["objects"] is not JsonObject objs) return tasks;
        var root = Path.Combine(mcDir, "assets", "objects");
        foreach (var kv in objs)
        {
            if (kv.Value is not JsonObject o) continue;
            var hash = Str(o["hash"]);
            var size = Lng(o["size"]);
            if (hash is null) continue;
            var dest = Path.Combine(root, hash[..2], hash);
            if (File.Exists(dest) && new FileInfo(dest).Length == size) continue;
            tasks.Add(new DlTask(src.AssetUrls(hash), dest, hash, size));
        }
        return tasks;
    }

    public static void ExtractNatives(Plan plan, string mcDir, string name, Action<string>? log = null)
    {
        var outDir = Path.Combine(mcDir, "versions", name, name + "-natives");
        Directory.CreateDirectory(outDir);
        foreach (var (jar, exclude) in plan.Natives.DistinctBy(x => x.Jar))
        {
            if (!File.Exists(jar)) continue;
            try
            {
                using var zip = ZipFile.OpenRead(jar);
                foreach (var e in zip.Entries)
                {
                    if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) continue;
                    if (exclude.Length > 0 && e.FullName.StartsWith(exclude, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!NativeExt.Contains(Path.GetExtension(e.Name))) continue;
                    var target = Path.Combine(outDir, e.Name);
                    e.ExtractToFile(target, true);
                }
            }
            catch (Exception e)
            {
                log?.Invoke($"[警告] native 包解压失败 {Path.GetFileName(jar)}: {e.Message}");
            }
        }
    }

    /// <summary>老版本资源映射：virtual → assets/virtual/&lt;id&gt;，map_to_resources → resources/</summary>
    public static void MapLegacyAssets(string mcDir, string indexId, JsonObject index, Action<string>? log = null)
    {
        var virtualDir = Path.Combine(mcDir, "assets", "virtual", indexId);
        var legacyDir = Path.Combine(mcDir, "resources");
        var doVirtual = Bool(index["virtual"]);
        var doLegacy = Bool(index["map_to_resources"]);
        if (!doVirtual && !doLegacy) return;
        if (index["objects"] is not JsonObject objs) return;

        foreach (var kv in objs)
        {
            if (kv.Value is not JsonObject o) continue;
            var hash = Str(o["hash"]);
            if (hash is null) continue;
            var srcFile = Path.Combine(mcDir, "assets", "objects", hash[..2], hash);
            if (!File.Exists(srcFile)) continue;
            var rel = kv.Key.Replace('/', Path.DirectorySeparatorChar);
            try
            {
                if (doVirtual)
                {
                    var dst = Path.Combine(virtualDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    if (!File.Exists(dst)) File.Copy(srcFile, dst, true);
                }
                if (doLegacy)
                {
                    var dst = Path.Combine(legacyDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    if (!File.Exists(dst)) File.Copy(srcFile, dst, true);
                }
            }
            catch { /* 单个资源失败不影响整体 */ }
        }
        log?.Invoke($"已映射老版本资源（virtual={doVirtual}, map_to_resources={doLegacy}）");
    }

    // ---------------------------------------------------------- 安装

    public static async Task<JsonObject> InstallAsync(
        Source src,
        string mcDir,
        string mcVersion,
        string? name,
        IReadOnlyList<LoaderPatch>? loaders,
        Action<long, long>? onBytes,
        Action<string>? onLog,
        int workers,
        bool force,
        CancellationToken ct)
    {
        void Log(string s) => onLog?.Invoke(s);
        name ??= mcVersion;
        var vdir = Path.Combine(mcDir, "versions", name);
        var targetJson = Path.Combine(vdir, name + ".json");

        if (File.Exists(targetJson) && !force)
        {
            Log($"版本 {name} 已存在，跳过");
            return JsonNode.Parse(await File.ReadAllTextAsync(targetJson, ct).ConfigureAwait(false))!.AsObject();
        }

        var manifest = await FetchManifestAsync(src, ct, onLog).ConfigureAwait(false);
        var entry = FindEntry(manifest, mcVersion) ?? throw new InvalidOperationException($"清单中找不到 {mcVersion}");
        var vj = await FetchVersionJsonAsync(src, entry, ct).ConfigureAwait(false);
        Log($"已获取 {mcVersion} 版本定义（{(vj["libraries"] as JsonArray)?.Count ?? 0} 个库）");

        var plan = BuildPlan(src, vj, mcDir, name);
        var assetTasks = new List<DlTask>();

        if (vj["assetIndex"] is JsonObject ai)
        {
            var idxPath = Path.Combine(mcDir, "assets", "indexes", (Str(ai["id"]) ?? "") + ".json");
            if (!File.Exists(idxPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(idxPath)!);
                await File.WriteAllBytesAsync(idxPath,
                    await src.GetBytesAsync(new[] { Str(ai["url"])! }, ct, 2).ConfigureAwait(false), ct)
                    .ConfigureAwait(false);
            }
            try
            {
                var idx = JsonNode.Parse(await File.ReadAllTextAsync(idxPath, ct).ConfigureAwait(false))!.AsObject();
                var at = BuildAssetTasks(src, mcDir, idx);
                Log($"资源索引 {Str(ai["id"])}：待下载 {at.Count} 个对象（已存在的自动跳过）");
                assetTasks.AddRange(at);
                MapLegacyAssets(mcDir, Str(ai["id"]) ?? "", idx, onLog);
            }
            catch (Exception e)
            {
                Log($"[警告] 资源索引解析失败：{e.Message}");
            }
        }

        Log("── 下载游戏本体与依赖 ──");
        var coreFailed = await src.DownloadManyAsync(plan.Tasks, workers, onBytes, onLog, ct).ConfigureAwait(false);
        if (coreFailed.Count > 0)
            throw new InvalidOperationException(
                $"核心文件 {coreFailed.Count} 个下载失败（{string.Join("、", coreFailed.Take(3).Select(x => x.Split(':')[0]))}），已中止安装");

        if (assetTasks.Count > 0)
        {
            Log("── 下载资源文件（声音、语言、贴图）──");
            var assetFailed = await src.DownloadManyAsync(assetTasks, workers, onBytes, onLog, ct).ConfigureAwait(false);
            if (assetFailed.Count > 0)
                Log($"[警告] {assetFailed.Count} 个资源文件下载失败，游戏可以启动，但可能缺声音或语言");
        }

        ExtractNatives(plan, mcDir, name, onLog);

        vj["id"] = name;
        vj["clientVersion"] = mcVersion;
        vj.Remove("inheritsFrom");

        if (loaders is not null)
        {
            var libs = vj["libraries"] as JsonArray ?? new JsonArray();
            if (vj["libraries"] is null) vj["libraries"] = libs;
            var have = libs.OfType<JsonObject>().Select(x => Str(x["name"])).ToHashSet();
            foreach (var ld in loaders)
            {
                foreach (var l in ld.Libraries)
                {
                    var nm = Str(l["name"]);
                    if (nm is not null && have.Contains(nm)) continue;
                    libs.Add(l.DeepClone());
                    if (nm is not null) have.Add(nm);
                }
                if (ld.MainClass is not null) vj["mainClass"] = ld.MainClass;
                if (ld.Jar is not null) vj["jar"] = ld.Jar;
                if (ld.Arguments.Count > 0)
                {
                    var args = vj["arguments"] as JsonObject ?? new JsonObject();
                    vj["arguments"] = args;
                    foreach (var kv in ld.Arguments)
                    {
                        var arr = args[kv.Key] as JsonArray ?? new JsonArray();
                        args[kv.Key] = arr;
                        foreach (var a in kv.Value) arr.Add(a.DeepClone());
                    }
                }
            }
        }

        Directory.CreateDirectory(vdir);
        await File.WriteAllTextAsync(targetJson, vj.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }), ct).ConfigureAwait(false);
        Log($"版本已写入 {targetJson}");
        return vj;
    }
}
