namespace SMMS.Core.Translation;

/// <summary>供应商需要用户填写的凭证字段。界面据此动态生成输入框。</summary>
public sealed record ProviderCredentialField(
    string Key,
    string Label,
    bool IsSecret = false,
    string? Placeholder = null);

/// <summary>一次翻译请求。凭证已按供应商的字段键名整理好。</summary>
public sealed record TranslationRequest(
    string Text,
    string SourceLanguage,
    string TargetLanguage,
    IReadOnlyDictionary<string, string> Credentials);

public sealed record TranslationOutcome(bool Success, string? Text, string? ErrorKey)
{
    public static TranslationOutcome Ok(string text) => new(true, text, null);

    public static TranslationOutcome Fail(string errorKey) => new(false, null, errorKey);
}

/// <summary>
/// 翻译供应商。各家的请求方式、签名算法、响应结构完全不同，
/// 因此只统一「输入文本 + 凭证 → 译文」，其余细节各实现自理。
/// </summary>
public interface ITranslationProvider
{
    /// <summary>持久化用的标识，切换供应商时不丢配置。</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>空表示无需凭证（如 MyMemory）。</summary>
    IReadOnlyList<ProviderCredentialField> Credentials { get; }

    /// <summary>该供应商是否需要代理才能在国内使用。</summary>
    bool RequiresForeignNetwork { get; }

    Task<TranslationOutcome> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken);
}
