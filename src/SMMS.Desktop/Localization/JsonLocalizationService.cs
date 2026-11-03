using System.Globalization;
using System.Text.Json;
using Avalonia.Platform;
using Microsoft.Extensions.Logging;

namespace SMMS.Desktop.Localization;

/// <summary>
/// 从 <c>Assets/i18n</c> 加载 JSON 文案，并把键值写入 <see cref="Avalonia.Application.Resources"/>，
/// 界面通过 <c>DynamicResource</c> 引用，因此切换语言时无需重建窗口即可全局刷新。
/// </summary>
public sealed class JsonLocalizationService(ILogger<JsonLocalizationService> logger) : ILocalizationService
{
    private const string FallbackLocale = "en";
    private const string ResourceRoot = "avares://SMMS.Desktop/Assets/i18n";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly Dictionary<string, string> _strings = new(StringComparer.Ordinal);
    private readonly HashSet<string> _injectedKeys = new(StringComparer.Ordinal);

    public CultureInfo CurrentCulture { get; private set; } = CultureInfo.CurrentUICulture;

    public IReadOnlyList<LanguageOption> AvailableLanguages { get; private set; } = [];

    public event EventHandler? LanguageChanged;

    public string this[string key] =>
        !string.IsNullOrEmpty(key) && _strings.TryGetValue(key, out var value) ? value : key ?? string.Empty;

    public void Initialize(string? preferredLocale)
    {
        AvailableLanguages = LoadIndex();
        Apply(Resolve(preferredLocale));
    }

    public void SetLanguage(string locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
        {
            return;
        }

        Apply(locale);
    }

    private void Apply(string locale)
    {
        var primary = LoadStrings(locale);
        var fallback = string.Equals(locale, FallbackLocale, StringComparison.OrdinalIgnoreCase)
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : LoadStrings(FallbackLocale);

        _strings.Clear();

        // 回退语言先铺底，主语言覆盖，缺键时不会显示空白。
        foreach (var (key, value) in fallback)
        {
            _strings[key] = value;
        }

        foreach (var (key, value) in primary)
        {
            _strings[key] = value;
        }

        CurrentCulture = CultureInfo.GetCultureInfo(locale);
        CultureInfo.DefaultThreadCurrentUICulture = CurrentCulture;
        CultureInfo.CurrentUICulture = CurrentCulture;

        PublishToResources();
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PublishToResources()
    {
        if (Avalonia.Application.Current is not { } application)
        {
            return;
        }

        var resources = application.Resources;

        // 只移除自己注入的键，避免影响主题或第三方资源。
        foreach (var key in _injectedKeys)
        {
            resources.Remove(key);
        }

        _injectedKeys.Clear();

        foreach (var (key, value) in _strings)
        {
            resources[key] = value;
            _injectedKeys.Add(key);
        }
    }

    private List<LanguageOption> LoadIndex()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri($"{ResourceRoot}/index.json"));
            var index = JsonSerializer.Deserialize<LanguageIndex>(stream, JsonOptions);
            return index?.Languages ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException)
        {
            logger.LogError(ex, "读取语言索引失败，界面将回退到内置文案");
            return [];
        }
    }

    private Dictionary<string, string> LoadStrings(string locale)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri($"{ResourceRoot}/{locale}.json"));
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream, JsonOptions)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException)
        {
            logger.LogWarning(ex, "读取语言文件失败：{Locale}", locale);
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private string Resolve(string? preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred)
            && AvailableLanguages.Any(option =>
                string.Equals(option.Locale, preferred, StringComparison.OrdinalIgnoreCase)))
        {
            return preferred;
        }

        var candidate = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? "zh-Hans"
            : FallbackLocale;

        if (AvailableLanguages.Count == 0)
        {
            return candidate;
        }

        return AvailableLanguages.Any(option =>
            string.Equals(option.Locale, candidate, StringComparison.OrdinalIgnoreCase))
            ? candidate
            : AvailableLanguages[0].Locale;
    }

    private sealed record LanguageIndex(List<LanguageOption> Languages);
}
