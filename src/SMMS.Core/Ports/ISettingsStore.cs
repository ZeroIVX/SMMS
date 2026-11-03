using SMMS.Core.Configuration;

namespace SMMS.Core.Ports;

/// <summary>
/// 应用设置的持久化。文件体积很小，采用同步读写，避免在启动路径上引入异步复杂度。
/// </summary>
public interface ISettingsStore
{
    /// <summary>读取设置。文件不存在或损坏时返回默认值，不抛异常。</summary>
    AppSettings Load();

    void Save(AppSettings settings);
}
