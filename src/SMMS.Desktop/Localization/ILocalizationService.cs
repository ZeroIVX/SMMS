using System.Globalization;

namespace SMMS.Desktop.Localization;

/// <summary>
/// 界面本地化。文案以 JSON 资源承载，新增语言只需增加一个 <c>Assets/i18n/{locale}.json</c>
/// 并在 <c>index.json</c> 中登记，无需改动代码。
/// </summary>
public interface ILocalizationService
{
    CultureInfo CurrentCulture { get; }

    IReadOnlyList<LanguageOption> AvailableLanguages { get; }

    event EventHandler? LanguageChanged;

    /// <summary>初始化。传 <c>null</c> 或空表示跟随系统；不可用的语言回退到英文。</summary>
    void Initialize(string? preferredLocale);

    void SetLanguage(string locale);

    /// <summary>供非 XAML 场景取文案（例如状态提示拼接）。</summary>
    string this[string key] { get; }
}
