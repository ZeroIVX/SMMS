using SMMS.Core.Updates;

namespace SMMS.Core.Ports;

/// <summary>
/// SMAPI 官方更新检查服务。公开接口，无需账号或 API Key，
/// 三档账号能力一致——这也是 SMMS 选择它作为检测唯一数据源的原因。
/// </summary>
public interface ISmapiCatalog
{
    Task<UpdateCheckResult> CheckAsync(UpdateCheckRequest request, CancellationToken cancellationToken = default);
}
