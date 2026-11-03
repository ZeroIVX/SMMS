using SMMS.Core.Profiles;

namespace SMMS.Desktop.ViewModels;

/// <summary>配置档列表项与启动区下拉共用。</summary>
public sealed class ProfileItemViewModel(ModProfile profile, string displayName, bool isActive)
{
    public ModProfile Profile { get; } = profile;

    public string Id => Profile.Id;

    public string DisplayName { get; } = displayName;

    public string PathText => Profile.ModsPath;

    public bool IsActive { get; } = isActive;

    public bool IsBuiltIn => Profile.IsBuiltIn;

    public bool IsFullInstance => Profile.Isolation.IsolateGameInstance;
}
