using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using SMMS.Core.Game;
using SMMS.Desktop.Localization;
using SMMS.Desktop.ViewModels;
using SMMS.Desktop.Views;
using SMMS.Infrastructure.DependencyInjection;
using SMMS.UseCases.DependencyInjection;
using SMMS.UseCases.Game;

namespace SMMS.Desktop;

public partial class App : Application
{
    private ServiceProvider? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = BuildServices();

            var settings = _services.GetRequiredService<GameInstallationService>();
            var localization = _services.GetRequiredService<ILocalizationService>();
            localization.Initialize(settings.Settings.Locale);

            var viewModel = _services.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = viewModel };

            // 仅供开发截图使用：SMMS_WINDOW_SIZE=1100x720 可指定启动窗口尺寸，
            // 用于验证三档响应式断点（>=1280 三栏 / 1024–1279 抽屉 / <1024 紧凑）。
            if (Environment.GetEnvironmentVariable("SMMS_WINDOW_SIZE") is { Length: > 0 } size
                && size.Split('x', 'X') is [var wText, var hText]
                && double.TryParse(wText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var width)
                && double.TryParse(hText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var height))
            {
                window.Width = width;
                window.Height = height;
            }

            desktop.MainWindow = window;
            desktop.Exit += (_, _) => Shutdown(viewModel);

#if DEBUG
            RunSelfTestIfRequested(viewModel);
#endif
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void Shutdown(MainWindowViewModel viewModel)
    {
        viewModel.Dispose();
        _services?.Dispose();
        _services = null;
        Log.CloseAndFlush();
    }

    /// <summary>
    /// 诊断入口，仅调试构建启用。
    /// <c>SMMS_START_PAGE</c> 指定启动后落在哪一页；
    /// <c>SMMS_SELFTEST=1</c> 额外触发一次配置档创建，用于验证命令链路。
    /// </summary>
    private void RunSelfTestIfRequested(MainWindowViewModel viewModel)
    {
        var startPage = Environment.GetEnvironmentVariable("SMMS_START_PAGE");
        if (!string.IsNullOrWhiteSpace(startPage))
        {
            viewModel.NavigateCommand.Execute(startPage);
            Log.Information("自检：启动页设置为 {Page}", startPage);
        }

        // 布局稳定后再渲染，否则截到空白。更新检查耗时更长，留足时间。
        var shotPath = Environment.GetEnvironmentVariable("SMMS_SCREENSHOT");
        if (!string.IsNullOrWhiteSpace(shotPath))
        {
            var delay = string.Equals(Environment.GetEnvironmentVariable("SMMS_UPDATE_DEMO"), "1", StringComparison.Ordinal)
                ? 10.0
                : 3.0;

            // 单页截图时页面数据可能还在异步加载，允许调用方自己放宽等待。
            if (double.TryParse(
                    Environment.GetEnvironmentVariable("SMMS_SCREENSHOT_DELAY"),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var customDelay) && customDelay > 0)
            {
                delay = customDelay;
            }

            DispatcherTimer.RunOnce(() => CaptureWindow(shotPath), TimeSpan.FromSeconds(delay));
        }

        if (string.Equals(Environment.GetEnvironmentVariable("SMMS_UPDATE_DEMO"), "1", StringComparison.Ordinal))
        {
            var modsPage = _services!.GetRequiredService<ModsPageViewModel>();
            TryExecuteCommand(modsPage, "CheckUpdatesCommand");
            Log.Information("自检：已触发更新检查");
        }

        if (string.Equals(Environment.GetEnvironmentVariable("SMMS_EXPORT_DEMO"), "1", StringComparison.Ordinal))
        {
            RunExportDemo();
        }

        if (string.Equals(Environment.GetEnvironmentVariable("SMMS_SELECT_FIRST"), "1", StringComparison.Ordinal))
        {
            var modsPage = _services!.GetRequiredService<ModsPageViewModel>();
            if (TrySelectFirstMod(modsPage, out var firstName))
            {
                Log.Information("自检：已选中模组「{Name}」", firstName);
            }
        }

        if (Environment.GetEnvironmentVariable("SMMS_DISCOVER_DEMO") is { Length: > 0 } keyword)
        {
            var discoverPage = _services!.GetRequiredService<DiscoverPageViewModel>();
            TrySetProperty(discoverPage, "SearchText", keyword);
            TryExecuteCommand(discoverPage, "RunQueryCommand");
        }

        if (string.Equals(Environment.GetEnvironmentVariable("SMMS_NOTE_TRANSLATE"), "1", StringComparison.Ordinal))
        {
            var modsPage = _services!.GetRequiredService<ModsPageViewModel>();
            TrySelectFirstMod(modsPage, out _);
            TryExecuteCommand(modsPage, "TranslateDescriptionCommand");
        }

        if (string.Equals(Environment.GetEnvironmentVariable("SMMS_CHECKING_DEMO"), "1", StringComparison.Ordinal))
        {
            // 模拟「检查更新中」状态，用于对比排序下拉的位置是否发生位移。
            _services!.GetRequiredService<ModsPageViewModel>().IsChecking = true;
        }

        if (string.Equals(Environment.GetEnvironmentVariable("SMMS_REFRESH_DEMO"), "1", StringComparison.Ordinal))
        {
            // 等价于点设置页的「自动检测」：应重新读盘而不是复用启动快照。
            _services!.GetRequiredService<SettingsPageViewModel>().RefreshCommand.Execute(null);
        }

        if (string.Equals(Environment.GetEnvironmentVariable("SMMS_SMAPI_DEMO"), "1", StringComparison.Ordinal))
        {
            TryExecuteCommand(_services!.GetRequiredService<SettingsPageViewModel>(), "CheckSmapiCommand");
        }

        if (string.Equals(Environment.GetEnvironmentVariable("SMMS_NETWORK_DEMO"), "1", StringComparison.Ordinal))
        {
            // 走 ViewModel 命令而不是直接调服务，这样能看到界面上的结果展示。
            TryExecuteCommand(_services!.GetRequiredService<SettingsPageViewModel>(), "ProbeNetworkCommand");
        }

        if (Environment.GetEnvironmentVariable("SMMS_VANITY_DEMO") is { Length: > 0 } archive)
        {
            var vanity = _services!.GetRequiredService<SMMS.UseCases.Vanity.VanityService>();
            var games = _services!.GetRequiredService<GameInstallationService>();
            var profiles = _services!.GetRequiredService<SMMS.UseCases.Profiles.ProfileService>();

            // 必须和页面用同一套解析逻辑：当前激活的配置档可能指向另一个游戏根。
            var activeProfile = profiles.GetActive();
            var game = activeProfile is null ? games.Current : games.ResolveForProfile(activeProfile);

            if (game is not null)
            {
                var result = vanity.Import(game, archive);
                Log.Information(
                    "自检导入美化包：成功={Success} 文件数={Count} 名称={Name} 错误={Error}",
                    result.Success, result.FileCount, result.Package?.Name, result.ErrorKey);

                // 自检绕过 ViewModel 直接调服务，需手动触发一次刷新才能看到列表。
                _services!.GetRequiredService<VanityPageViewModel>().Refresh();
            }
        }

        if (string.Equals(Environment.GetEnvironmentVariable("SMMS_PREFLIGHT_DEMO"), "1", StringComparison.Ordinal))
        {
            ShowPreflightDemo(viewModel);
        }

        if (!string.Equals(Environment.GetEnvironmentVariable("SMMS_SELFTEST"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var page = _services!.GetRequiredService<ProfilePageViewModel>();
        TrySetProperty(page, "NewProfileName", "selftest");
        TryExecuteCommand(page, "CreateCommand");
        Log.Information("自检：已触发配置档创建命令");
    }

    /// <summary>自检用：按名称反射取命令并执行，页面属性改名不至于让诊断入口编不过。</summary>
    private static void TryExecuteCommand(object target, string commandName)
    {
        if (target.GetType().GetProperty(commandName)?.GetValue(target) is System.Windows.Input.ICommand command
            && command.CanExecute(null))
        {
            command.Execute(null);
            return;
        }

        Log.Warning("自检：命令 {Command} 不存在或不可执行，已跳过", commandName);
    }

    /// <summary>自检用：按名称反射设置属性。</summary>
    private static void TrySetProperty(object target, string propertyName, object? value)
    {
        var property = target.GetType().GetProperty(propertyName);
        if (property is null || !property.CanWrite)
        {
            Log.Warning("自检：属性 {Property} 不可写，已跳过", propertyName);
            return;
        }

        property.SetValue(target, value);
    }

    /// <summary>自检用：把模块库的第一行选中，方便截图展示详情面板。</summary>
    private static bool TrySelectFirstMod(ModsPageViewModel modsPage, out string name)
    {
        name = string.Empty;

        if (modsPage.GetType().GetProperty("Mods")?.GetValue(modsPage) is not System.Collections.IEnumerable mods)
        {
            return false;
        }

        foreach (var mod in mods)
        {
            TrySetProperty(modsPage, "SelectedMod", mod);
            name = mod.GetType().GetProperty("Name")?.GetValue(mod)?.ToString() ?? string.Empty;
            return true;
        }

        return false;
    }

    /// <summary>诊断用：构造一份假的预检结果并打开对话框，用于验证渲染而无需真实触发错误。</summary>
    private static void ShowPreflightDemo(MainWindowViewModel viewModel)
    {
        var fake = new PreflightResult(
        [
            new PreflightIssue
            {
                Severity = PreflightSeverity.Error,
                Code = "preflight.smapiTooOld",
                Arguments = ["4.5.1", "Stardew Valley Expanded、Ridgeside Village"]
            },
            new PreflightIssue
            {
                Severity = PreflightSeverity.Warning,
                Code = "preflight.missingDependencies",
                Arguments = ["Ridgeside Village → Pathoschild.ContentPatcher"]
            },
            new PreflightIssue
            {
                Severity = PreflightSeverity.Error,
                Code = "preflight.gameRunning"
            }
        ]);

        var dialog = new PreflightDialog { DataContext = viewModel.CreatePreflightViewModel(fake) };
        dialog.Show();

        DispatcherTimer.RunOnce(() =>
        {
            var size = new PixelSize((int)Math.Round(dialog.Bounds.Width), (int)Math.Round(dialog.Bounds.Height));
            using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
            bitmap.Render(dialog);
#pragma warning disable CS0618
            bitmap.Save(Path.Combine(Path.GetTempPath(), "smms-preflight.png"));
#pragma warning restore CS0618
            Log.Information("自检：预检对话框截图完成 {Size}", size);
        }, TimeSpan.FromSeconds(2));
    }

    /// <summary>诊断用：导出当前配置档清单到固定路径，验证真实数据的打包链路。</summary>
    private void RunExportDemo()
    {
        var packs = _services!.GetRequiredService<SMMS.UseCases.Profiles.ProfilePackService>();
        var profile = _services!.GetRequiredService<SMMS.UseCases.Profiles.ProfileService>().GetActive();

        if (profile is null)
        {
            Log.Warning("自检导出：没有激活的配置档");
            return;
        }

        _ = Task.Run(async () =>
        {
            var target = Path.Combine(Path.GetTempPath(), "smms-selftest-pack.zip");
            var result = await packs.ExportAsync(profile, includeMods: false, target);
            Log.Information(
                "自检导出：成功={Success} 模组数={Count} 大小={Size} 错误={Error}",
                result.Success, result.ModCount, result.SizeBytes, result.ErrorKey);
        });
    }

    /// <summary>把主窗口渲染成 PNG，用于无人值守的界面诊断。</summary>
    private void CaptureWindow(string path)
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            Log.Warning("自检：主窗口不可用，跳过截图");
            return;
        }

        var width = (int)Math.Round(window.Bounds.Width);
        var height = (int)Math.Round(window.Bounds.Height);
        if (width <= 0 || height <= 0)
        {
            Log.Warning("自检：窗口尺寸无效 {Width}x{Height}", width, height);
            return;
        }

        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(window);
#pragma warning disable CS0618 // 诊断用截图，不需要编码器选项
        bitmap.Save(path);
#pragma warning restore CS0618
        Log.Information("自检：已截图 {Path} ({Width}x{Height})", path, width, height);

        foreach (var button in window.GetVisualDescendants().OfType<Button>())
        {
            Log.Information(
                "可视树按钮：Content=「{Content}」 Bounds={Bounds} Visible={Visible} Enabled={Enabled}",
                button.Content, button.Bounds, button.IsVisible, button.IsEffectivelyEnabled);
        }

        foreach (var checkBox in window.GetVisualDescendants().OfType<CheckBox>())
        {
            Log.Information(
                "可视树复选框：Content=「{Content}」 Bounds={Bounds} Visible={Visible}",
                checkBox.Content, checkBox.Bounds, checkBox.IsVisible);
        }

        foreach (var toggle in window.GetVisualDescendants().OfType<ToggleSwitch>())
        {
            Log.Information(
                "可视树开关：IsChecked={Checked} Bounds={Bounds} Visible={Visible}",
                toggle.IsChecked?.ToString() ?? "null", toggle.Bounds, toggle.IsVisible);
        }

        foreach (var combo in window.GetVisualDescendants().OfType<ComboBox>())
        {
            Log.Information(
                "可视树下拉：项数={Count} 选中={Selected} Bounds={Bounds}",
                combo.ItemCount, combo.SelectedItem?.ToString() ?? "null", combo.Bounds);
        }

        // 无人值守截图：拍完就退，避免残留进程挡下一次构建。
        if (string.Equals(Environment.GetEnvironmentVariable("SMMS_EXIT_AFTER_SHOT"), "1", StringComparison.Ordinal))
        {
            DispatcherTimer.RunOnce(
                () => (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown(),
                TimeSpan.FromMilliseconds(400));
        }
    }

    private static ServiceProvider BuildServices()
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SMMS");
        Directory.CreateDirectory(Path.Combine(dataDirectory, "logs"));

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(dataDirectory, "logs", "smms-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                formatProvider: System.Globalization.CultureInfo.InvariantCulture,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        var services = new ServiceCollection();

        services.AddLogging(builder => builder.AddSerilog(Log.Logger, dispose: true));
        services.AddSmmsInfrastructure();
        services.AddSmmsApplication();
        services.AddSingleton<ILocalizationService, JsonLocalizationService>();
        services.AddSingleton<LaunchpadPageViewModel>();
        services.AddSingleton<ModsPageViewModel>();
        services.AddSingleton<DiscoverPageViewModel>();
        services.AddSingleton<VanityPageViewModel>();
        services.AddSingleton<DiagnosticsPageViewModel>();
        services.AddSingleton<UpdatesPageViewModel>();
        services.AddSingleton<SettingsPageViewModel>();
        services.AddSingleton<ProfilePageViewModel>();
        services.AddSingleton<BackupsPageViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }
}
