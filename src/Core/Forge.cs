using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PCLMCDL.Core;

/// <summary>Forge / NeoForge：官方 installer + headless 外壳（镜像 + 跳过 mojmap）。</summary>
public static class Forge
{
    /// <summary>Forge 主版本号 ≥ 此值时使用 installer 注入方式（MC 1.13+）。</summary>
    const int NewStyleMajor = 20;

    /// <summary>新式（installer 注入）安装要跑 Java；1.12.2 及以前的 legacy 解包不需要。</summary>
    public static bool NeedsJava(string loaderVer, bool isNeo)
        => isNeo || (int.TryParse(loaderVer.Split('.')[0], out var m) && m >= NewStyleMajor);

    // ---------------------------------------------------------- 版本列表

    static (DateTime At, JsonObject Data)? _forgeMetaCache;

    static async Task<JsonObject> ForgeMetadataAsync(Source src, CancellationToken ct)
    {
        if (_forgeMetaCache is { } c && (DateTime.Now - c.At).TotalMinutes < 30) return c.Data;
        var node = await src.GetJsonAsync(src.ForgeMetadataUrls(), ct).ConfigureAwait(false);
        var obj = node.AsObject();
        _forgeMetaCache = (DateTime.Now, obj);
        return obj;
    }

    /// <summary>启动时预热清单：官方站约 10 秒，先抓一次，之后打开版本列表就是秒开。</summary>
    public static async Task WarmUpAsync(Source src)
    {
        try { await ForgeMetadataAsync(src, CancellationToken.None).ConfigureAwait(false); }
        catch { /* 预热失败不影响后续操作 */ }
    }

    public static async Task<List<string>> ListForgeAsync(Source src, string mc, CancellationToken ct)
    {
        var node = await ForgeMetadataAsync(src, ct).ConfigureAwait(false);
        var result = new List<string>();
        if (node is not JsonObject obj) return result;
        foreach (var kv in obj)
        {
            if (kv.Value is not JsonArray arr) continue;
            foreach (var v in arr)
            {
                var s = v?.GetValue<string>();
                if (string.IsNullOrEmpty(s)) continue;
                // 形如 1.21.1-52.0.1 → 取后半段
                var idx = s.IndexOf(mc + "-", StringComparison.Ordinal);
                var ver = idx == 0 ? s[(mc.Length + 1)..] : s;
                if (idx == 0) result.Add(ver);
            }
        }
        return result;
    }

    public static async Task<List<string>> ListNeoForgeAsync(Source src, string mc, CancellationToken ct)
    {
        var node = await src.GetJsonAsync(new[] { $"{Source.Bmcl}/neoforge/list/{mc}" }, ct).ConfigureAwait(false);
        var result = new List<string>();
        if (node is not JsonArray arr) return result;
        foreach (var n in arr)
        {
            if (n is not JsonObject o) continue;
            var v = o["version"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(v)) result.Add(v);
        }
        return result;
    }

    // ---------------------------------------------------------- 主流程

    public static async Task InstallAsync(
        Source src, string mcDir, string mc, string loaderVer, string name, bool isNeo,
        Action<long, long>? onBytes, Action<string>? onLog, int workers, bool force, CancellationToken ct)
    {
        void Log(string s) => onLog?.Invoke(s);
        var vdir = Path.Combine(mcDir, "versions", name);
        if (File.Exists(Path.Combine(vdir, name + ".json")) && !force)
        {
            Log($"版本 {name} 已存在，跳过");
            return;
        }

        var coord = $"{mc}-{loaderVer}";
        var major = int.TryParse(loaderVer.Split('.')[0], out var m) ? m : 0;
        var newStyle = isNeo || major >= NewStyleMajor;
        var loaderName = isNeo ? "NeoForge" : "Forge";

        // installer 需要 launcher_profiles.json
        EnsureLauncherProfiles(mcDir);

        var tempDir = Path.Combine(Path.GetTempPath(), "pclmcdl", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var installer = Path.Combine(tempDir, "installer.jar");

        try
        {
            Log($"下载 {loaderName} 安装器 {coord}");
            string[] urls = isNeo
                ? src.NeoForgeInstallerUrls(loaderVer)
                : src.ForgeInstallerUrls(coord);
            await src.DownloadAsync(new DlTask(urls, installer, null, 0), ct, 2, Log).ConfigureAwait(false);

            if (newStyle)
            {
                await PredownloadAsync(src, mcDir, installer, onBytes, Log, workers, ct).ConfigureAwait(false);
                await RunInjectorAsync(src, installDir: mcDir, installer, name, isNeo, mc, Log, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                await LegacyInstallAsync(installer, mcDir, name, mc, Log, ct).ConfigureAwait(false);
            }
            Log($"版本 {name} 安装完成");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* ignore */ }
        }
    }

    static void EnsureLauncherProfiles(string mcDir)
    {
        var p = Path.Combine(mcDir, "launcher_profiles.json");
        if (File.Exists(p)) return;
        File.WriteAllText(p, """
        {
          "profiles": {},
          "settings": { "useCustomResolution": false },
          "version": 3
        }
        """);
    }

    // ---------------------------------------------------------- 新式：headless 外壳

    /// <summary>
    /// 安装器内部的 Java URLConnection 走不通 BMCLAPI 的多级重定向，
    /// 所以先用本进程把 install_profile / version.json 里的库全部下好，
    /// 安装器随后会直接命中 "File exists: Checksum validated"。
    /// </summary>
    static async Task PredownloadAsync(Source src, string mcDir, string installer,
        Action<long, long>? onBytes, Action<string> log, int workers, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(installer);
        var tasks = new Dictionary<string, DlTask>(StringComparer.OrdinalIgnoreCase);

        foreach (var jsonName in new[] { "install_profile.json", "version.json" })
        {
            var entry = zip.GetEntry(jsonName);
            if (entry is null) continue;
            JsonObject root;
            try { root = JsonNode.Parse(await ReadEntryAsync(entry, ct).ConfigureAwait(false))!.AsObject(); }
            catch { continue; }
            if (root["libraries"] is not JsonArray libs) continue;

            foreach (var ln in libs)
            {
                if (ln is not JsonObject lib) continue;
                var art = lib["downloads"]?["artifact"] as JsonObject;
                var path = art?["path"]?.GetValue<string>();
                var url = art?["url"]?.GetValue<string>();
                if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(url)) continue;
                if (tasks.ContainsKey(path)) continue;

                // installer 给带 classifier 的库算错本地路径：如 accesstransformers:0.14.4-shadowed
                // 会落到 0.14.4-shadowed/ 目录，而 maven 上文件在 0.14.4/ 下。
                // 本地必须落在 installer 期望的 path（它按此校验 sha1），远程则要按 url 里的真实路径取。
                var remotePath = MavenRelativePath(url) ?? path;
                if (!string.Equals(remotePath, path, StringComparison.OrdinalIgnoreCase))
                    log($"[提示] {Path.GetFileName(path)} 镜像路径按官方 URL 修正为 {remotePath}");

                string[] urls;
                if (url.Contains("maven.minecraftforge.net", StringComparison.OrdinalIgnoreCase))
                    urls = src.ForgeMavenUrls(remotePath);
                else if (url.Contains("maven.neoforged.net", StringComparison.OrdinalIgnoreCase))
                    urls = src.NeoForgeMavenUrls(remotePath);
                else if (url.Contains("libraries.minecraft.net", StringComparison.OrdinalIgnoreCase))
                    urls = src.LibraryUrls(remotePath);
                else
                    urls = new[] { url };

                tasks[path] = new DlTask(urls, Path.Combine(mcDir, "libraries", path),
                    art?["sha1"]?.GetValue<string>(), art?["size"]?.GetValue<long>() ?? 0);
            }
        }

        // processors 的 remap 步骤需要 Mojang 的 client_mappings（原版 json 里有 url）
        var profEntry = zip.GetEntry("install_profile.json");
        if (profEntry is not null)
        {
            try
            {
                var pj = JsonNode.Parse(await ReadEntryAsync(profEntry, ct).ConfigureAwait(false))!.AsObject();
                var mcVer = pj["minecraft"]?.GetValue<string>();
                var coordRaw = pj["data"]?["MOJMAPS"]?["client"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(mcVer) && !string.IsNullOrEmpty(coordRaw))
                {
                    var coord = coordRaw.Trim().TrimStart('[').TrimEnd(']');
                    var path = Paths.MavenPath(coord);
                    var vjson = Path.Combine(mcDir, "versions", mcVer, mcVer + ".json");
                    if (!tasks.ContainsKey(path) && File.Exists(vjson))
                    {
                        var vj = JsonNode.Parse(await File.ReadAllTextAsync(vjson, ct).ConfigureAwait(false))!.AsObject();
                        if (vj["downloads"]?["client_mappings"] is JsonObject cm
                            && cm["url"]?.GetValue<string>() is { Length: > 0 } cmUrl)
                        {
                            tasks[path] = new DlTask(new[] { cmUrl },
                                Path.Combine(mcDir, "libraries", path),
                                cm["sha1"]?.GetValue<string>(),
                                cm["size"]?.GetValue<long>() ?? 0);
                            log($"补充原版映射文件 {Path.GetFileName(path)}");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                log($"[提示] 解析 MOJMAPS 失败：{e.Message}");
            }
        }

        log($"预下载加载器依赖 {tasks.Count} 个");
        var failed = await src.DownloadManyAsync(tasks.Values, Math.Min(workers, 4), onBytes, log, ct).ConfigureAwait(false);
        if (failed.Count > 0)
            throw new InvalidOperationException(
                $"安装器依赖 {failed.Count} 个下载失败（{string.Join("、", failed.Take(3).Select(x => x.Split(':')[0]))}），已中止安装");
    }

    static async Task RunInjectorAsync(
        Source src, string installDir, string installer, string name, bool isNeo,
        string mc, Action<string> log, CancellationToken ct)
    {
        var java = Paths.FindJava(8, Paths.LoadConfig().JavaPath) ?? throw new InvalidOperationException("未找到可用的 Java（需要 8 及以上）");
        log($"使用 Java {java.Major}：{java.Exe}");

        var wrapper = ExtractWrapper();
        var startedAt = DateTime.Now.AddSeconds(-2);
        var before = Directory.Exists(Path.Combine(installDir, "versions"))
            ? Directory.GetDirectories(Path.Combine(installDir, "versions"))
            : Array.Empty<string>();

        var psi = new ProcessStartInfo(java.Exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = installDir,
        };
        if (java.Major >= 9)
        {
            psi.ArgumentList.Add("--add-exports");
            psi.ArgumentList.Add("cpw.mods.bootstraplauncher/cpw.mods.bootstraplauncher=ALL-UNNAMED");
        }
        psi.ArgumentList.Add("-cp");
        psi.ArgumentList.Add($"{wrapper};{installer}");
        psi.ArgumentList.Add("com.bangbang93.ForgeInstaller");
        psi.ArgumentList.Add(installDir);

        var lines = new List<string>();
        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (lines) { lines.Add(e.Data); if (lines.Count > 200) lines.RemoveAt(0); }
            log("  " + e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (lines) { lines.Add(e.Data); if (lines.Count > 200) lines.RemoveAt(0); }
            log("  " + e.Data);
        };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消时必须杀掉安装器进程，否则它继续在后台写文件
            try { proc.Kill(entireProcessTree: true); } catch { /* 已退出则忽略 */ }
            try { await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* ignore */ }
            CleanupAddedDirs(installDir, before, name, log);
            throw;
        }

        try
        {
            bool ok;
            lock (lines) ok = lines.TakeLast(5).Any(l => l.Trim() == "true");
            if (!ok)
            {
                string tail;
                lock (lines) tail = string.Join("\n", lines.TakeLast(8));
                throw new IOException($"{(isNeo ? "NeoForge" : "Forge")} 安装器执行失败：\n{tail}");
            }

            // 安装器会在 versions/ 下新建目录，找出它并改名为目标名
            var after = Directory.GetDirectories(Path.Combine(installDir, "versions"));
            var added = after.Except(before, StringComparer.OrdinalIgnoreCase)
                .Where(d => Directory.EnumerateFiles(d).Any()).ToList();

            if (added.Count == 0)
            {
                // 兜底：按本次运行时间 + 名称特征定位（如 1.20.4-forge49.0.9）
                added = after
                    .Where(d => Directory.GetLastWriteTime(d) >= startedAt)
                    .Where(d => Directory.EnumerateFiles(d, "*.json").Any())
                    .Where(d => Path.GetFileName(d).Contains("forge", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(Directory.GetLastWriteTime)
                    .Take(1)
                    .ToList();
                if (added.Count > 0) log($"按时间戳定位安装产物：{Path.GetFileName(added[0])}");
            }

            if (added.Count == 0)
                throw new IOException("安装器未生成版本目录");

            var srcDir = added.OrderByDescending(d => d).First();
            var json = Directory.EnumerateFiles(srcDir, "*.json").FirstOrDefault()
                       ?? throw new IOException("安装器未生成版本 json");
            var target = Path.Combine(installDir, "versions", name);
            Directory.CreateDirectory(target);

            var vj = JsonNode.Parse(await File.ReadAllTextAsync(json, ct).ConfigureAwait(false))!.AsObject();
            vj["id"] = name;
            vj["clientVersion"] = mc;
            await File.WriteAllTextAsync(Path.Combine(target, name + ".json"), vj.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }), ct).ConfigureAwait(false);

            // 安装器产出的其他文件（jar / 缓存）一并搬过去，跳过它自己那份 json
            foreach (var f in Directory.EnumerateFiles(srcDir))
            {
                if (f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(f, Path.Combine(target, Path.GetFileName(f)), true);
            }

            // 安装器新建的目录可能不止一个（它挑一个转成目标名后，剩下的同样没人管）
            CleanupAddedDirs(installDir, before, name, log);
            log($"已转换为版本 {name}");
        }
        catch
        {
            // 失败时清掉安装器产出的中间版本目录，避免残留（目标目录由上层回滚）
            CleanupAddedDirs(installDir, before, name, log);
            throw;
        }
    }

    /// <summary>删除安装器本次新建的中间版本目录（名字与最终目标不同，上层回滚删不到）。</summary>
    static void CleanupAddedDirs(string installDir, string[] before, string targetName, Action<string>? log = null)
    {
        try
        {
            var versions = Path.Combine(installDir, "versions");
            if (!Directory.Exists(versions)) return;
            var target = Path.Combine(versions, targetName);
            foreach (var d in Directory.GetDirectories(versions))
            {
                if (before.Contains(d, StringComparer.OrdinalIgnoreCase)) continue;
                if (string.Equals(d, target, StringComparison.OrdinalIgnoreCase)) continue;
                var dirName = Path.GetFileName(d);
                try
                {
                    Directory.Delete(d, true);
                    log?.Invoke($"已清理安装器中间目录 {dirName}");
                }
                catch (Exception e)
                {
                    log?.Invoke($"[提示] 中间目录 {dirName} 清理失败：{e.Message}");
                }
            }
        }
        catch { /* ignore */ }
    }

    static string ExtractWrapper()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pclmcdl");
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, "forge-installer.jar");
        if (File.Exists(dest) && new FileInfo(dest).Length > 1024) return dest;

        var asm = typeof(Forge).Assembly;
        var resName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("forge-installer.jar", StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("缺少内置 forge-installer.jar");
        using var s = asm.GetManifestResourceStream(resName)!;
        using var f = File.Create(dest);
        s.CopyTo(f);
        return dest;
    }

    // ---------------------------------------------------------- 旧式：解压 legacy installer

    static async Task LegacyInstallAsync(string installer, string mcDir, string name, string mc,
        Action<string> log, CancellationToken ct)
    {
        log("使用旧版 Forge 安装流程");
        using var zip = ZipFile.OpenRead(installer);
        var profileEntry = zip.GetEntry("install_profile.json")
            ?? throw new IOException("installer 缺少 install_profile.json");
        var profile = JsonNode.Parse(await ReadEntryAsync(profileEntry, ct).ConfigureAwait(false))!.AsObject();

        var target = Path.Combine(mcDir, "versions", name);
        Directory.CreateDirectory(target);

        if (profile["install"] is null)
        {
            // Legacy 方式 1：installer 内含完整 version json + maven 目录
            var jsonPath = profile["json"]?.GetValue<string>()?.TrimStart('/')
                ?? throw new IOException("install_profile 缺少 json 字段");
            var entry = zip.GetEntry(jsonPath) ?? throw new IOException($"installer 缺少 {jsonPath}");
            var vj = JsonNode.Parse(await ReadEntryAsync(entry, ct).ConfigureAwait(false))!.AsObject();
            vj["id"] = name;
            vj["clientVersion"] = mc;
            await File.WriteAllTextAsync(Path.Combine(target, name + ".json"),
                vj.ToJsonString(new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }), ct).ConfigureAwait(false);

            // 解压 installer 里的 maven 目录到 libraries
            foreach (var e in zip.Entries)
            {
                if (!e.FullName.StartsWith("maven/", StringComparison.OrdinalIgnoreCase)) continue;
                var rel = e.FullName["maven/".Length..];
                if (string.IsNullOrEmpty(rel) || rel.EndsWith('/')) continue;
                var dest = Path.Combine(mcDir, "libraries", rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                e.ExtractToFile(dest, true);
            }
            log("已解压 installer 内置的 maven 库");
        }
        else
        {
            // Legacy 方式 2：universal jar + versionInfo
            var rawPath = profile["install"]?["path"]?.GetValue<string>()
                ?? throw new IOException("install_profile 缺少 install.path");
            // 老版本这里给的是 maven 坐标（group:artifact:version），必须转成目录路径
            var libPath = rawPath.Contains(':') ? Paths.MavenPath(rawPath) : rawPath;
            var filePath = profile["install"]?["filePath"]?.GetValue<string>()
                ?? throw new IOException("install_profile 缺少 install.filePath");
            var jarEntry = zip.GetEntry(filePath) ?? throw new IOException($"installer 缺少 {filePath}");
            var destJar = Path.Combine(mcDir, "libraries", libPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destJar)!);
            jarEntry.ExtractToFile(destJar, true);

            var vj = profile["versionInfo"]?.DeepClone().AsObject()
                ?? throw new IOException("install_profile 缺少 versionInfo");
            vj["id"] = name;
            vj["clientVersion"] = mc;
            vj["inheritsFrom"] ??= mc;
            await File.WriteAllTextAsync(Path.Combine(target, name + ".json"),
                vj.ToJsonString(new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }), ct).ConfigureAwait(false);
            log($"已写入 universal jar：{libPath}");
        }
    }

    /// <summary>从官方 maven URL 截出相对路径；不是已知 maven 根时返回 null。</summary>
    static string? MavenRelativePath(string url)
    {
        foreach (var root in new[] { Source.ForgeMaven + "/", Source.NeoForgeMaven + "/", Source.Libs + "/" })
            if (url.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return url[root.Length..];
        return null;
    }

    static async Task<string> ReadEntryAsync(ZipArchiveEntry e, CancellationToken ct)
    {
        await using var s = e.Open();
        using var r = new StreamReader(s);
        return await r.ReadToEndAsync(ct).ConfigureAwait(false);
    }
}
