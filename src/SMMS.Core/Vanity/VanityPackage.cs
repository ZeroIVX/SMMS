namespace SMMS.Core.Vanity;

/// <summary>
/// 一个 Content 替换型美化包。这类包没有 manifest.json，
/// 靠覆盖游戏资源文件生效，因此只能以「文件集合」的形式管理。
/// </summary>
public sealed class VanityPackage
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>导入时的来源压缩包路径，仅用于追溯。</summary>
    public string? SourceArchive { get; set; }

    public DateTimeOffset ImportedAt { get; set; }

    public int FileCount { get; set; }

    public long TotalBytes { get; set; }

    /// <summary>包内相对 Content 根的子目录，通常是 Content 本身。</summary>
    public string ContentRoot { get; set; } = "Content";

    public string? Note { get; set; }
}

/// <summary>当前生效的美化状态。空包表示原版。</summary>
public sealed class VanityState
{
    public string? ActivePackageId { get; set; }

    public string? ActivePackageName { get; set; }

    public DateTimeOffset? AppliedAt { get; set; }

    /// <summary>已覆盖的相对路径（相对 Content 根），用于还原与失效检测。</summary>
    public List<string> AppliedFiles { get; set; } = [];

    public bool IsActive => !string.IsNullOrEmpty(ActivePackageId);
}
