using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SMMS.Core.Configuration;
using SMMS.Core.Translation;
using SMMS.UseCases.Game;

namespace SMMS.UseCases.Translation;

public sealed record TranslationResult(bool Success, string? Text, string? ErrorKey);

/// <summary>
/// 翻译入口。按设置里的供应商分发到具体实现，并做结果缓存。
/// 缓存按「供应商 + 原文」存，切换供应商不会串味。
/// </summary>
public sealed class TranslationService(
    IEnumerable<ITranslationProvider> providers,
    GameInstallationService games,
    ILogger<TranslationService> logger)
{
    private const string DefaultTarget = "zh";

    private readonly List<ITranslationProvider> _providers = providers.ToList();

    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    public IReadOnlyList<ITranslationProvider> Providers => _providers;

    /// <summary>当前生效的供应商。配置缺失或未注册时回退到第一个（MyMemory）。</summary>
    public ITranslationProvider ActiveProvider
    {
        get
        {
            var id = games.Settings.TranslationProvider;

            return _providers.FirstOrDefault(provider => string.Equals(provider.Id, id, StringComparison.OrdinalIgnoreCase))
                   ?? _providers[0];
        }
    }

    public async Task<TranslationResult> TranslateToChineseAsync(
        string? text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new TranslationResult(false, null, "translate.error.empty");
        }

        var provider = ActiveProvider;
        var source = text.Trim();
        var cacheKey = string.Join('\u0001', provider.Id, source);

        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            return new TranslationResult(true, cached, null);
        }

        var request = new TranslationRequest(
            source,
            "en",
            DefaultTarget,
            ResolveCredentials(games.Settings, provider));

        var outcome = await provider.TranslateAsync(request, cancellationToken).ConfigureAwait(false);

        if (!outcome.Success || string.IsNullOrWhiteSpace(outcome.Text))
        {
            logger.LogWarning("翻译失败（{Provider}）：{Error}", provider.Id, outcome.ErrorKey);
            return new TranslationResult(false, null, outcome.ErrorKey ?? "translate.error.failed");
        }

        _cache[cacheKey] = outcome.Text;
        return new TranslationResult(true, outcome.Text, null);
    }

    /// <summary>取该供应商保存的凭证；空字典也能工作（无需凭证的供应商）。</summary>
    private static Dictionary<string, string> ResolveCredentials(AppSettings settings, ITranslationProvider provider)
    {
        if (settings.TranslationCredentials.TryGetValue(provider.Id, out var stored) && stored.Count > 0)
        {
            return stored;
        }

        // 兼容早期只有百度两字段的配置。
        if (provider.Id == "baidu"
            && !string.IsNullOrWhiteSpace(settings.BaiduAppId)
            && !string.IsNullOrWhiteSpace(settings.BaiduSecret))
        {
            return new Dictionary<string, string>
            {
                ["appId"] = settings.BaiduAppId,
                ["secret"] = settings.BaiduSecret
            };
        }

        return new Dictionary<string, string>();
    }
}
