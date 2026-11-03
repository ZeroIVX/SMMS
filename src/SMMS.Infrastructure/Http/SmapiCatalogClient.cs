using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SMMS.Core.Ports;
using SMMS.Core.Updates;

namespace SMMS.Infrastructure.Http;

/// <summary>
/// smapi.io 更新检查客户端。
/// </summary>
/// <remarks>
/// 注意响应体是**数组**，每项自带 <c>id</c>；不是按 id 索引的对象。
/// 接口为公共端点，无账号要求，但属于"未正式发布"状态，可能变更。
/// </remarks>
internal sealed class SmapiCatalogClient(HttpClient http, ILogger<SmapiCatalogClient> logger) : ISmapiCatalog
{
    private const string Endpoint = "https://smapi.io/api/v4.0.0/mods";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<UpdateCheckResult> CheckAsync(
        UpdateCheckRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Mods.Count == 0)
        {
            return UpdateCheckResult.Empty;
        }

        var payload = new SmapiRequest
        {
            ApiVersion = request.ApiVersion,
            GameVersion = request.GameVersion,
            Platform = request.Platform,
            Mods = request.Mods
                .Select(mod => new SmapiRequestMod
                {
                    Id = mod.Id,
                    UpdateKeys = mod.UpdateKeys.Count > 0 ? mod.UpdateKeys.ToList() : null,
                    InstalledVersion = mod.InstalledVersion,
                    IsBroken = mod.IsBroken
                })
                .ToList()
        };

        try
        {
            using var response = await http.PostAsJsonAsync(Endpoint, payload, Options, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("smapi.io 返回非成功状态：{Status}", (int)response.StatusCode);
                return new UpdateCheckResult
                {
                    Succeeded = false,
                    Errors = [$"HTTP {(int)response.StatusCode}"]
                };
            }

            var body = await response.Content.ReadFromJsonAsync<List<SmapiModResult>>(Options, cancellationToken)
                       ?? [];

            var mods = body
                .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                .Select(item => new ModUpdateInfo
                {
                    UniqueId = item.Id!,
                    SuggestedVersion = item.SuggestedUpdate?.Version,
                    UpdateUrl = item.SuggestedUpdate?.Url ?? item.Metadata?.Main?.Url,
                    CompatibilityStatus = item.Metadata?.CompatibilityStatus,
                    BrokeIn = item.Metadata?.BrokeIn,
                    Name = item.Metadata?.Name
                })
                .ToList();

            var errors = body.SelectMany(item => item.Errors ?? []).ToList();

            logger.LogInformation(
                "smapi.io 检查完成：{Count} 个模组，{Updates} 个有更新",
                mods.Count, mods.Count(mod => mod.HasUpdate));

            return new UpdateCheckResult { Mods = mods, Errors = errors };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "smapi.io 请求失败");
            return new UpdateCheckResult
            {
                Succeeded = false,
                Errors = [ex.GetType().Name]
            };
        }
    }

    private sealed class SmapiRequest
    {
        [JsonPropertyName("mods")]
        public List<SmapiRequestMod> Mods { get; set; } = [];

        [JsonPropertyName("apiVersion")]
        public string? ApiVersion { get; set; }

        [JsonPropertyName("gameVersion")]
        public string? GameVersion { get; set; }

        [JsonPropertyName("platform")]
        public string? Platform { get; set; }

        [JsonPropertyName("includeExtendedMetadata")]
        public bool IncludeExtendedMetadata { get; set; } = true;
    }

    private sealed class SmapiRequestMod
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("updateKeys")]
        public List<string>? UpdateKeys { get; set; }

        [JsonPropertyName("installedVersion")]
        public string? InstalledVersion { get; set; }

        [JsonPropertyName("isBroken")]
        public bool IsBroken { get; set; }
    }

    private sealed class SmapiModResult
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("suggestedUpdate")]
        public SmapiSuggestedUpdate? SuggestedUpdate { get; set; }

        [JsonPropertyName("metadata")]
        public SmapiMetadata? Metadata { get; set; }

        [JsonPropertyName("errors")]
        public List<string>? Errors { get; set; }
    }

    private sealed class SmapiSuggestedUpdate
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }

    private sealed class SmapiMetadata
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("nexusID")]
        public int? NexusId { get; set; }

        [JsonPropertyName("compatibilityStatus")]
        public string? CompatibilityStatus { get; set; }

        [JsonPropertyName("brokeIn")]
        public string? BrokeIn { get; set; }

        [JsonPropertyName("main")]
        public SmapiVersionEntry? Main { get; set; }
    }

    private sealed class SmapiVersionEntry
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }
}
