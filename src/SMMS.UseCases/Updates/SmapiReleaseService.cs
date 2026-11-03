using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SMMS.Core.Versioning;

namespace SMMS.UseCases.Updates;

public sealed record SmapiRelease(
    SemanticVersion Version,
    string TagName,
    string DownloadUrl,
    string PageUrl,
    DateTimeOffset PublishedAt);

/// <summary>
/// 查询 SMAPI 最新版本。
/// </summary>
/// <remarks>
/// 走 GitHub API 而不是 Nexus：Nexus 上 SMAPI 的下载需要登录，
/// 而 GitHub Release 是公开的。注意这里只查版本信息——
/// 在国内网络下 github.com 的下载端点往往连不上（api.github.com 却通），
/// 所以下载交给用户，SMMS 只负责"告诉你有没有新版"。
/// </remarks>
public sealed class SmapiReleaseService(ILogger<SmapiReleaseService> logger)
{
    private const string ApiUrl = "https://api.github.com/repos/Pathoschild/SMAPI/releases/latest";
    private const string PageUrl = "https://github.com/Pathoschild/SMAPI/releases/latest";

    private static readonly HttpClient Client = CreateClient();

    public async Task<SmapiRelease?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var release = await Client
                .GetFromJsonAsync<GitHubRelease>(ApiUrl, cancellationToken)
                .ConfigureAwait(false);

            if (release?.TagName is null)
            {
                return null;
            }

            if (!SemanticVersion.TryParse(release.TagName.TrimStart('v', 'V'), out var version) || version is null)
            {
                return null;
            }

            var asset = release.Assets?
                .FirstOrDefault(item => item.Name?.EndsWith("-installer.zip", StringComparison.OrdinalIgnoreCase) == true);

            logger.LogInformation("SMAPI 最新版本：{Version}", version);

            return new SmapiRelease(
                version,
                release.TagName,
                asset?.BrowserDownloadUrl ?? PageUrl,
                PageUrl,
                release.PublishedAt);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            logger.LogWarning(ex, "查询 SMAPI 最新版本失败");
            return null;
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };

        // GitHub API 强制要求 User-Agent，缺失会直接 403。
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SMMS/0.1");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        return client;
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("published_at")] DateTimeOffset PublishedAt,
        [property: JsonPropertyName("assets")] List<GitHubAsset>? Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("browser_download_url")] string? BrowserDownloadUrl);
}
