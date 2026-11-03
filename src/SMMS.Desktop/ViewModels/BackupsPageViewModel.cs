using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SMMS.Core.Profiles;
using SMMS.Core.Snapshots;
using SMMS.Desktop.Localization;
using SMMS.UseCases.Profiles;
using SMMS.UseCases.Snapshots;

namespace SMMS.Desktop.ViewModels;

public sealed class SnapshotItemViewModel(SnapshotRecord record, string timeText, string metaText, string labelText)
{
    public SnapshotRecord Record { get; } = record;

    public string TimeText { get; } = timeText;

    public string MetaText { get; } = metaText;

    public string LabelText { get; } = labelText;

    public bool IsAutomatic => Record.IsAutomatic;
}

public partial class BackupsPageViewModel : PageViewModelBase
{
    private readonly SnapshotService _snapshots;
    private readonly ProfileService _profiles;
    private readonly ILogger<BackupsPageViewModel> _logger;

    private ModProfile? _activeProfile;
    private string? _errorKey;

    public BackupsPageViewModel(
        SnapshotService snapshots,
        ProfileService profiles,
        ILocalizationService localization,
        ILogger<BackupsPageViewModel> logger)
        : base(localization)
    {
        _snapshots = snapshots;
        _profiles = profiles;
        _logger = logger;

        _profiles.ProfilesChanged += OnProfilesChanged;
        Refresh();
    }

    public ObservableCollection<SnapshotItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial string ProfileHeader { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial string ErrorText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasStatus { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsEmpty { get; set; } = true;

    /// <summary>「共 N 个备份，占用 X」。快照是整目录复制，不看着点很容易堆起来。</summary>
    [ObservableProperty]
    public partial string SummaryText { get; set; } = string.Empty;

    public Func<ConfirmDialogViewModel, Task<bool>>? ConfirmRequested { get; set; }

    /// <summary>恢复或删除后需要让模组页重新扫描。</summary>
    public event EventHandler? LibraryChanged;

    [RelayCommand]
    private void Create()
    {
        if (IsBusy)
        {
            return;
        }

        var profile = _activeProfile;
        if (profile is null)
        {
            ShowError("snapshots.error.noProfile");
            return;
        }

        IsBusy = true;

        try
        {
            var result = _snapshots.Create(profile, L("snapshots.manualLabel"));

            if (!result.Success)
            {
                ShowError(result.ErrorKey ?? "snapshots.error.createFailed");
                return;
            }

            ShowStatus(L("snapshots.created"));
            Refresh();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RestoreAsync(SnapshotItemViewModel? item)
    {
        if (item is null || IsBusy || _activeProfile is null || ConfirmRequested is null)
        {
            return;
        }

        var confirmed = await ConfirmRequested(new ConfirmDialogViewModel(
            L("snapshots.restoreTitle"),
            string.Format(CultureInfo.CurrentCulture, L("snapshots.restoreMessage"), item.TimeText),
            L("snapshots.restore"),
            L("common.cancel"))).ConfigureAwait(true);

        if (!confirmed)
        {
            return;
        }

        IsBusy = true;

        try
        {
            var result = _snapshots.Restore(_activeProfile, item.Record.Id);

            if (!result.Success)
            {
                ShowError(result.ErrorKey ?? "snapshots.error.restoreFailed");
                return;
            }

            _profiles.SyncManifest(_activeProfile);
            ShowStatus(L("snapshots.restored"));
            Refresh();
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(SnapshotItemViewModel? item)
    {
        if (item is null || IsBusy || _activeProfile is null || ConfirmRequested is null)
        {
            return;
        }

        var confirmed = await ConfirmRequested(new ConfirmDialogViewModel(
            L("snapshots.deleteTitle"),
            string.Format(CultureInfo.CurrentCulture, L("snapshots.deleteMessage"), item.TimeText),
            L("snapshots.delete"),
            L("common.cancel"))).ConfigureAwait(true);

        if (!confirmed)
        {
            return;
        }

        var result = _snapshots.Delete(_activeProfile.Id, item.Record.Id);

        if (!result.Success)
        {
            ShowError(result.ErrorKey ?? "snapshots.error.deleteFailed");
            return;
        }

        Refresh();
    }

    public override void RefreshTexts()
    {
        Refresh();

        if (_errorKey is { } key)
        {
            ErrorText = L(key);
        }
    }

    public void Refresh()
    {
        _activeProfile = _profiles.GetActive();

        ProfileHeader = _activeProfile is null
            ? L("snapshots.error.noProfile")
            : string.Format(
                CultureInfo.CurrentCulture,
                L("snapshots.forProfile"),
                string.IsNullOrWhiteSpace(_activeProfile.Name) ? L("profiles.default") : _activeProfile.Name);

        Items.Clear();

        if (_activeProfile is not null)
        {
            foreach (var record in _snapshots.List(_activeProfile.Id))
            {
                Items.Add(new SnapshotItemViewModel(
                    record,
                    record.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture),
                    string.Format(CultureInfo.CurrentCulture, L("snapshots.meta"), record.ModCount, FormatSize(record.SizeBytes)),
                    string.IsNullOrWhiteSpace(record.Label) ? L("snapshots.unlabeled") : record.Label));
            }
        }

        IsEmpty = Items.Count == 0;

        SummaryText = Items.Count == 0
            ? string.Empty
            : string.Format(
                CultureInfo.CurrentCulture,
                L("snapshots.summary"),
                Items.Count,
                FormatSize(Items.Sum(item => item.Record.SizeBytes)));
    }

    private static string FormatSize(long bytes)
    {
        const double megabyte = 1024 * 1024;

        return bytes >= megabyte
            ? $"{bytes / megabyte:0.#} MB"
            : $"{Math.Max(1, bytes / 1024)} KB";
    }

    private void OnProfilesChanged(object? sender, EventArgs e) => Refresh();

    private void ShowError(string key)
    {
        _errorKey = key;
        ErrorText = L(key);
        HasError = true;
        HasStatus = false;
    }

    private void ShowStatus(string text)
    {
        StatusText = text;
        HasStatus = true;
        HasError = false;
        _errorKey = null;
    }

    public override void Dispose()
    {
        _profiles.ProfilesChanged -= OnProfilesChanged;
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
