using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace EglantineUpdater.Core;

public readonly record struct InstallProgress(string Stage, int Percent);

/// <summary>Downloads a release zip and unpacks it over the Eglantine folder.</summary>
public static class Installer
{
    public const string EglantineExe = "eglantine.exe";

    public static async Task InstallAsync(ReleaseInfo release, string installDir,
        IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        var temp = Path.Combine(Path.GetTempPath(), "EglantineUpdater_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var zipPath = Path.Combine(temp, "download.zip");
            await DownloadAsync(release.AssetUrl, zipPath, progress, ct).ConfigureAwait(false);

            progress?.Report(new InstallProgress("Extracting", -1));
            var extractDir = Path.Combine(temp, "extract");
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extractDir), ct).ConfigureAwait(false);

            var root = FindPayloadRoot(extractDir);
            progress?.Report(new InstallProgress("Installing", -1));
            await Task.Run(() => CopyTree(root, installDir, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
        }
    }

    private static async Task DownloadAsync(string url, string target, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new InstallProgress("Downloading", 0));
        using var response = await GitHubReleases.Http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1;
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = File.Create(target);

        var buffer = new byte[256 * 1024];
        long done = 0;
        int lastPercent = -1, read;
        while ((read = await ReadWithIdleTimeoutAsync(input, buffer, ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            if (total > 0)
            {
                var percent = (int)(done * 100 / total);
                if (percent != lastPercent) { lastPercent = percent; progress?.Report(new InstallProgress("Downloading", percent)); }
            }
        }
        if (total > 0 && done != total)
            throw new IOException($"Download incomplete ({done:N0} of {total:N0} bytes).");
    }

    private static async Task<int> ReadWithIdleTimeoutAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            return await s.ReadAsync(buffer.AsMemory(), idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("The download stalled for 2 minutes.");
        }
    }

    /// <summary>
    /// The folder whose contents should land in the install folder: unwraps a single
    /// top-level folder, or uses wherever eglantine.exe is inside the archive.
    /// </summary>
    public static string FindPayloadRoot(string extractDir)
    {
        var root = extractDir;
        var entries = Directory.GetFileSystemEntries(extractDir);
        if (entries.Length == 1 && Directory.Exists(entries[0])) root = entries[0];

        if (!File.Exists(Path.Combine(root, EglantineExe)))
        {
            var exe = Directory.EnumerateFiles(extractDir, EglantineExe, SearchOption.AllDirectories)
                .OrderBy(p => p.Length)
                .FirstOrDefault();
            if (exe is not null) root = Path.GetDirectoryName(exe)!;
        }
        return root;
    }

    /// <summary>Copies everything from <paramref name="source"/> over <paramref name="dest"/>, overwriting files and keeping extras.</summary>
    public static void CopyTree(string source, string dest, CancellationToken ct = default)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(dest, Path.GetRelativePath(source, file));
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
                    File.Copy(file, target, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < 5)
                {
                    Thread.Sleep(1000); // file briefly locked (antivirus, process still exiting)
                }
                catch (UnauthorizedAccessException ex)
                {
                    throw new UnauthorizedAccessException(
                        $"Cannot write {target}. If Eglantine is in Program Files, move it to a folder you own.", ex);
                }
            }
        }
    }
}
