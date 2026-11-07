using System.Text.Json;
using System.Text.RegularExpressions;

namespace EglantineUpdater.Core;

internal sealed record ReleaseInfo(string Tag, Uri Url, long Size, string Digest)
{
    public string Identity => Tag + "\n" + Digest;
}

internal static class GitHubReleases
{
    private const string Repo = "Djihads80/Eglantine";
    private static readonly Regex AssetPattern = new(@"^eglantine-windows-x86_64-pgo.*\.zip$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    internal static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    static GitHubReleases()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("EglantineLauncher/1.0");
    }

    internal static async Task<ReleaseInfo> GetLatestAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{Repo}/releases?per_page=30");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token);

        foreach (var release in json.RootElement.EnumerateArray()
            .Where(r => !r.GetProperty("draft").GetBoolean() && r.GetProperty("published_at").ValueKind == JsonValueKind.String)
            .OrderByDescending(r => r.GetProperty("published_at").GetDateTimeOffset()))
        {
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                if (!AssetPattern.IsMatch(asset.GetProperty("name").GetString() ?? "")) continue;

                var digest = asset.TryGetProperty("digest", out var hash) ? hash.GetString() : null;
                if (digest is null || !Regex.IsMatch(digest, @"\Asha256:[a-fA-F0-9]{64}\z"))
                    throw new InvalidDataException("GitHub did not provide a SHA-256 checksum for the latest build.");

                if (!Uri.TryCreate(asset.GetProperty("browser_download_url").GetString(), UriKind.Absolute, out var url)
                    || url.Scheme != Uri.UriSchemeHttps || url.Host != "github.com" || !url.IsDefaultPort
                    || url.UserInfo.Length != 0
                    || !url.AbsolutePath.StartsWith($"/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The release points outside the expected GitHub downloads.");

                var size = asset.GetProperty("size").GetInt64();
                if (size is <= 0 or > 2L * 1024 * 1024 * 1024)
                    throw new InvalidDataException("Unexpected update download size.");

                return new ReleaseInfo(release.GetProperty("tag_name").GetString()!, url, size,
                    digest[7..].ToUpperInvariant());
            }
        }
        throw new InvalidDataException("No matching Windows x64 PGO release was found.");
    }
}
