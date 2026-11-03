namespace SMMS.Core.Nexus;

/// <summary>从 Nexus GraphQL 取回的模组概要。全部字段匿名可取，不需要密钥。</summary>
public sealed record NexusModInfo(
    int ModId,
    string Name,
    string Version,
    string Author,
    string Summary,
    string? PictureUrl,
    string? Category,
    long Downloads)
{
    /// <summary>网页地址，可直接交给浏览器打开。</summary>
    public string PageUrl => $"https://www.nexusmods.com/stardewvalley/mods/{ModId}";

    public bool HasPicture => !string.IsNullOrWhiteSpace(PictureUrl);
}
