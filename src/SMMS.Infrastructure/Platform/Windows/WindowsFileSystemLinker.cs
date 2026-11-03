using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using SMMS.Core.Ports;

namespace SMMS.Infrastructure.Platform.Windows;

/// <summary>
/// Windows 目录链接能力。
/// </summary>
/// <remarks>
/// 当前使用符号链接实现，需要开发者模式或管理员权限，失败时返回 false 由调用方回退到复制。
/// Phase 2 将改为 junction（P/Invoke DeviceIoControl），无需任何权限，但需要额外的原生调用实现。
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsFileSystemLinker(ILogger<WindowsFileSystemLinker> logger) : IFileSystemLinker
{
    public bool AreOnSameVolume(string pathA, string pathB)
    {
        try
        {
            var rootA = Path.GetPathRoot(Path.GetFullPath(pathA));
            var rootB = Path.GetPathRoot(Path.GetFullPath(pathB));
            return !string.IsNullOrEmpty(rootA)
                && string.Equals(rootA, rootB, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            if (Directory.Exists(linkPath) || File.Exists(linkPath))
            {
                return false;
            }

            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            logger.LogWarning(ex, "创建目录链接失败：{Link} -> {Target}", linkPath, targetPath);
            return false;
        }
    }
}
