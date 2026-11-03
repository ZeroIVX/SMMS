using System.IO.Compression;

namespace SMMS.Core.Archives;

/// <summary>压缩包里的东西属于哪一类。</summary>
public enum PackageKind
{
    Unknown = 0,

    /// <summary>含 manifest.json —— 模组或内容包，应该进 Mods 目录。</summary>
    Mod,

    /// <summary>只有 Content 下的 XNB —— 资源替换型美化包。</summary>
    Vanity,

    /// <summary>含 profile.json —— SMMS 配置档包。</summary>
    ProfilePack
}

/// <summary>
/// 判断压缩包类型。
/// </summary>
/// <remarks>
/// 关键点：Content Patcher 美化包**也是模组**——它有 manifest.json，
/// 靠运行时替换贴图生效，解压进 Mods 目录就能用。
/// 只有那些直接覆盖 Content/*.xnb、没有 manifest 的包才需要走美化包管理。
/// 判反了会导致 CP 包被塞进 Content 覆盖流程，既加载不了也还原不了。
/// </remarks>
public static class PackageInspector
{
    public static PackageKind Inspect(string archivePath)
    {
        if (!File.Exists(archivePath))
        {
            return PackageKind.Unknown;
        }

        try
        {
            using var archive = ZipFile.OpenRead(archivePath);

            var hasManifest = false;
            var hasContentJson = false;
            var hasProfile = false;
            var hasXnb = false;

            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');

                if (name.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase))
                {
                    hasXnb = true;
                }
                else if (IsFile(name, "manifest.json"))
                {
                    hasManifest = true;
                }
                else if (IsFile(name, "content.json"))
                {
                    hasContentJson = true;
                }
                else if (IsFile(name, "profile.json"))
                {
                    hasProfile = true;
                }
            }

            // 顺序很重要：配置档包 > 模组 > 美化包。
            // 一个 CP 包可能同时带 xnb 素材，但有 manifest 就一定是模组。
            if (hasProfile)
            {
                return PackageKind.ProfilePack;
            }

            if (hasManifest)
            {
                return PackageKind.Mod;
            }

            if (hasXnb)
            {
                return hasContentJson ? PackageKind.Mod : PackageKind.Vanity;
            }

            return PackageKind.Unknown;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return PackageKind.Unknown;
        }
    }

    private static bool IsFile(string entryName, string fileName)
        => string.Equals(entryName.Split('/')[^1], fileName, StringComparison.OrdinalIgnoreCase);
}
