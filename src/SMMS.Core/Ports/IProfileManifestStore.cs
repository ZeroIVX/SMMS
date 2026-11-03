using SMMS.Core.Profiles;

namespace SMMS.Core.Ports;

/// <summary>配置档清单的持久化。目录下固定为 <c>profile.json</c>。</summary>
public interface IProfileManifestStore
{
    /// <summary>读取清单。文件缺失或损坏时返回 <c>null</c>，不抛异常。</summary>
    ProfileManifest? Read(string profileDirectory);

    bool Write(string profileDirectory, ProfileManifest manifest);
}
