using System.Text.Json;
using Microsoft.Extensions.Logging;
using SMMS.Core.Game;
using SMMS.UseCases.Game;

namespace SMMS.UseCases.Mods;

/// <summary>
/// 玩家自己写的模组备注。按 UniqueID 存，跨配置档共享——
/// 备注描述的是「这个模组是干什么的」，与它装在哪个配置档无关。
/// </summary>
public sealed class ModNotesService(GameInstallationService games, ILogger<ModNotesService> logger)
{
    private const string FileName = "mod-notes.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private Dictionary<string, string>? _cache;
    private string? _cachePath;

    public string Get(string uniqueId)
    {
        if (string.IsNullOrWhiteSpace(uniqueId))
        {
            return string.Empty;
        }

        var notes = Load();
        return notes.TryGetValue(uniqueId, out var note) ? note : string.Empty;
    }

    public bool Set(string uniqueId, string? note)
    {
        if (string.IsNullOrWhiteSpace(uniqueId))
        {
            return false;
        }

        var notes = Load();
        var trimmed = note?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            if (!notes.Remove(uniqueId))
            {
                return true;
            }
        }
        else if (notes.TryGetValue(uniqueId, out var existing) && existing == trimmed)
        {
            return true;
        }
        else
        {
            notes[uniqueId] = trimmed;
        }

        return Save(notes);
    }

    private Dictionary<string, string> Load()
    {
        var path = GetPath();
        if (path is null)
        {
            return [];
        }

        if (_cache is not null && string.Equals(_cachePath, path, StringComparison.OrdinalIgnoreCase))
        {
            return _cache;
        }

        try
        {
            _cache = File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), JsonOptions) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            logger.LogWarning(ex, "读取模组备注失败：{Path}", path);
            _cache = [];
        }

        _cachePath = path;
        return _cache;
    }

    private bool Save(Dictionary<string, string> notes)
    {
        var path = GetPath();
        if (path is null)
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // 先写临时文件再原子替换。File.WriteAllText 直接覆盖时若中途失败，
            // 会留下半截文件，下次加载解析失败等于把玩家所有备注一次性丢掉。
            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, JsonSerializer.Serialize(notes, JsonOptions));
            File.Move(temp, path, overwrite: true);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "保存模组备注失败：{Path}", path);
            return false;
        }
    }

    private string? GetPath()
    {
        var game = games.Current ?? games.Resolve();
        return game is null ? null : Path.Combine(game.WorkspacePath, FileName);
    }
}
