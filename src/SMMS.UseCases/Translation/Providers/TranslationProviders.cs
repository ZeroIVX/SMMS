using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SMMS.Core.Translation;

namespace SMMS.UseCases.Translation.Providers;

/// <summary>共享的 HTTP 客户端与签名工具，避免每个 provider 重复造轮子。</summary>
internal static class ProviderHttp
{
    public static readonly HttpClient Client = Create();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // 百度翻译协议规定用 MD5 做请求签名，这里不是安全用途，无法换算法。
    [SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "百度翻译 API 协议要求")]
    public static string Md5(string input)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

    public static string Sha256(string input)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

    private static HttpClient Create()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SMMS/0.1");
        return client;
    }
}

/// <summary>MyMemory：无凭证、免费，匿名额度约 5000 词/天。</summary>
public sealed class MyMemoryProvider(ILogger<MyMemoryProvider> logger) : ITranslationProvider
{
    private const string Endpoint = "https://api.mymemory.translated.net/get";

    public string Id => "mymemory";

    public string DisplayName => "MyMemory（免费）";

    public IReadOnlyList<ProviderCredentialField> Credentials => [];

    public bool RequiresForeignNetwork => false;

    public async Task<TranslationOutcome> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{Endpoint}?q={Uri.EscapeDataString(request.Text)}&langpair={request.SourceLanguage}|{request.TargetLanguage}";

            var response = await ProviderHttp.Client
                .GetFromJsonAsync<MyMemoryResponse>(url, ProviderHttp.JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (response?.ResponseStatus != 200)
            {
                logger.LogWarning("MyMemory 返回状态 {Status}", response?.ResponseStatus);
                return TranslationOutcome.Fail("translate.error.quota");
            }

            return response.ResponseData?.TranslatedText is { Length: > 0 } text
                ? TranslationOutcome.Ok(text)
                : TranslationOutcome.Fail("translate.error.failed");
        }
        catch (TaskCanceledException)
        {
            return TranslationOutcome.Fail("translate.error.network");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            logger.LogWarning(ex, "MyMemory 请求失败");
            return TranslationOutcome.Fail("translate.error.network");
        }
    }

    private sealed record MyMemoryResponse(
        [property: JsonPropertyName("responseData")] MyMemoryData? ResponseData,
        [property: JsonPropertyName("responseStatus")] int ResponseStatus);

    private sealed record MyMemoryData(
        [property: JsonPropertyName("translatedText")] string? TranslatedText);
}

/// <summary>百度翻译：sign = MD5(appid + q + salt + 密钥)，语言码 en → zh。</summary>
public sealed class BaiduProvider(ILogger<BaiduProvider> logger) : ITranslationProvider
{
    private const string Endpoint = "https://fanyi-api.baidu.com/api/trans/vip/translate";

    public string Id => "baidu";

    public string DisplayName => "百度翻译";

    public IReadOnlyList<ProviderCredentialField> Credentials =>
    [
        new("appId", "AppID"),
        new("secret", "密钥", IsSecret: true)
    ];

    public bool RequiresForeignNetwork => false;

    public async Task<TranslationOutcome> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        if (!request.Credentials.TryGetValue("appId", out var appId) || !request.Credentials.TryGetValue("secret", out var secret))
        {
            return TranslationOutcome.Fail("translate.error.missingCredential");
        }

        try
        {
            var salt = DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var sign = ProviderHttp.Md5(appId + request.Text + salt + secret);

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["q"] = request.Text,
                ["from"] = request.SourceLanguage,
                ["to"] = request.TargetLanguage,
                ["appid"] = appId,
                ["salt"] = salt,
                ["sign"] = sign
            });

            var payload = await (await ProviderHttp.Client
                    .PostAsync(Endpoint, content, cancellationToken)
                    .ConfigureAwait(false))
                .Content.ReadFromJsonAsync<BaiduResponse>(ProviderHttp.JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (payload?.ErrorCode is { Length: > 0 } code && code != "52000")
            {
                logger.LogWarning("百度错误码 {Code}：{Message}", code, payload.ErrorMessage);
                return TranslationOutcome.Fail(code switch
                {
                    "54003" or "54005" => "translate.error.rateLimit",
                    "54004" => "translate.error.quota",
                    "52001" or "52002" => "translate.error.network",
                    "54001" => "translate.error.invalidCredential",
                    _ => "translate.error.failed"
                });
            }

            return payload?.TransResult?.FirstOrDefault()?.Destination is { Length: > 0 } text
                ? TranslationOutcome.Ok(text)
                : TranslationOutcome.Fail("translate.error.failed");
        }
        catch (TaskCanceledException)
        {
            return TranslationOutcome.Fail("translate.error.network");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            logger.LogWarning(ex, "百度翻译请求失败");
            return TranslationOutcome.Fail("translate.error.network");
        }
    }

    private sealed record BaiduResponse(
        [property: JsonPropertyName("trans_result")] List<BaiduResult>? TransResult,
        [property: JsonPropertyName("error_code")] string? ErrorCode,
        [property: JsonPropertyName("error_msg")] string? ErrorMessage);

    private sealed record BaiduResult([property: JsonPropertyName("dst")] string? Destination);
}

/// <summary>
/// 有道智云：sign = SHA256(appKey + truncate(q) + salt + curtime + appSecret)。
/// truncate 规则：长度 ≤ 20 用原串，否则取「前10 + 长度 + 后10」。
/// </summary>
public sealed class YoudaoProvider(ILogger<YoudaoProvider> logger) : ITranslationProvider
{
    private const string Endpoint = "https://openapi.youdao.com/api";

    public string Id => "youdao";

    public string DisplayName => "有道智云";

    public IReadOnlyList<ProviderCredentialField> Credentials =>
    [
        new("appKey", "应用 ID"),
        new("appSecret", "应用密钥", IsSecret: true)
    ];

    public bool RequiresForeignNetwork => false;

    public async Task<TranslationOutcome> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        if (!request.Credentials.TryGetValue("appKey", out var appKey)
            || !request.Credentials.TryGetValue("appSecret", out var appSecret))
        {
            return TranslationOutcome.Fail("translate.error.missingCredential");
        }

        try
        {
            var salt = Guid.NewGuid().ToString("N");
            var curtime = DateTimeOffset.Now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var truncated = Truncate(request.Text);
            var sign = ProviderHttp.Sha256(appKey + truncated + salt + curtime + appSecret);

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["q"] = request.Text,
                ["from"] = request.SourceLanguage,
                ["to"] = request.TargetLanguage,
                ["appKey"] = appKey,
                ["salt"] = salt,
                ["sign"] = sign,
                ["signType"] = "v3",
                ["curtime"] = curtime
            });

            var payload = await (await ProviderHttp.Client
                    .PostAsync(Endpoint, content, cancellationToken)
                    .ConfigureAwait(false))
                .Content.ReadFromJsonAsync<YoudaoResponse>(ProviderHttp.JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (payload?.ErrorCode is { Length: > 0 } code && code != "0")
            {
                logger.LogWarning("有道错误码 {Code}", code);
                return TranslationOutcome.Fail(code switch
                {
                    "411" or "412" => "translate.error.rateLimit",
                    "401" or "108" => "translate.error.invalidCredential",
                    _ => "translate.error.failed"
                });
            }

            return payload?.Translation?.FirstOrDefault() is { Length: > 0 } text
                ? TranslationOutcome.Ok(text)
                : TranslationOutcome.Fail("translate.error.failed");
        }
        catch (TaskCanceledException)
        {
            return TranslationOutcome.Fail("translate.error.network");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            logger.LogWarning(ex, "有道翻译请求失败");
            return TranslationOutcome.Fail("translate.error.network");
        }
    }

    private static string Truncate(string text)
        => text.Length <= 20 ? text : text[..10] + text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + text[^10..];

    private sealed record YoudaoResponse(
        [property: JsonPropertyName("translation")] List<string>? Translation,
        [property: JsonPropertyName("errorCode")] string? ErrorCode);
}

/// <summary>DeepL：密钥放 Authorization 头，免费版走 api-free 域名。</summary>
public sealed class DeepLProvider(ILogger<DeepLProvider> logger) : ITranslationProvider
{
    public string Id => "deepl";

    public string DisplayName => "DeepL";

    public IReadOnlyList<ProviderCredentialField> Credentials =>
    [
        new("authKey", "Auth Key", IsSecret: true, Placeholder: "以 :fx 结尾的是免费版")
    ];

    public bool RequiresForeignNetwork => true;

    public async Task<TranslationOutcome> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        if (!request.Credentials.TryGetValue("authKey", out var authKey) || string.IsNullOrWhiteSpace(authKey))
        {
            return TranslationOutcome.Fail("translate.error.missingCredential");
        }

        try
        {
            // 免费版密钥以 :fx 结尾，域名也不同。
            var endpoint = authKey.TrimEnd().EndsWith(":fx", StringComparison.OrdinalIgnoreCase)
                ? "https://api-free.deepl.com/v2/translate"
                : "https://api.deepl.com/v2/translate";

            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(new
                {
                    text = new[] { request.Text },
                    source_lang = request.SourceLanguage.ToUpperInvariant(),
                    target_lang = request.TargetLanguage == "zh" ? "ZH" : request.TargetLanguage.ToUpperInvariant()
                })
            };
            message.Headers.TryAddWithoutValidation("Authorization", $"DeepL-Auth-Key {authKey}");

            using var response = await ProviderHttp.Client.SendAsync(message, cancellationToken).ConfigureAwait(false);

            if ((int)response.StatusCode is 403 or 401)
            {
                return TranslationOutcome.Fail("translate.error.invalidCredential");
            }

            if ((int)response.StatusCode == 456)
            {
                return TranslationOutcome.Fail("translate.error.quota");
            }

            response.EnsureSuccessStatusCode();

            var payload = await response.Content
                .ReadFromJsonAsync<DeepLResponse>(ProviderHttp.JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            return payload?.Translations?.FirstOrDefault()?.Text is { Length: > 0 } text
                ? TranslationOutcome.Ok(text)
                : TranslationOutcome.Fail("translate.error.failed");
        }
        catch (TaskCanceledException)
        {
            return TranslationOutcome.Fail("translate.error.network");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            logger.LogWarning(ex, "DeepL 请求失败");
            return TranslationOutcome.Fail("translate.error.network");
        }
    }

    private sealed record DeepLResponse(
        [property: JsonPropertyName("translations")] List<DeepLTranslation>? Translations);

    private sealed record DeepLTranslation([property: JsonPropertyName("text")] string? Text);
}

/// <summary>Google Cloud Translation v2：API Key 放查询串。</summary>
public sealed class GoogleProvider(ILogger<GoogleProvider> logger) : ITranslationProvider
{
    public string Id => "google";

    public string DisplayName => "Google 翻译";

    public IReadOnlyList<ProviderCredentialField> Credentials =>
    [
        new("apiKey", "API Key", IsSecret: true, Placeholder: "Google Cloud 控制台获取")
    ];

    public bool RequiresForeignNetwork => true;

    public async Task<TranslationOutcome> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        if (!request.Credentials.TryGetValue("apiKey", out var apiKey) || string.IsNullOrWhiteSpace(apiKey))
        {
            return TranslationOutcome.Fail("translate.error.missingCredential");
        }

        try
        {
            var url = $"https://translation.googleapis.com/language/translate/v2?key={Uri.EscapeDataString(apiKey)}";

            var payload = await (await ProviderHttp.Client
                    .PostAsJsonAsync(url, new
                    {
                        q = new[] { request.Text },
                        source = request.SourceLanguage,
                        target = request.TargetLanguage == "zh" ? "zh-CN" : request.TargetLanguage,
                        format = "text"
                    }, cancellationToken)
                    .ConfigureAwait(false))
                .Content.ReadFromJsonAsync<GoogleResponse>(ProviderHttp.JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            // Google 把 HTML 实体也一并返回，这里还原常见几种。
            var text = payload?.Data?.Translations?.FirstOrDefault()?.TranslatedText;
            return text is { Length: > 0 }
                ? TranslationOutcome.Ok(DecodeHtml(text))
                : TranslationOutcome.Fail("translate.error.failed");
        }
        catch (TaskCanceledException)
        {
            return TranslationOutcome.Fail("translate.error.network");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            logger.LogWarning(ex, "Google 翻译请求失败");
            return TranslationOutcome.Fail("translate.error.network");
        }
    }

    private static string DecodeHtml(string text) => text
        .Replace("&quot;", "\"", StringComparison.Ordinal)
        .Replace("&#39;", "'", StringComparison.Ordinal)
        .Replace("&amp;", "&", StringComparison.Ordinal)
        .Replace("&lt;", "<", StringComparison.Ordinal)
        .Replace("&gt;", ">", StringComparison.Ordinal);

    private sealed record GoogleResponse([property: JsonPropertyName("data")] GoogleData? Data);

    private sealed record GoogleData(
        [property: JsonPropertyName("translations")] List<GoogleTranslation>? Translations);

    private sealed record GoogleTranslation(
        [property: JsonPropertyName("translatedText")] string? TranslatedText);
}

/// <summary>Azure / Bing 翻译：订阅密钥放请求头，Region 为可选第二凭证。</summary>
public sealed class AzureProvider(ILogger<AzureProvider> logger) : ITranslationProvider
{
    public string Id => "azure";

    public string DisplayName => "Azure / Bing 翻译";

    public IReadOnlyList<ProviderCredentialField> Credentials =>
    [
        new("subscriptionKey", "订阅密钥", IsSecret: true),
        new("region", "区域", Placeholder: "如 eastasia，可留空")
    ];

    public bool RequiresForeignNetwork => true;

    public async Task<TranslationOutcome> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        if (!request.Credentials.TryGetValue("subscriptionKey", out var key) || string.IsNullOrWhiteSpace(key))
        {
            return TranslationOutcome.Fail("translate.error.missingCredential");
        }

        try
        {
            var target = request.TargetLanguage == "zh" ? "zh-Hans" : request.TargetLanguage;
            var url = $"https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&from={request.SourceLanguage}&to={target}";

            using var message = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new[] { new { Text = request.Text } })
            };
            message.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", key);

            if (request.Credentials.TryGetValue("region", out var region) && !string.IsNullOrWhiteSpace(region))
            {
                message.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Region", region);
            }

            using var response = await ProviderHttp.Client.SendAsync(message, cancellationToken).ConfigureAwait(false);

            if ((int)response.StatusCode is 401 or 403)
            {
                return TranslationOutcome.Fail("translate.error.invalidCredential");
            }

            if ((int)response.StatusCode == 429)
            {
                return TranslationOutcome.Fail("translate.error.rateLimit");
            }

            response.EnsureSuccessStatusCode();

            var payload = await response.Content
                .ReadFromJsonAsync<List<AzureResult>>(ProviderHttp.JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            return payload?.FirstOrDefault()?.Translations?.FirstOrDefault()?.Text is { Length: > 0 } text
                ? TranslationOutcome.Ok(text)
                : TranslationOutcome.Fail("translate.error.failed");
        }
        catch (TaskCanceledException)
        {
            return TranslationOutcome.Fail("translate.error.network");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            logger.LogWarning(ex, "Azure 翻译请求失败");
            return TranslationOutcome.Fail("translate.error.network");
        }
    }

    private sealed record AzureResult(
        [property: JsonPropertyName("translations")] List<AzureTranslation>? Translations);

    private sealed record AzureTranslation([property: JsonPropertyName("text")] string? Text);
}
