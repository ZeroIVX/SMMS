using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SMMS.Core.Game;
using SMMS.Core.Ports;
using SMMS.Core.Profiles;

namespace SMMS.UseCases.Game;

public enum GameLaunchState
{
    Idle = 0,
    Launching,
    Running
}

/// <summary>启动结果。失败时携带本地化键，由界面负责翻译，避免在用例层拼文案。</summary>
public sealed record GameLaunchResult(bool Success, string? ErrorKey)
{
    public static GameLaunchResult Ok() => new(true, null);

    public static GameLaunchResult Fail(string errorKey) => new(false, errorKey);
}

/// <summary>
/// 启动游戏并跟踪进程状态。
/// </summary>
/// <remarks>
/// 只跟踪由本进程启动的游戏。玩家自行从 Steam 或桌面启动的实例不可见，
/// 这是有意为之——扫描系统进程既慢又容易误判，代价大于收益。
/// </remarks>
public sealed class GameLaunchService : IDisposable
{
    private readonly IGameLauncher _launcher;
    private readonly ILogger<GameLaunchService> _logger;
    private Process? _process;

    public GameLaunchService(IGameLauncher launcher, ILogger<GameLaunchService> logger)
    {
        _launcher = launcher;
        _logger = logger;
    }

    public GameLaunchState State { get; private set; } = GameLaunchState.Idle;

    /// <summary>状态变化可能在后台线程触发，订阅方需自行切回界面线程。</summary>
    public event EventHandler? StateChanged;

    public GameLaunchResult Launch(GameInstallation? game, ModProfile profile, LaunchMode mode)
    {
        if (game is null)
        {
            return GameLaunchResult.Fail("launch.error.noGame");
        }

        if (State == GameLaunchState.Running)
        {
            return GameLaunchResult.Fail("launch.error.alreadyRunning");
        }

        if (mode == LaunchMode.Smapi && !game.IsSmapiInstalled)
        {
            return GameLaunchResult.Fail("launch.error.noSmapi");
        }

        LaunchPlan plan;
        try
        {
            plan = _launcher.BuildLaunchPlan(game, profile, mode);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "构造启动计划失败");
            return GameLaunchResult.Fail("launch.error.noSmapi");
        }

        SetState(GameLaunchState.Launching);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = plan.ExecutablePath,
                WorkingDirectory = plan.WorkingDirectory,
                UseShellExecute = false
            };

            foreach (var argument in plan.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            foreach (var (key, value) in plan.EnvironmentVariables)
            {
                startInfo.Environment[key] = value;
            }

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Exited += OnProcessExited;

            if (!process.Start())
            {
                process.Exited -= OnProcessExited;
                process.Dispose();
                SetState(GameLaunchState.Idle);
                return GameLaunchResult.Fail("launch.error.failed");
            }

            _process = process;
            SetState(GameLaunchState.Running);

            _logger.LogInformation(
                "已以 {Mode} 方式启动游戏（PID {Pid}）：{Exe}",
                mode, process.Id, plan.ExecutablePath);

            return GameLaunchResult.Ok();
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            _logger.LogError(ex, "启动游戏失败：{Exe}", plan.ExecutablePath);
            SetState(GameLaunchState.Idle);
            return GameLaunchResult.Fail("launch.error.failed");
        }
    }

    /// <summary>
    /// 检查游戏是否在运行。不只看本进程启动的实例——玩家从 Steam 或桌面启动的也要算，
    /// 否则会出现「SMMS 觉得空闲、实际已经开着」的重复启动。
    /// </summary>
    public bool IsGameProcessRunning()
    {
        if (State == GameLaunchState.Running)
        {
            return true;
        }

        try
        {
            return Process.GetProcessesByName("Stardew Valley").Length > 0
                   || Process.GetProcessesByName("StardewModdingAPI").Length > 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogDebug(ex, "查询游戏进程失败，回退到本地状态判断");
            return State == GameLaunchState.Running;
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (sender is Process process)
        {
            process.Exited -= OnProcessExited;
            process.Dispose();
        }

        _process = null;
        _logger.LogInformation("游戏进程已退出");

        // 已退出说明这次启动到游戏真正跑起来之间可能失败，状态回落到空闲即可，
        // 具体失败原因由 SMAPI 控制台或游戏日志给出。
        SetState(GameLaunchState.Idle);
    }

    private void SetState(GameLaunchState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_process is { } process)
        {
            process.Exited -= OnProcessExited;
            process.Dispose();
            _process = null;
        }

        GC.SuppressFinalize(this);
    }
}
