using Microsoft.Extensions.Logging;
using SMMS.Core.Configuration;
using SMMS.Core.Game;
using SMMS.Core.Ports;
using SMMS.Core.Profiles;

namespace SMMS.UseCases.Game;

/// <summary>
/// 游戏安装的解析与采纳。设置中记录的路径优先，失效时回退到自动发现。
/// </summary>
public sealed class GameInstallationService
{
    private readonly IGameLocator _locator;
    private readonly ISettingsStore _settingsStore;
    private readonly ILogger<GameInstallationService> _logger;

    public GameInstallationService(
        IGameLocator locator,
        ISettingsStore settingsStore,
        ILogger<GameInstallationService> logger)
    {
        _locator = locator;
        _settingsStore = settingsStore;
        _logger = logger;
        Settings = settingsStore.Load();
    }

    public AppSettings Settings { get; }

    public GameInstallation? Current { get; private set; }

    /// <summary>当前使用的安装发生变化（例如用户在设置页改选了目录）。</summary>
    public event EventHandler? InstallationChanged;

    /// <summary>解析当前使用的安装：先信任已保存路径，再回退自动发现。</summary>
    public GameInstallation? Resolve()
    {
        var saved = Settings.GameRootPath;
        if (!string.IsNullOrWhiteSpace(saved)
            && _locator.TryInspect(saved, out var installation)
            && installation is not null)
        {
            _logger.LogInformation("使用已保存的游戏目录：{Root}", installation.RootPath);
            Current = installation;
            return installation;
        }

        var discovered = _locator.Discover();
        Current = discovered.Count > 0 ? discovered[0] : null;

        if (Current is null)
        {
            _logger.LogInformation("未能自动定位游戏目录");
        }

        return Current;
    }

    /// <summary>校验并采纳用户指定的目录，成功后立即持久化。</summary>
    public bool TryAdopt(string rootPath, out GameInstallation? installation)
    {
        if (!_locator.TryInspect(rootPath, out installation) || installation is null)
        {
            _logger.LogWarning("指定的目录不是有效的游戏根目录：{Root}", rootPath);
            return false;
        }

        Settings.GameRootPath = installation.RootPath;
        Current = installation;
        _settingsStore.Save(Settings);
        InstallationChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void SaveSettings() => _settingsStore.Save(Settings);

    /// <summary>
    /// 解析某个配置档实际使用的游戏安装。
    /// 携带完整实例的配置档用副本启动，其余共用主安装。
    /// </summary>
    public GameInstallation? ResolveForProfile(ModProfile profile)
    {
        if (!profile.HasOwnInstance)
        {
            return Current ?? Resolve();
        }

        if (_locator.TryInspect(profile.InstanceRootPath!, out var instance) && instance is not null)
        {
            return instance;
        }

        // 副本被手动删掉或损坏时返回 null，由启动预检给出明确提示，而不是悄悄用主安装启动。
        _logger.LogWarning("配置档「{Name}」的完整实例不可用：{Path}", profile.Name, profile.InstanceRootPath);
        return null;
    }
}
