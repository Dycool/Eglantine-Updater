using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace EglantineUpdater.Core;

internal static class Installer
{
    internal const string Emulator = "eglantine.exe";
    internal const string StateFolder = ".eglantine-launcher";
    private const string Pending = "pending";
    private const string Staging = "staging";
    private const string Finished = "finished";
    private const string Journal = "journal.json";
    private sealed record Change(string Path, string StagedPath, bool HadOriginal);

    internal static bool HasPendingUpdate(string state) => Directory.Exists(Path.Combine(state, Pending));

    internal static async Task InstallAsync(ReleaseInfo release, string root, string state,
        IProgress<string> progress, Func<bool> emulatorRunning)
    {
        var staging = Path.Combine(state, Staging);
        var pending = Path.Combine(state, Pending);
        EnsurePlainDirectory(staging);
        var zip = Path.Combine(staging, "update.zip");
        try
        {
            progress.Report($"Downloading {release.Tag}.");
            await DownloadAsync(release, zip);
            progress.Report("Verifying SHA-256.");
            await using (var file = File.OpenRead(zip))
            {
                var digest = Convert.ToHexString(await SHA256.HashDataAsync(file));
                if (digest != release.Digest) throw new InvalidDataException("The update ZIP failed SHA-256 verification.");
            }

            progress.Report("Preparing update.");
            var extracted = Path.Combine(staging, "extracted");
            using (var archive = ZipFile.OpenRead(zip))
            {
                if (archive.Entries.Count > 50_000 || archive.Entries.Sum(e => (decimal)e.Length) > 8L * 1024 * 1024 * 1024)
                    throw new InvalidDataException("The update archive is too large to extract.");
                foreach (var entry in archive.Entries)
                    ValidateRelativePath(entry.FullName.TrimEnd('/', '\\'));
                archive.ExtractToDirectory(extracted);
            }

            var payload = FindPayload(extracted);
            var paths = Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(payload, file)).ToArray();
            foreach (var path in paths) ValidatePayloadPath(path);

            var changes = new List<Change>();
            var payloadPath = Path.GetRelativePath(staging, payload);
            foreach (var path in paths)
                changes.Add(BackUp(root, staging, path, Path.Combine(payloadPath, path)));
            var identityPath = Path.Combine(StateFolder, "installed");
            changes.Add(BackUp(root, staging, identityPath, "identity"));
            File.WriteAllText(Path.Combine(staging, "identity"), release.Identity);
            using (var journal = new FileStream(Path.Combine(staging, Journal), FileMode.CreateNew, FileAccess.Write))
            {
                JsonSerializer.Serialize(journal, changes);
                journal.Flush(flushToDisk: true);
            }

            if (emulatorRunning()) throw new IOException("Eglantine is running. Close it to apply the update.");
            Directory.Move(staging, pending);
            try
            {
                progress.Report("Installing update.");
                foreach (var change in changes)
                    ReplaceFile(CheckedPath(pending, change.StagedPath), CheckedPath(root, change.Path));
                Directory.Move(pending, Path.Combine(state, Finished));
            }
            catch
            {
                Recover(root, state);
                throw;
            }
        }
        finally
        {
            TryClean(staging);
            TryClean(Path.Combine(state, Finished));
        }
    }

    internal static void Recover(string root, string state)
    {
        var pending = Path.Combine(state, Pending);
        var finished = Path.Combine(state, Finished);
        Clean(Path.Combine(state, Staging));
        Clean(finished);
        if (!Directory.Exists(pending)) return;
        EnsurePlainDirectory(pending);

        var changes = JsonSerializer.Deserialize<Change[]>(File.ReadAllText(CheckedPath(pending, Journal)))
            ?? throw new InvalidDataException("The previous update has an unreadable recovery journal.");
        foreach (var change in changes.Reverse())
        {
            if (change.Path != Path.Combine(StateFolder, "installed")) ValidatePayloadPath(change.Path);
            // A staged file disappears only when it has been moved into the install.
            if (File.Exists(CheckedPath(pending, change.StagedPath))) continue;
            var target = CheckedPath(root, change.Path);
            if (change.HadOriginal)
            {
                var backup = CheckedPath(pending, Path.Combine("backup", change.Path));
                var restore = CheckedPath(pending, "restore.tmp");
                File.Copy(backup, restore, overwrite: true);
                ReplaceFile(restore, target);
            }
            else
            {
                File.Delete(target);
            }
        }

        Directory.Move(pending, finished);
        TryClean(finished);
    }

    private static async Task DownloadAsync(ReleaseInfo release, string target)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = timeout.Token;
        using var response = await GitHubReleases.Http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(target);
        var buffer = new byte[128 * 1024];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) != 0)
        {
            total += read;
            if (total > release.Size) throw new InvalidDataException("The update exceeds the published asset size.");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        if (total != release.Size) throw new InvalidDataException("The update ZIP is incomplete.");
    }

    private static string FindPayload(string extracted)
    {
        if (File.Exists(Path.Combine(extracted, Emulator))) return extracted;
        var folders = Directory.GetDirectories(extracted);
        if (folders.Length == 1 && File.Exists(Path.Combine(folders[0], Emulator))) return folders[0];
        throw new InvalidDataException($"The update ZIP does not contain {Emulator} in the expected location.");
    }

    private static Change BackUp(string root, string staging, string relativePath, string stagedPath)
    {
        var original = CheckedPath(root, relativePath);
        if (Directory.Exists(original))
            throw new IOException($"A directory conflicts with the update file {relativePath}.");

        var exists = File.Exists(original);
        if (exists)
        {
            var backup = Path.Combine(staging, "backup", relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(original, backup);
        }
        return new Change(relativePath, stagedPath, exists);
    }

    private static void ReplaceFile(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(source, target, overwrite: true);
    }

    private static void ValidatePayloadPath(string path)
    {
        ValidateRelativePath(path);
        var top = path.Split('/', '\\')[0];
        if (top.Equals(StateFolder, StringComparison.OrdinalIgnoreCase)
            || top.Equals("user", StringComparison.OrdinalIgnoreCase)
            || top.StartsWith("EglantineLauncher", StringComparison.OrdinalIgnoreCase)
            || top.Equals("EglantineUpdater.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The update contains a reserved path: {path}");
    }

    private static void ValidateRelativePath(string path)
    {
        foreach (var part in path.Split('/', '\\'))
        {
            var stem = part.Split('.')[0];
            if (part.Length == 0 || part.EndsWith('.') || part.EndsWith(' ')
                || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || System.Text.RegularExpressions.Regex.IsMatch(stem,
                    @"\A(?:CON|PRN|AUX|NUL|COM[0-9���]|LPT[0-9���])\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new InvalidDataException($"The update contains an invalid Windows path: {path}");
        }
    }

    internal static string CheckedPath(string root, string path)
    {
        ValidateRelativePath(path);
        foreach (var part in path.Split('/', '\\'))
        {
            root = Path.Combine(root, part);
            RejectReparsePoint(root);
        }
        return root;
    }

    internal static void EnsurePlainDirectory(string path)
    {
        RejectReparsePoint(path);
        Directory.CreateDirectory(path);
    }

    private static void RejectReparsePoint(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Updates cannot traverse a link or junction: {path}");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static void Clean(string path)
    {
        RejectReparsePoint(path);
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private static void TryClean(string path)
    {
        try { Clean(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
