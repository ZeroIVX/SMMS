using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace SMMS.UseCases.Network;

/// <summary>单个目标的可达性结果。任何 HTTP 响应都算「可达」——状态码是服务端策略，不是网络问题。</summary>
public sealed record ProbeTargetResult(
    string TargetKey,
    bool Reachable,
    int? StatusCode,
    long ElapsedMs,
    string? ErrorKey);

public sealed record NexusProbeResult(bool AnyReachable, IReadOnlyList<ProbeTargetResult> Targets);

/// <summary>
/// Nexus Mods 连通性探测。
/// </summary>
/// <remarks>
/// 只测网络可达性，不涉及账号与密钥：拿不到 200 也无所谓，
/// 收到 401/403/412 恰恰说明链路是通的、只是服务端在拦。
/// 真正需要区分的是「连不上」和「连上了但被拒」。
/// </remarks>
public sealed class NetworkProbeService(ILogger<NetworkProbeService> logger)
{
    private const int TimeoutSeconds = 8;

    private static readonly HttpClient Client = CreateClient();

    private static readonly (string Key, string Url)[] Targets =
    [
        ("site", "https://www.nexusmods.com/"),
        ("api", "https://api.nexusmods.com/v1/games/stardewvalley.json")
    ];

    public async Task<NexusProbeResult> ProbeNexusAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<ProbeTargetResult>();

        foreach (var (key, url) in Targets)
        {
            results.Add(await ProbeAsync(key, url, cancellationToken).ConfigureAwait(false));
        }

        var anyReachable = results.Any(result => result.Reachable);

        logger.LogInformation(
            "Nexus 连通性：{Detail}",
            string.Join("; ", results.Select(r => $"{r.TargetKey}={(r.Reachable ? "可达" : "不可达")} {r.StatusCode} {r.ElapsedMs}ms")));

        return new NexusProbeResult(anyReachable, results);
    }

    private static async Task<ProbeTargetResult> ProbeAsync(string key, string url, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var response = await Client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            stopwatch.Stop();

            return new ProbeTargetResult(key, true, (int)response.StatusCode, stopwatch.ElapsedMilliseconds, null);
        }
        catch (TaskCanceledException)
        {
            // HttpClient 超时走的是 TaskCanceledException，不是 TimeoutException。
            stopwatch.Stop();
            return new ProbeTargetResult(key, false, null, stopwatch.ElapsedMilliseconds, "network.error.timeout");
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();

            var errorKey = ex.InnerException is SocketException { SocketErrorCode: SocketError.HostNotFound }
                ? "network.error.dns"
                : "network.error.unreachable";

            return new ProbeTargetResult(key, false, null, stopwatch.ElapsedMilliseconds, errorKey);
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };

        // 部分 CDN 会拒绝无 User-Agent 的请求，给一个能识别的标识。
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SMMS/0.1 (Stardew Valley mod manager)");

        return client;
    }
}
