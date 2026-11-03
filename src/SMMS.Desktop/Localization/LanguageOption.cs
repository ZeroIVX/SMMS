namespace SMMS.Desktop.Localization;

/// <summary>可选语言项。<paramref name="Locale"/> 对应 <c>Assets/i18n/{locale}.json</c> 的文件名。</summary>
public sealed record LanguageOption(string Locale, string DisplayName);
