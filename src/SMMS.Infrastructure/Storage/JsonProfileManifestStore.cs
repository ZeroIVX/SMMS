using System.Text.Json;
using Microsoft.Extensions.Logging;
using SMMS.Core.Ports;
using SMMS.Core.Profiles;

namespace SMMS.Infrastructure.Storage;

/// <summary>配置档清单读写。写入走临时文件替换，避免断电留下半截 JSON。</summary>
internal sealed class JsonProfileManifestStore(ILogger<JsonProfileManifestStore> logger) : IProfileManifestStore
{
    private const string FileName = "profile.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public ProfileManifest? Read(string profileDirectory)
    {
        try
        {
            var path = Path.Combine(profileDirectory, FileName);
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ProfileManifest>(json, Options);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "读取配置档清单失败：{Directory}", profileDirectory);
            return null;
        }
    }

    public bool Write(string profileDirectory, ProfileManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        try
        {
            Directory.CreateDirectory(profileDirectory);
            manifest.UpdatedAt = DateTimeOffset.Now;

            var path = Path.Combine(profileDirectory, FileName);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, Options));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "写入配置档清单失败：{Directory}", profileDirectory);
            return false;
        }
    }
}
