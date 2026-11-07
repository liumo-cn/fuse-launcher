using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Fuse.Core.ModConflict;

public sealed class ModFixOutcome
{
    public bool BackupCreated { get; init; }
    public string BackupDirectory { get; init; } = "";
    public List<string> Applied { get; init; } = new();
    public List<string> Failed { get; init; } = new();
}

/// <summary>
/// 应用冲突修复计划:先整目录备份,再禁用/替换/安装。
/// 永远不删除文件,只重命名为 .disabled。
/// </summary>
public static class ModConflictFixer
{
    public static async Task<ModFixOutcome> ApplyAsync(
        string modsDir, string gameVersion, string loader,
        IEnumerable<ModFixAction> actions, HttpClient http,
        IProgress<string>? progress, CancellationToken ct)
    {
        var applied = new List<string>();
        var failed = new List<string>();
        var backup = Backup(modsDir);
        progress?.Report("已备份模组目录");

        foreach (var action in actions)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                switch (action.Kind)
                {
                    case ModFixKind.DisableDuplicate:
                    case ModFixKind.DisableIncompatible:
                        Disable(modsDir, action.FileName);
                        applied.Add("已禁用 " + action.FileName);
                        progress?.Report(applied[^1]);
                        break;

                    case ModFixKind.ReplaceVersion:
                    {
                        progress?.Report("正在查找 " + action.DependencyName + " 的兼容版本…");
                        var resolved = await ResolveAsync(
                            http, action.DependencyId, gameVersion, loader, action.Constraint, ct, action.InstalledSha1)
                            .ConfigureAwait(false);
                        if (resolved == null)
                        {
                            if (action.DisableIfNoReplacement)
                            {
                                Disable(modsDir, action.FileName);
                                applied.Add("没有适配的版本，已禁用 " + action.FileName);
                                progress?.Report(applied[^1]);
                                break;
                            }
                            failed.Add(action.DependencyName + "：没有找到满足 " + action.Constraint + " 的版本");
                            break;
                        }
                        await DownloadAsync(http, resolved.Url, Path.Combine(modsDir, resolved.FileName), resolved.Sha1, ct)
                            .ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(action.FileName)) Disable(modsDir, action.FileName);
                        applied.Add($"{action.DependencyName} {action.InstalledVersion} → {resolved.VersionNumber}");
                        progress?.Report(applied[^1]);
                        break;
                    }

                    case ModFixKind.InstallDependency:
                    {
                        progress?.Report("正在安装 " + action.DependencyName + "…");
                        var resolved = await ResolveAsync(
                            http, action.DependencyId, gameVersion, loader, action.Constraint, ct, action.InstalledSha1)
                            .ConfigureAwait(false);
                        if (resolved == null)
                        {
                            failed.Add(action.DependencyName + "：没有找到适配 " + gameVersion + " 的版本");
                            break;
                        }
                        await DownloadAsync(http, resolved.Url, Path.Combine(modsDir, resolved.FileName), resolved.Sha1, ct)
                            .ConfigureAwait(false);
                        applied.Add($"已安装 {action.DependencyName} {resolved.VersionNumber}");
                        progress?.Report(applied[^1]);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                failed.Add((string.IsNullOrEmpty(action.ModName) ? action.ModId : action.ModName) + "：" + ex.Message);
            }
        }

        return new ModFixOutcome
        {
            BackupCreated = backup != null,
            BackupDirectory = backup ?? "",
            Applied = applied,
            Failed = failed,
        };
    }

    private sealed record ResolvedFile(string FileName, string Url, string Sha1, string VersionNumber);

    private static async Task<ResolvedFile?> ResolveAsync(
        HttpClient http, string id, string gameVersion, string loader,
        string? constraint, CancellationToken ct, string? sha1)
    {
        var modrinth = await Fuse.Core.Sources.ModrinthSource.ResolveCompatibleAsync(
            http, id, gameVersion, loader, constraint, ct, sha1).ConfigureAwait(false);
        if (modrinth != null)
            return new ResolvedFile(modrinth.FileName, modrinth.Url, modrinth.Sha1, modrinth.VersionNumber);

        var cf = await Fuse.Core.Sources.CurseForgeSource.ResolveCompatibleAsync(
            id, gameVersion, loader, constraint, ct).ConfigureAwait(false);
        return cf == null ? null : new ResolvedFile(cf.FileName, cf.Url, cf.Sha1, cf.VersionNumber);
    }

    /// <summary>最近一次修复的备份目录;没有返回 null。</summary>
    public static string? LatestBackup(string modsDir)
    {
        try
        {
            var parent = Path.GetDirectoryName(modsDir);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) return null;
            return Directory.GetDirectories(parent, "mods-backup-*")
                .OrderByDescending(d => d)
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>撤销修复:当前 mods 移到 mods-pre-undo-时间,再把备份内容拷回来。</summary>
    public static bool RestoreBackup(string modsDir, string backupDir, out string error)
    {
        error = "";
        try
        {
            if (!Directory.Exists(backupDir)) { error = "备份目录不存在"; return false; }
            var parent = Path.GetDirectoryName(modsDir)!;
            var aside = Path.Combine(parent, "mods-pre-undo-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            if (Directory.Exists(modsDir))
                Directory.Move(modsDir, aside);
            Directory.CreateDirectory(modsDir);
            foreach (var file in Directory.GetFiles(backupDir))
                File.Copy(file, Path.Combine(modsDir, Path.GetFileName(file)), true);
            foreach (var dir in Directory.GetDirectories(backupDir))
                CopyDirectory(dir, Path.Combine(modsDir, Path.GetFileName(dir)));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string? Backup(string modsDir)
    {
        try
        {
            if (!Directory.Exists(modsDir)) return null;
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var dir = Path.Combine(Path.GetDirectoryName(modsDir)!, "mods-backup-" + stamp);
            Directory.CreateDirectory(dir);
            foreach (var file in Directory.GetFiles(modsDir))
                File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);
            foreach (var sub in Directory.GetDirectories(modsDir))
                CopyDirectory(sub, Path.Combine(dir, Path.GetFileName(sub)));
            return dir;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    private static void Disable(string modsDir, string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return;
        var source = Path.Combine(modsDir, fileName);
        if (!File.Exists(source)) return;
        var target = source + ".disabled";
        if (File.Exists(target)) target = source + "." + DateTime.Now.Ticks + ".disabled";
        File.Move(source, target);
    }

    private static async Task DownloadAsync(
        HttpClient http, string url, string destPath, string? sha1, CancellationToken ct)
    {
        var temp = destPath + ".part";
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(120));
                using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                           .ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    using var output = File.Create(temp);
                    await input.CopyToAsync(output, 64 * 1024, cts.Token).ConfigureAwait(false);
                }
                if (!string.IsNullOrEmpty(sha1))
                {
                    using var hash = SHA1.Create();
                    using var stream = File.OpenRead(temp);
                    var actual = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                    if (!actual.Equals(sha1, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("下载校验失败(SHA-1 不匹配)");
                }
                if (File.Exists(destPath)) File.Delete(destPath);
                File.Move(temp, destPath);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
                if (ct.IsCancellationRequested) throw;
                await Task.Delay(1500 * (attempt + 1), ct).ConfigureAwait(false);
            }
        }
        throw new IOException("下载失败(重试 3 次)：" + last?.Message, last);
    }
}
