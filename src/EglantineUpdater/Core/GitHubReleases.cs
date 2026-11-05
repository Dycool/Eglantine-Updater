using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace EglantineUpdater.Core;

public sealed record ReleaseInfo(
    string Tag,
    string Name,
    string Url,
    bool Prerelease,
    DateTimeOffset Published,
    string AssetName,
    string AssetUrl,
    long AssetSize);

/// <summary>Reads the Eglantine release list from the GitHub API.</summary>
public static class GitHubReleases
{
    public const string Repo = "Djihads80/Eglantine";
    public const string ReleasesPage = "https://github.com/" + Repo + "/releases";
    public const string ApiUrl = "https://api.github.com/repos/" + Repo + "/releases?per_page=30";

    public static HttpClient Http { get; } = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; // per-request timeouts instead
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("EglantineUpdater", "1.0"));
        return client;
    }

    /// <summary>
    /// Returns the most recently published (non-draft) release, pre-releases included,
    /// that has an asset whose name matches <paramref name="assetPattern"/>; null if none.
    /// </summary>
    public static async Task<ReleaseInfo?> GetLatestAsync(string assetPattern, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        using var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var hint = (int)response.StatusCode == 403 ? " (GitHub rate limit; it will retry later)" : "";
            throw new HttpRequestException($"GitHub returned {(int)response.StatusCode} {response.ReasonPhrase}{hint}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        return await ParseAsync(stream, assetPattern, timeout.Token).ConfigureAwait(false);
    }

    public static async Task<ReleaseInfo?> ParseAsync(Stream json, string assetPattern, CancellationToken ct = default)
    {
        using var doc = await JsonDocument.ParseAsync(json, cancellationToken: ct).ConfigureAwait(false);
        var regex = new Regex(assetPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        ReleaseInfo? best = null;

        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
            if (!r.TryGetProperty("published_at", out var pub) || pub.ValueKind != JsonValueKind.String) continue;
            if (!r.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;

            var published = pub.GetDateTimeOffset();
            if (best is not null && published <= best.Published) continue;

            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                if (!regex.IsMatch(name)) continue;

                var tag = r.GetProperty("tag_name").GetString() ?? "";
                var title = r.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString())
                    ? n.GetString()!
                    : tag;
                best = new ReleaseInfo(
                    tag,
                    title,
                    r.GetProperty("html_url").GetString() ?? ReleasesPage,
                    r.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True,
                    published,
                    name,
                    a.GetProperty("browser_download_url").GetString() ?? "",
                    a.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number ? size.GetInt64() : -1);
                break;
            }
        }
        return best;
    }
}
