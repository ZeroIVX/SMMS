namespace SMMS.Core.Ports;

using SMMS.Core.Nexus;

/// <summary>
/// Nexus 模组目录查询。只读、匿名，不涉及账号与下载。
/// </summary>
public interface INexusCatalog
{
    /// <summary>按模组尾号查询。找不到返回 null。</summary>
    Task<NexusModInfo?> GetModAsync(int modId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按条件查询目录。筛选与排序在同一次请求里组合，不会互相覆盖。
    /// </summary>
    Task<IReadOnlyList<NexusModInfo>> QueryAsync(NexusQuery query, CancellationToken cancellationToken = default);
}
