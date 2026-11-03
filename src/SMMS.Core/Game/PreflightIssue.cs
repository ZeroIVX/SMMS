namespace SMMS.Core.Game;

public enum PreflightSeverity
{
    Info = 0,
    Warning,
    Error
}

/// <summary>
/// 启动前检查发现的一处问题。<see cref="Code"/> 是本地化键，<see cref="Arguments"/> 用于格式化，
/// 用例层不拼文案，保证双语一致。
/// </summary>
public sealed record PreflightIssue
{
    public required PreflightSeverity Severity { get; init; }

    public required string Code { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];
}

public sealed record PreflightResult(IReadOnlyList<PreflightIssue> Issues)
{
    public static PreflightResult Clean { get; } = new([]);

    public bool HasErrors => Issues.Any(issue => issue.Severity == PreflightSeverity.Error);

    public bool HasWarnings => Issues.Any(issue => issue.Severity == PreflightSeverity.Warning);

    /// <summary>有问题就该弹窗，哪怕是警告——玩家有权在启动前知道将要面对什么。</summary>
    public bool NeedsConfirmation => Issues.Count > 0;
}
