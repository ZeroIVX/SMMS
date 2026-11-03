namespace SMMS.Core.Nexus;

/// <summary>可排序的字段。Nexus 的公开 GraphQL 只提供这些，没有「时间段下载量」。</summary>
public enum NexusSortField
{
    Downloads,
    Endorsements,
    UpdatedAt,
    CreatedAt,
    Name
}

/// <summary>
/// 一次目录查询的全部条件。筛选（关键词 / 分类）与排序可以任意组合——
/// 它们本来就是同一次请求的不同部分，分开建模才会互相覆盖。
/// </summary>
public sealed record NexusQuery
{
    public string? Keyword { get; init; }

    public string? Category { get; init; }

    public NexusSortField Sort { get; init; } = NexusSortField.Downloads;

    public bool Descending { get; init; } = true;

    public int Count { get; init; } = 20;

    /// <summary>映射到 GraphQL 的排序字段名。</summary>
    public string SortFieldName => Sort switch
    {
        NexusSortField.Endorsements => "endorsements",
        NexusSortField.UpdatedAt => "updatedAt",
        NexusSortField.CreatedAt => "createdAt",
        NexusSortField.Name => "name",
        _ => "downloads"
    };

    public string DirectionName => Descending ? "DESC" : "ASC";

    public bool HasFilters => !string.IsNullOrWhiteSpace(Keyword) || !string.IsNullOrWhiteSpace(Category);
}
