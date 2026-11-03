namespace SMMS.Core.Configuration;

/// <summary>
/// 持久化到用户目录的应用设置。<see cref="Locale"/> 为空表示跟随系统语言。
/// </summary>
public sealed class AppSettings
{
    public string? GameRootPath { get; set; }

    public string Locale { get; set; } = string.Empty;

    public bool RegisterNxmProtocol { get; set; } = true;

    public string ActiveProfileId { get; set; } = Profiles.ModProfile.DefaultId;

    /// <summary>用户自建的配置档。内置默认档不写入此列表。</summary>
    public List<ProfileRecord> ProfileRecords { get; set; } = [];

    /// <summary>
    /// 配置档的手动排序（存 Id，按显示顺序）。
    /// 内置档（原版 / 默认）固定在前两位，不参与这里。
    /// </summary>
    public List<string> ProfileOrder { get; set; } = [];

    /// <summary>翻译供应商 Id：mymemory / baidu / youdao / deepl / google / azure / custom。</summary>
    public string TranslationProvider { get; set; } = "mymemory";

    /// <summary>各供应商的凭证，按供应商 Id 分组保存，切换时不丢配置。</summary>
    public Dictionary<string, Dictionary<string, string>> TranslationCredentials { get; set; } = [];

    /// <summary>百度翻译 AppID（早期字段，仅用于迁移到 <see cref="TranslationCredentials"/>）。</summary>
    public string BaiduAppId { get; set; } = string.Empty;

    /// <summary>百度翻译密钥（早期字段，仅用于迁移）。</summary>
    public string BaiduSecret { get; set; } = string.Empty;

    /// <summary>最近一次成功启动游戏的时间（本地时区），用于启动台「上次启动」展示。</summary>
    public DateTimeOffset? LastLaunchAt { get; set; }

    /// <summary>最近一次成功启动所用的配置档 Id，用于配置档页「环境对比」的「上次启动」列。</summary>
    public string? LastLaunchProfileId { get; set; }

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.ProfileRecords = ProfileRecords.Select(record => new ProfileRecord
        {
            Id = record.Id,
            Name = record.Name,
            ModsPath = record.ModsPath
        }).ToList();

        return copy;
    }
}
