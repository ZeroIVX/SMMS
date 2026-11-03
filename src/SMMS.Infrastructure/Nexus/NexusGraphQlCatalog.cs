using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SMMS.Core.Nexus;
using SMMS.Core.Ports;

namespace SMMS.Infrastructure.Nexus;

/// <summary>
/// Nexus 的公开 GraphQL 端点。匿名即可查询模组信息与列表——
/// 官方 REST API 需要 API Key（无 key 返回 401），而 GraphQL 不需要，
/// 这是「尾号查询」和「热门推荐」能落地的唯一原因。
/// </summary>
public sealed class NexusGraphQlCatalog(ILogger<NexusGraphQlCatalog> logger) : INexusCatalog
{
    private const string Endpoint = "https://api-router.nexusmods.com/graphql";
    private const int StardewValleyGameId = 1303;
    private const string StardewValleyDomain = "stardewvalley";

    private const string ModFields = "modId name version author summary pictureUrl category downloads";

    private static readonly HttpClient Client = CreateClient();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<NexusModInfo?> GetModAsync(int modId, CancellationToken cancellationToken = default)
    {
        if (modId <= 0)
        {
            return null;
        }

        var query = $"{{ mod(modId: {modId}, gameId: {StardewValleyGameId}) {{ {ModFields} }} }}";
        var response = await ExecuteAsync<ModEnvelope>(query, cancellationToken).ConfigureAwait(false);

        return response?.Mod is null ? null : Map(response.Mod);
    }

    public async Task<IReadOnlyList<NexusModInfo>> QueryAsync(
        NexusQuery query,
        CancellationToken cancellationToken = default)
    {
        var filters = new List<string> { $"gameDomainName: {{ value: \"{StardewValleyDomain}\" }}" };

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            // op: WILDCARD 是唯一可用的模糊匹配；LIKE 会报 BaseFilterValueEqualsWildcard 类型不符。
            filters.Add($"name: {{ value: \"{Escape(query.Keyword)}\", op: WILDCARD }}");
        }

        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            filters.Add($"categoryName: {{ value: \"{Escape(query.Category)}\" }}");
        }

        var gql =
            $"{{ mods(filter: {{ {string.Join(", ", filters)} }}, " +
            $"sort: [{{ {query.SortFieldName}: {{ direction: {query.DirectionName} }} }}], " +
            $"count: {Clamp(query.Count)}) {{ nodes {{ {ModFields} }} }} }}";

        return await GetListAsync(gql, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>转义 GraphQL 字符串字面量里的特殊字符，避免反斜杠或换行破坏查询语法。</summary>
    private static string Escape(string value) => value
        .Trim()
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", string.Empty, StringComparison.Ordinal);

    private async Task<IReadOnlyList<NexusModInfo>> GetListAsync(string query, CancellationToken cancellationToken)
    {
        var response = await ExecuteAsync<ListEnvelope>(query, cancellationToken).ConfigureAwait(false);
        var nodes = response?.Mods?.Nodes;

        return nodes is null ? [] : nodes.Select(Map).ToList();
    }

    private async Task<T?> ExecuteAsync<T>(string query, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { query });

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await Client.PostAsync(Endpoint, content, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Nexus GraphQL 返回 {Status}", (int)response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false);
            return body;
        }
        catch (TaskCanceledException)
        {
            logger.LogWarning("Nexus GraphQL 请求超时");
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            logger.LogWarning(ex, "Nexus GraphQL 请求失败");
            return null;
        }
    }

    private static int Clamp(int count) => Math.Clamp(count, 1, 30);

    private static NexusModInfo Map(GraphQlMod mod) => new(
        mod.ModId,
        mod.Name ?? $"#{mod.ModId}",
        mod.Version ?? "—",
        mod.Author ?? "—",
        mod.Summary ?? string.Empty,
        mod.PictureUrl,
        mod.Category,
        mod.Downloads);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SMMS/0.1 (Stardew Valley mod manager)");
        return client;
    }

    private sealed record ModEnvelope([property: JsonPropertyName("data")] ModData? Data)
    {
        public GraphQlMod? Mod => Data?.Mod;
    }

    private sealed record ModData([property: JsonPropertyName("mod")] GraphQlMod? Mod);

    private sealed record ListEnvelope([property: JsonPropertyName("data")] ListData? Data)
    {
        public ModsNode? Mods => Data?.Mods;
    }

    private sealed record ListData([property: JsonPropertyName("mods")] ModsNode? Mods);

    private sealed record ModsNode([property: JsonPropertyName("nodes")] List<GraphQlMod>? Nodes);

    private sealed record GraphQlMod(
        [property: JsonPropertyName("modId")] int ModId,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("author")] string? Author,
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("pictureUrl")] string? PictureUrl,
        [property: JsonPropertyName("category")] string? Category,
        [property: JsonPropertyName("downloads")] long Downloads);
}
