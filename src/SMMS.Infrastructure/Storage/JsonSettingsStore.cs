using System.Text.Json;
using Microsoft.Extensions.Logging;
using SMMS.Core.Configuration;
using SMMS.Core.Ports;

namespace SMMS.Infrastructure.Storage;

/// <summary>
/// 设置持久化到 <c>%AppData%\SMMS\settings.json</c>。写入采用临时文件加替换，避免断电截断。
/// </summary>
internal sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly ILogger<JsonSettingsStore> _logger;
    private readonly string _filePath;

    public JsonSettingsStore(ILogger<JsonSettingsStore> logger)
        : this(logger, GetDefaultPath())
    {
    }

    internal JsonSettingsStore(ILogger<JsonSettingsStore> logger, string filePath)
    {
        _logger = logger;
        _filePath = filePath;
    }

    public static string GetDefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SMMS",
        "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "读取设置失败，使用默认设置：{Path}", _filePath);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(settings, SerializerOptions);
            var temporary = _filePath + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, _filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "保存设置失败：{Path}", _filePath);
        }
    }
}
