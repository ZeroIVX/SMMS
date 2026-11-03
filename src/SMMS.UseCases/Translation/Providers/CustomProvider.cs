using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SMMS.Core.Translation;

namespace SMMS.UseCases.Translation.Providers;

/// <summary>
/// 自定义接口：用模板适配任意 REST 翻译服务。
/// </summary>
/// <remarks>
/// 需要用户提供四样东西：请求地址、请求头、JSON 请求体模板、结果路径。
/// 模板里可用 <c>{text}</c> <c>{from}</c> <c>{to}</c> 占位符，替换时会自动做 JSON 转义，
/// 因此用户按普通 JSON 写即可，不用手动处理引号和换行。
/// 这样腾讯云、阿里云这类签名复杂但本质是 REST 的服务，用户用脚本中转后也能接进来。
/// </remarks>
public sealed class CustomProvider(ILogger<CustomProvider> logger) : ITranslationProvider
{
    public string Id => "custom";

    public string DisplayName => "自定义接口";

    public IReadOnlyList<ProviderCredentialField> Credentials =>
    [
        new("url", "请求地址", Placeholder: "https://api.example.com/translate"),
        new("headers", "请求头", Placeholder: "每行一个，如 Authorization: Bearer xxx"),
        new("body", "请求体模板", Placeholder: "{\"text\":\"{text}\",\"from\":\"{from}\",\"to\":\"{to}\"}"),
        new("resultPath", "结果路径", Placeholder: "如 data.translation")
    ];

    public bool RequiresForeignNetwork => false;

    public async Task<TranslationOutcome> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        if (!request.Credentials.TryGetValue("url", out var url) || string.IsNullOrWhiteSpace(url))
        {
            return TranslationOutcome.Fail("translate.error.missingCredential");
        }

        // 只放行 http/https，防止 file:// 或内网地址被误当成翻译接口。
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return TranslationOutcome.Fail("translate.error.invalidUrl");
        }

        if (!request.Credentials.TryGetValue("resultPath", out var resultPath) || string.IsNullOrWhiteSpace(resultPath))
        {
            return TranslationOutcome.Fail("translate.error.missingCredential");
        }

        try
        {
            var body = (request.Credentials.GetValueOrDefault("body") ?? string.Empty)
                .Replace("{text}", JsonEscape(request.Text), StringComparison.Ordinal)
                .Replace("{from}", JsonEscape(request.SourceLanguage), StringComparison.Ordinal)
                .Replace("{to}", JsonEscape(request.TargetLanguage), StringComparison.Ordinal);

            using var message = new HttpRequestMessage(HttpMethod.Post, url);
            message.Content = new StringContent(body, Encoding.UTF8, "application/json");

            foreach (var line in (request.Credentials.GetValueOrDefault("headers") ?? string.Empty)
                         .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0)
                {
                    continue;
                }

                var name = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim();

                // Content-Type / Content-Length 属于内容头，必须加到 Content 上，
                // 加到 message.Headers 会被静默忽略。
                if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    message.Content.Headers.TryAddWithoutValidation(name, value);
                }
                else
                {
                    message.Headers.TryAddWithoutValidation(name, value);
                }
            }

            using var response = await ProviderHttp.Client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using var document = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var text = ResolvePath(document.RootElement, resultPath);

            return string.IsNullOrWhiteSpace(text)
                ? TranslationOutcome.Fail("translate.error.resultPath")
                : TranslationOutcome.Ok(text);
        }
        catch (TaskCanceledException)
        {
            return TranslationOutcome.Fail("translate.error.network");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            logger.LogWarning(ex, "自定义翻译接口请求失败");
            return TranslationOutcome.Fail("translate.error.failed");
        }
    }

    /// <summary>按点分路径取字符串，支持数组下标（如 <c>trans_result.0.dst</c>）。</summary>
    private static string? ResolvePath(JsonElement root, string path)
    {
        var current = root;

        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current.ValueKind == JsonValueKind.Array)
            {
                if (!int.TryParse(segment, out var index) || index < 0 || index >= current.GetArrayLength())
                {
                    return null;
                }

                current = current[index];
                continue;
            }

            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
            {
                return null;
            }

            current = next;
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.ToString(),
            _ => null
        };
    }

    private static string JsonEscape(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
}
