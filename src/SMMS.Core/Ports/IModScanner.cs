using SMMS.Core.Mods;

namespace SMMS.Core.Ports;

/// <summary>
/// 扫描模组目录并解析 manifest。只读操作，不修改任何文件。
/// </summary>
public interface IModScanner
{
    /// <summary>
    /// 扫描模组目录。<paramref name="disabledPath"/> 非空时一并扫描禁用区，
    /// 结果中以 <see cref="InstalledMod.IsEnabled"/> 区分。
    /// </summary>
    ModScanResult Scan(string modsPath, string? disabledPath = null);
}
