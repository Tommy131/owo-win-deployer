using System.Text;
using WinDeploy.Core.I18n;

namespace WinDeploy.Core.Engine;

/// <summary>Renders a finished <see cref="RunSummary"/> as a self-contained HTML report — per-item status,
/// duration and message plus an overall ok/failed/skipped tally — so a deployment leaves an auditable record
/// (mirrors the inventory export's styling).</summary>
public static class DeployReport
{
    public static string ToHtml(RunSummary summary, string machine, DateTime when)
    {
        var rows = summary.Results;
        var sb = new StringBuilder();
        var lang = Localizer.Current;
        sb.AppendLine($"<!doctype html><html lang=\"{lang}\"><head><meta charset=\"utf-8\">");
        sb.AppendLine($"<title>{H(Localizer.T("engine.report.title"))}</title><style>");
        sb.AppendLine("body{font-family:Segoe UI,system-ui,sans-serif;margin:32px;color:#1b1b1a}");
        sb.AppendLine("h1{font-size:20px;margin-bottom:2px}.sub{color:#6b6b66;font-size:13px;margin-bottom:18px}");
        sb.AppendLine("table{border-collapse:collapse;width:100%;font-size:13px}");
        sb.AppendLine("th,td{border:1px solid #e6e6e1;padding:7px 10px;text-align:left;vertical-align:top}");
        sb.AppendLine("th{background:#f5f5f2}tr:nth-child(even){background:#fafaf8}");
        sb.AppendLine(".ok{color:#1a7f37;font-weight:600}.failed{color:#cf222e;font-weight:600}.skip{color:#9a6700}");
        sb.AppendLine("</style></head><body>");
        sb.AppendLine($"<h1>{H(Localizer.T("engine.report.title"))}</h1>");
        sb.AppendLine($"<div class=\"sub\">{H(Localizer.Format("engine.report.heading", machine, when.ToString("yyyy-MM-dd HH:mm:ss")))}"
                      + $" &middot; {H(Localizer.Format("engine.report.summary", summary.Ok, summary.Failed, summary.Skipped))}</div>");
        sb.AppendLine($"<table><tr><th>{H(Localizer.T("engine.report.colName"))}</th><th>{H(Localizer.T("engine.report.colId"))}</th>"
                      + $"<th>{H(Localizer.T("engine.report.colMethod"))}</th><th>{H(Localizer.T("engine.report.colStatus"))}</th>"
                      + $"<th>{H(Localizer.T("engine.report.colDuration"))}</th><th>{H(Localizer.T("engine.report.colMessage"))}</th></tr>");
        foreach (var r in rows)
        {
            var (cls, label) = r.Status switch
            {
                StepStatus.Ok => ("ok", Localizer.T("engine.report.statusOk")),
                StepStatus.Failed => ("failed", Localizer.T("engine.report.statusFailed")),
                _ => ("skip", Localizer.T("engine.report.statusSkipped")),
            };
            sb.AppendLine($"<tr><td>{H(r.Item.Name)}</td><td>{H(r.Item.Id)}</td><td>{H(r.Item.Install.Method)}</td>"
                          + $"<td class=\"{cls}\">{H(label)}</td><td>{Dur(r.Duration)}</td><td>{H(r.Message)}</td></tr>");
        }
        sb.AppendLine("</table></body></html>");
        return sb.ToString();
    }

    private static string Dur(TimeSpan t) => t.TotalSeconds >= 1 ? $"{t.TotalSeconds:0.0}s" : t.TotalMilliseconds >= 1 ? $"{t.TotalMilliseconds:0}ms" : "";
    private static string H(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
}
