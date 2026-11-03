using System.Globalization;
using Avalonia.Media;
using SMMS.Core.Game;

namespace SMMS.Desktop.ViewModels;

public sealed class PreflightIssueViewModel(PreflightIssue issue, Func<string, string> translate)
{
    public string Text { get; } = Format(issue, translate);

    public IBrush Accent { get; } = issue.Severity == PreflightSeverity.Error
        ? new SolidColorBrush(Color.Parse("#C0392B"))
        : new SolidColorBrush(Color.Parse("#C58A1D"));

    private static string Format(PreflightIssue issue, Func<string, string> translate)
    {
        var template = translate(issue.Code);
        if (issue.Arguments.Count == 0)
        {
            return template;
        }

        var arguments = issue.Arguments.Cast<object>().ToArray();
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, arguments);
        }
        catch (FormatException)
        {
            // 占位符与参数不匹配时退回原始模板，不让界面因文案问题崩掉。
            return template;
        }
    }
}

public sealed class PreflightDialogViewModel
{
    public PreflightDialogViewModel(PreflightResult result, Func<string, string> translate)
    {
        Title = translate("preflight.dialogTitle");
        Subtitle = translate(result.HasErrors
            ? "preflight.dialogSubtitleError"
            : "preflight.dialogSubtitleWarning");
        CancelText = translate("preflight.cancel");
        ContinueText = translate("preflight.continue");

        Issues = result.Issues
            .Select(issue => new PreflightIssueViewModel(issue, translate))
            .ToList();
    }

    public string Title { get; }

    public string Subtitle { get; }

    public string CancelText { get; }

    public string ContinueText { get; }

    public IReadOnlyList<PreflightIssueViewModel> Issues { get; }
}
