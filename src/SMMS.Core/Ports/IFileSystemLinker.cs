namespace SMMS.Core.Ports;

/// <summary>
/// 目录链接能力。用于在多个配置档之间共享同一份模组文件以节省磁盘。
/// 硬链接与 junction 都要求同卷，跨卷时调用方应回退到复制。
/// </summary>
public interface IFileSystemLinker
{
    bool AreOnSameVolume(string pathA, string pathB);

    bool TryCreateDirectoryLink(string linkPath, string targetPath);
}
