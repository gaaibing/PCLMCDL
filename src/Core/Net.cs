using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PCLMCDL.Core;

/// <summary>下载源偏好。</summary>
public enum SourceMode
{
    /// <summary>官方优先，失败回退 BMCLAPI。</summary>
    Auto,
    /// <summary>只用官方源。</summary>
    OfficialOnly,
    /// <summary>BMCLAPI 优先，失败回退官方。</summary>
    MirrorFirst,
}

/// <summary>下载任务：多源候选、目标路径、校验信息。</summary>
public sealed record DlTask(string[] Urls, string Dest, string? Sha1, long Size);

/// <summary>官方源 / BMCLAPI 镜像双源，带自动回退与并发下载。</summary>
public sealed class Source
{
    public const string Mojang = "https://piston-meta.mojang.com";
    public const string Resources = "https://resources.download.minecraft.net";
    public const string Libs = "https://libraries.minecraft.net";
    public const string ForgeMaven = "https://maven.minecraftforge.net";
    public const string NeoForgeMaven = "https://maven.neoforged.net/releases";
    public const string FabricMeta = "https://meta.fabricmc.net";
    public const string Bmcl = "https://bmclapi2.bangbang93.com";

    public static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            // 部分镜像对二进制返回错误的 Content-Encoding，关掉自动解压更稳
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = 64,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
        // BMCLAPI 的 HTTP/2 连接偶发挂起，强制走 HTTP/1.1
        c.DefaultRequestVersion = HttpVersion.Version11;
        c.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/126.0 Safari/537.36 PCLMCDL/0.1");
        return c;
    }

    public SourceMode Mode { get; init; } = SourceMode.Auto;

    // ---------------------------------------------------------- URL 候选

    // 通用资源：Auto 下官方优先，镜像兜底
    string[] Order(string official, string mirror) => Mode switch
    {
        SourceMode.OfficialOnly => new[] { official },
        SourceMode.MirrorFirst => new[] { mirror, official },
        _ => new[] { official, mirror },
    };

    // maven 类资源：官方仓库在国内常超时，Auto 下镜像优先
    string[] MavenOrder(string official, string mirror) => Mode switch
    {
        SourceMode.OfficialOnly => new[] { official },
        _ => new[] { mirror, official },
    };

    public string[] ManifestUrls()
        => Order(Mojang + "/mc/game/version_manifest_v2.json", Bmcl + "/mc/game/version_manifest_v2.json");

    public string[] VersionJsonUrls(string id, string officialUrl)
        => Order(officialUrl, $"{Bmcl}/version/{id}/json");

    public string[] ClientUrls(string id, string officialUrl)
        => Order(officialUrl, $"{Bmcl}/version/{id}/client");

    public string[] LibraryUrls(string path)
        => Order($"{Libs}/{path}", $"{Bmcl}/maven/{path}");

    public string[] AssetUrls(string hash)
        => Order($"{Resources}/{hash[..2]}/{hash}", $"{Bmcl}/assets/{hash[..2]}/{hash}");

    public string[] ForgeInstallerUrls(string coord)
    {
        var path = $"net/minecraftforge/forge/{coord}/forge-{coord}-installer.jar";
        return MavenMirrors(path, $"{ForgeMaven}/{path}");
    }

    public string[] NeoForgeInstallerUrls(string coord)
    {
        // NeoForge 1.20.1 及以前沿用 net.neoforged:forge 坐标（版本号带 MC 前缀），
        // 1.20.2 起才改名为 net.neoforged:neoforge
        var legacy = coord.StartsWith("1.", StringComparison.Ordinal);
        var path = legacy
            ? $"net/neoforged/forge/{coord}/forge-{coord}-installer.jar"
            : $"net/neoforged/neoforge/{coord}/neoforge-{coord}-installer.jar";
        return MavenMirrors(path, $"{NeoForgeMaven}/{path}");
    }

    public string[] ForgeMavenUrls(string path)
        => MavenMirrors(path, $"{ForgeMaven}/{path}");

    public string[] NeoForgeMavenUrls(string path)
        => MavenMirrors(path, $"{NeoForgeMaven}/{path}");

    public string[] FabricMetaUrls(string path)
        => MavenOrder($"{FabricMeta}/{path}", $"{Bmcl}/fabric-meta/{path}");

    /// <summary>maven 资源的多镜像候选。各镜像站路径前缀不同，官方源放最后兜底。</summary>
    public string[] MavenMirrors(string mavenPath, string officialUrl)
    {
        if (Mode == SourceMode.OfficialOnly) return new[] { officialUrl };
        return new[]
        {
            $"https://mirror.nju.edu.cn/bmclapi/{mavenPath}",
            $"https://mirrors.sjtug.sjtu.edu.cn/bmclapi/{mavenPath}",
            $"{Bmcl}/maven/{mavenPath}",
            officialUrl,
        };
    }

    /// <summary>
    /// Forge 版本清单。注意：nju / sjtug / bmclapi 上的这份 maven-metadata.json 只更新到 1.12.2，
    /// 用过它会看不到 1.13+ 的所有构建，所以官方站必须排第一，镜像只作兜底。
    /// </summary>
    public string[] ForgeMetadataUrls()
    {
        var path = "net/minecraftforge/forge/maven-metadata.json";
        var official = $"https://files.minecraftforge.net/{path}";
        if (Mode == SourceMode.OfficialOnly) return new[] { official };
        return new[]
        {
            official,
            $"{Bmcl}/maven/{path}",
            $"https://mirror.nju.edu.cn/bmclapi/{path}",
        };
    }

    // Fabric 的库地址写在 profile json 里，官方 maven 在国内极慢，默认走镜像
    public string[] FabricMavenUrls(string path, string officialUrl)
        => MavenMirrors(path, officialUrl);

    // OptiFine 官方站有 Cloudflare 保护且需人工点下载，只能走镜像
    public string[] OptiFineListUrls(string mc) => new[] { $"{Bmcl}/optifine/{mc}" };

    public string[] OptiFineFileUrls(string mc, string kind, string patch)
        => new[] { $"{Bmcl}/optifine/{mc}/{kind}/{patch}" };

    // ---------------------------------------------------------- 元数据

    public async Task<byte[]> GetBytesAsync(string[] urls, CancellationToken ct, int retries = 1, int perUrlTimeoutSeconds = 20)
    {
        Exception? last = null;
        for (var attempt = 0; attempt <= retries; attempt++)
        {
            foreach (var url in urls)
            {
                // 单个 URL 限时，卡死的源赶紧换下一个，别让用户干等
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(perUrlTimeoutSeconds));
                try
                {
                    return await Http.GetByteArrayAsync(url, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    last = new TimeoutException($"{perUrlTimeoutSeconds} 秒无响应：{url}");
                }
                catch (Exception e)
                {
                    last = e;
                }
            }
            if (attempt < retries) await Task.Delay(400 * (attempt + 1), ct).ConfigureAwait(false);
        }
        throw new IOException($"全部来源失败：{string.Join(", ", urls)}", last);
    }

    public async Task<JsonNode> GetJsonAsync(string[] urls, CancellationToken ct, int retries = 1, int perUrlTimeoutSeconds = 20)
        => JsonNode.Parse(System.Text.Encoding.UTF8.GetString(
               await GetBytesAsync(urls, ct, retries, perUrlTimeoutSeconds).ConfigureAwait(false)).TrimStart('\uFEFF'))
           ?? throw new IOException("JSON 解析为空");

    // ---------------------------------------------------------- 单文件

    public static string Sha1Of(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>下载到 dest。已存在且校验通过返回 false（跳过）。</summary>
    public async Task<bool> DownloadAsync(DlTask t, CancellationToken ct, int retries = 1, Action<string>? onLog = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(t.Dest)!);
        if (File.Exists(t.Dest))
        {
            var fi = new FileInfo(t.Dest);
            if ((t.Size <= 0 || fi.Length == t.Size) && (t.Sha1 is null || Sha1Of(t.Dest) == t.Sha1))
                return false;
        }

        var tmp = t.Dest + ".part";
        Exception? last = null;
        for (var attempt = 0; attempt <= retries; attempt++)
        {
            foreach (var url in t.Urls)
            {
                try
                {
                    using (var perReq = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        // 30 秒没有新数据就判定这个源卡死，换下一个
                        perReq.CancelAfter(TimeSpan.FromSeconds(30));
                        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, perReq.Token)
                            .ConfigureAwait(false);
                        resp.EnsureSuccessStatusCode();
                        await using var src = await resp.Content.ReadAsStreamAsync(perReq.Token).ConfigureAwait(false);
                        await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                            1 << 16, true);

                        var buf = new byte[81920];
                        while (true)
                        {
                            var n = await src.ReadAsync(buf, perReq.Token).ConfigureAwait(false);
                            if (n <= 0) break;
                            await dst.WriteAsync(buf.AsMemory(0, n), perReq.Token).ConfigureAwait(false);
                            perReq.CancelAfter(TimeSpan.FromSeconds(30)); // 收到数据就重置计时
                        }
                    }
                    if (t.Sha1 is not null && Sha1Of(tmp) != t.Sha1)
                    {
                        last = new IOException("sha1 校验失败");
                        File.Delete(tmp);
                        continue;
                    }
                    File.Move(tmp, t.Dest, true);
                    return true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
                    throw;
                }
                catch (OperationCanceledException)
                {
                    last = new TimeoutException("30 秒无数据，已放弃该源");
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
                }
                catch (Exception e)
                {
                    last = e;
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
                    var name = Path.GetFileName(t.Dest);

                    // 403 说明镜像在限流。官方源在国内极慢（实测 25 秒只下到 425 KB），
                    // 回退过去基本等于卡死，不如等一会儿直接重试镜像
                    if (e is HttpRequestException hre && (int?)hre.StatusCode == 403)
                    {
                        if (url != t.Urls[^1])
                        {
                            onLog?.Invoke($"  ! {name}：该镜像 403，换下一个源");
                            continue;
                        }
                        var wait = attempt == 0 ? 8 : 20;
                        onLog?.Invoke($"  ! {name}：镜像全部限流（403），{wait} 秒后重试");
                        await Task.Delay(wait * 1000, ct).ConfigureAwait(false);
                        break;
                    }

                    if (url != t.Urls[^1])
                        onLog?.Invoke($"  ! {name}：{e.GetType().Name}，换源重试");
                }
            }
            if (attempt < retries) await Task.Delay(400 * (attempt + 1), ct).ConfigureAwait(false);
        }
        throw new IOException($"下载失败 {Path.GetFileName(t.Dest)}：{last?.Message}", last);
    }

    // ---------------------------------------------------------- 批量

    public async Task<List<string>> DownloadManyAsync(
        IEnumerable<DlTask> tasks,
        int workers,
        Action<long, long>? onBytes,
        Action<string>? onLog,
        CancellationToken ct)
    {
        var list = tasks.ToList();
        var total = list.Sum(x => Math.Max(x.Size, 0));
        long done = 0;
        var failed = new List<string>();
        var gate = new object();
        onLog?.Invoke(total > 0
            ? $"待下载 {list.Count} 个文件，共 {total / 1048576.0:F1} MB"
            : $"待下载 {list.Count} 个文件");
        if (list.Count == 0) return failed;

        // 文件少就逐个报，文件多就每 10% 报一次，避免刷屏
        var reportEach = list.Count <= 30;
        var step = Math.Max(1, list.Count / 10);
        var completed = 0;

        // 卡住时每 5 秒报一次，明确说出在等哪个文件、等多久了
        var lastSeen = 0;
        var inFlight = new System.Collections.Concurrent.ConcurrentDictionary<string, (string Name, DateTime At)>();
        using var heartbeat = new System.Threading.Timer(_ =>
        {
            var cur = Volatile.Read(ref completed);
            if (cur != lastSeen || ct.IsCancellationRequested)
            {
                lastSeen = cur;
                return;
            }

            var now = DateTime.Now;
            var pending = inFlight.Values
                .Where(v => (now - v.At).TotalSeconds >= 5)
                .Select(v => $"{v.Name}（{(int)(now - v.At).TotalSeconds}s）")
                .ToList();

            onLog?.Invoke(pending.Count > 0
                ? $"  … {cur}/{list.Count} 完成，正在等 {string.Join("、", pending)}"
                : $"  … {cur}/{list.Count} 完成，连接中");
            lastSeen = cur;
        }, null, 5000, 5000);

        using var sem = new SemaphoreSlim(Math.Max(1, workers));
        var jobs = list.Select(async t =>
        {
            await sem.WaitAsync(ct).ConfigureAwait(false);
            var name = Path.GetFileName(t.Dest);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (reportEach) onLog?.Invoke($"  ↓ {name}");
                inFlight[t.Dest] = (name, DateTime.Now);
                await DownloadAsync(t, ct, 2, onLog).ConfigureAwait(false);
                inFlight.TryRemove(t.Dest, out _);
                var n = Interlocked.Increment(ref completed);

                if (reportEach)
                {
                    long size = 0;
                    try { size = new FileInfo(t.Dest).Length; } catch { /* ignore */ }
                    onLog?.Invoke($"  ✓ {name}（{size / 1024.0:F0} KB，{sw.Elapsed.TotalSeconds:F1}s）");
                }
                else if (onLog is not null && (n % step == 0 || n == list.Count))
                {
                    onLog($"  进度 {n}/{list.Count}");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                inFlight.TryRemove(t.Dest, out _);
                lock (gate) failed.Add($"{Path.GetFileName(t.Dest)}: {e.Message}");
                onLog?.Invoke($"  ✗ {Path.GetFileName(t.Dest)}：{e.Message}");
            }
            finally
            {
                var n = Interlocked.Add(ref done, Math.Max(t.Size, 0));
                if (onBytes is not null) lock (gate) onBytes(n, total);
                sem.Release();
            }
        });
        await Task.WhenAll(jobs).ConfigureAwait(false);
        heartbeat.Dispose();
        onLog?.Invoke(failed.Count == 0
            ? $"  完成 {list.Count} 个文件"
            : $"  完成 {list.Count - failed.Count} 个，失败 {failed.Count} 个");
        return failed;
    }
}
