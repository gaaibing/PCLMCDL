using System.Diagnostics;
using System.Text.Json.Nodes;

namespace PCLMCDL.Core;

/// <summary>OptiFine：≤1.13 手写 launchwrapper 配置，≥1.14 跑官方安装器。</summary>
public static class OptiFine
{
    public sealed record Entry(string Type, string Patch, string FileName, string McVersion)
    {
        /// <summary>用于版本名后缀，如 E4。</summary>
        public string Suffix => Patch;
    }

    public static async Task<List<Entry>> ListAsync(Source src, string mc, CancellationToken ct)
    {
        var node = await src.GetJsonAsync(src.OptiFineListUrls(mc), ct).ConfigureAwait(false);
        var list = new List<Entry>();
        if (node is not JsonArray arr) return list;
        foreach (var n in arr)
        {
            if (n is not JsonObject o) continue;
            var type = o["type"]?.GetValue<string>();
            var patch = o["patch"]?.GetValue<string>();
            var file = o["filename"]?.GetValue<string>();
            if (type is null || patch is null || file is null) continue;
            if (patch.StartsWith("pre", StringComparison.OrdinalIgnoreCase)) continue; // 跳过预览版
            list.Add(new Entry(type, patch, file, mc));
        }
        return list;
    }

    static bool IsModern(string mc)
    {
        var p = mc.Split('.');
        if (p.Length < 2) return true;
        if (!int.TryParse(p[0], out var major)) return true;
        if (major != 1) return true; // 26.x 之类的新命名
        return int.TryParse(p[1], out var minor) && minor >= 14;
    }

    /// <summary>该 MC 版本是否走旧版（手写 launchwrapper）路径——旧版不需要 Java。</summary>
    public static bool IsLegacyFor(string mc) => !IsModern(mc);

    public static async Task InstallAsync(
        Source src, string mcDir, string mc, Entry entry, string name,
        Action<long, long>? onBytes, Action<string>? onLog, int workers, bool force, CancellationToken ct,
        string? inheritFrom = null)
    {
        void Log(string s) => onLog?.Invoke(s);
        var vdir = Path.Combine(mcDir, "versions", name);
        if (File.Exists(Path.Combine(vdir, name + ".json")) && !force)
        {
            Log($"版本 {name} 已存在，跳过");
            return;
        }

        var temp = Path.Combine(Path.GetTempPath(), "pclmcdl", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var jar = Path.Combine(temp, entry.FileName);
        try
        {
            Log($"下载 OptiFine {entry.FileName}");
            await src.DownloadAsync(new DlTask(
                src.OptiFineFileUrls(mc, entry.Type, entry.Patch), jar, null, 0), ct, 2, Log).ConfigureAwait(false);

            if (IsModern(mc))
                await InstallModernAsync(src, mcDir, mc, jar, name, Log, ct).ConfigureAwait(false);
            else
                await InstallLegacyAsync(src, mcDir, mc, jar, entry, name, Log, workers, ct).ConfigureAwait(false);

            Log($"版本 {name} 安装完成");
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { /* ignore */ }
        }

        // 叠加到其他加载器之上时，把继承链改为指向那个版本
        if (inheritFrom is not null)
        {
            var jp = Path.Combine(mcDir, "versions", name, name + ".json");
            if (File.Exists(jp))
            {
                var vj = JsonNode.Parse(await File.ReadAllTextAsync(jp, ct).ConfigureAwait(false))!.AsObject();
                vj["inheritsFrom"] = inheritFrom;
                await File.WriteAllTextAsync(jp, vj.ToJsonString(new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }), ct).ConfigureAwait(false);
                Log($"继承链已指向 {inheritFrom}");
            }
        }
    }

    // ---------------------------------------------------------- 旧版：手写 launchwrapper

    static async Task InstallLegacyAsync(Source src, string mcDir, string mc, string jarFile,
        Entry entry, string name, Action<string> log, int workers, CancellationToken ct)
    {
        // OptiFine_1.13.1_HD_U_E4.jar → 1.13.1_HD_U_E4
        var ver = Path.GetFileNameWithoutExtension(entry.FileName)
            .Replace("OptiFine_", "", StringComparison.Ordinal)
            .Replace("preview_", "", StringComparison.Ordinal);

        var libDir = Path.Combine(mcDir, "libraries", "optifine", "OptiFine", ver);
        Directory.CreateDirectory(libDir);
        var libJar = Path.Combine(libDir, "OptiFine-" + ver + ".jar");
        File.Copy(jarFile, libJar, true);
        log($"已放入 libraries：{libJar}");

        // launchwrapper 1.12
        var lwPath = "net/minecraft/launchwrapper/1.12/launchwrapper-1.12.jar";
        await src.DownloadAsync(new DlTask(src.LibraryUrls(lwPath),
            Path.Combine(mcDir, "libraries", lwPath), null, 0), ct, 2, log).ConfigureAwait(false);

        var vj = new JsonObject
        {
            ["id"] = name,
            ["inheritsFrom"] = mc,
            ["clientVersion"] = mc,
            ["type"] = "release",
            ["time"] = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
            ["releaseTime"] = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
            ["mainClass"] = "net.minecraft.launchwrapper.Launch",
            ["minimumLauncherVersion"] = 21,
            ["libraries"] = new JsonArray
            {
                new JsonObject { ["name"] = $"optifine:OptiFine:{ver}" },
                new JsonObject { ["name"] = "net.minecraft:launchwrapper:1.12" },
            },
            ["arguments"] = new JsonObject
            {
                ["game"] = new JsonArray { "--tweakClass", "optifine.OptiFineTweaker" },
            },
        };

        var vdir = Path.Combine(mcDir, "versions", name);
        Directory.CreateDirectory(vdir);
        await File.WriteAllTextAsync(Path.Combine(vdir, name + ".json"),
            vj.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }), ct).ConfigureAwait(false);
        log("已写入旧版 OptiFine 版本 json（launchwrapper + OptiFineTweaker）");
    }

    // ---------------------------------------------------------- 新版：跑官方安装器

    static async Task InstallModernAsync(Source src, string mcDir, string mc, string jarFile,
        string name, Action<string> log, CancellationToken ct)
    {
        var home = Path.Combine(Path.GetTempPath(), "pclmcdl", "of_" + Guid.NewGuid().ToString("N"));
        var fakeMc = Path.Combine(home, ".minecraft");
        try
        {
            // 准备一个只含原版的假 .minecraft
            var vdir = Path.Combine(fakeMc, "versions", mc);
            Directory.CreateDirectory(vdir);
            var realVdir = Path.Combine(mcDir, "versions", mc);
            var realJson = Path.Combine(realVdir, mc + ".json");
            var realJar = Path.Combine(realVdir, mc + ".jar");
            if (!File.Exists(realJson) || !File.Exists(realJar))
                throw new IOException($"需要先安装原版 {mc}");
            File.Copy(realJson, Path.Combine(vdir, mc + ".json"), true);
            File.Copy(realJar, Path.Combine(vdir, mc + ".jar"), true);
            File.WriteAllText(Path.Combine(fakeMc, "launcher_profiles.json"), """
            {"profiles":{},"settings":{"useCustomResolution":false},"version":3}
            """);

            var java = Paths.FindJava(8, Paths.LoadConfig().JavaPath) ?? throw new InvalidOperationException("未找到可用的 Java");
            log($"使用 Java {java.Major} 运行 OptiFine 安装器");

            var psi = new ProcessStartInfo(java.Exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = home,
            };
            if (java.Major >= 9)
            {
                psi.ArgumentList.Add("--add-exports");
                psi.ArgumentList.Add("cpw.mods.bootstraplauncher/cpw.mods.bootstraplauncher=ALL-UNNAMED");
            }
            psi.ArgumentList.Add($"-Duser.home={home}");
            psi.ArgumentList.Add("-cp");
            psi.ArgumentList.Add(jarFile);
            psi.ArgumentList.Add("optifine.Installer");
            psi.EnvironmentVariables["appdata"] = home;

            var lines = 0;
            var last = "";
            using var proc = new Process { StartInfo = psi };
            void OnLine(string? s)
            {
                if (s is null) return;
                lines++;
                last = s;
                if (lines % 50 == 0) log("  " + s);
            }
            proc.OutputDataReceived += (_, e) => OnLine(e.Data);
            proc.ErrorDataReceived += (_, e) => OnLine(e.Data);
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
                throw;
            }

            if (last.Contains(" at ", StringComparison.Ordinal))
                throw new IOException($"OptiFine 安装器出错，末行：{last}");

            // 把假 .minecraft 下新增的内容搬回来
            var fakeVersions = Path.Combine(fakeMc, "versions");
            if (!Directory.Exists(fakeVersions))
                throw new IOException("OptiFine 安装器未生成版本目录");

            var produced = Directory.GetDirectories(fakeVersions)
                .Where(d => !string.Equals(Path.GetFileName(d), mc, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (produced.Count == 0)
                throw new IOException("OptiFine 安装器未生成新的版本目录");

            var srcDir = produced[0];
            var target = Path.Combine(mcDir, "versions", name);
            Directory.CreateDirectory(target);

            var json = Directory.EnumerateFiles(srcDir, "*.json").FirstOrDefault()
                       ?? throw new IOException("OptiFine 安装器未生成 json");
            var vj = JsonNode.Parse(await File.ReadAllTextAsync(json, ct).ConfigureAwait(false))!.AsObject();
            vj["id"] = name;
            vj["clientVersion"] = mc;
            await File.WriteAllTextAsync(Path.Combine(target, name + ".json"),
                vj.ToJsonString(new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }), ct).ConfigureAwait(false);

            foreach (var f in Directory.EnumerateFiles(srcDir))
            {
                if (f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(f, Path.Combine(target, Path.GetFileName(f)), true);
            }

            // 安装器写入 libraries 的 OptiFine jar 也要搬
            var fakeLibs = Path.Combine(fakeMc, "libraries", "optifine");
            if (Directory.Exists(fakeLibs))
                CopyDir(fakeLibs, Path.Combine(mcDir, "libraries", "optifine"));

            log("已从安装器产物生成版本");
        }
        finally
        {
            try { Directory.Delete(home, true); } catch { /* ignore */ }
        }
    }

    static void CopyDir(string from, string to)
    {
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(from, to));
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = f.Replace(from, to);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest, true);
        }
    }
}
