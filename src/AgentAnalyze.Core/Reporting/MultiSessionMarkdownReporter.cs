using System.Globalization;
using System.Text;
using AgentAnalyze.Core.Domain;

namespace AgentAnalyze.Core.Reporting;

/// <summary>
/// Renders a one-page summary of the most recent N sessions, with each row
/// linking to its full deep-dive report.
/// </summary>
public static class MultiSessionMarkdownReporter
{
    /// <summary>
    /// Returns the canonical file name for a multi-session summary.
    /// </summary>
    public static string FileNameFor(MultiSessionSummary summary)
        => $"summary-{summary.GeneratedAt.ToUniversalTime():yyyyMMdd-HHmmss}-last{summary.Sessions.Count}.md";

    /// <summary>
    /// Renders the summary as Markdown text.
    /// </summary>
    public static string Render(MultiSessionSummary summary)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Agent session summary — last {summary.Sessions.Count}");
        sb.AppendLine();
        sb.AppendLine($"_Generated {summary.GeneratedAt:yyyy-MM-dd HH:mm:ss} UTC. " +
                      $"Telemetry scan: {(summary.IncludedTelemetry ? $"on ({summary.ScannedLogs} log file(s) scanned)" : "off")}._");
        sb.AppendLine();

        // Disclosure of how the keeper set was assembled — important when min-turns
        // hides sessions, or when fewer than requested were available.
        var disclosure = BuildDisclosure(summary);
        if (disclosure != null)
        {
            sb.AppendLine($"_{disclosure}_");
            sb.AppendLine();
        }

        if (summary.Sessions.Count == 0)
        {
            sb.AppendLine("_No sessions found._");
            return sb.ToString();
        }

        // Headline table — one row per session
        sb.AppendLine("## Sessions");
        sb.AppendLine();
        sb.AppendLine("| # | Started (UTC) | Project | Turns | Tool calls | Fresh in | Output | bash | dotnet | dotnet % | Top agent tool | Top external | Session |");
        sb.AppendLine("|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---|---|---|");
        for (int i = 0; i < summary.Sessions.Count; i++)
        {
            var s = summary.Sessions[i];
            var dotnetPct = s.DotNetSharePct.HasValue ? $"{s.DotNetSharePct.Value:0.0}%" : "—";
            var freshIn = s.FreshInputTokens.HasValue ? Fmt(s.FreshInputTokens.Value) : "—";
            var outTok = s.OutputTokens.HasValue ? Fmt(s.OutputTokens.Value) : "—";
            var topAgent = s.TopAgentTool != null ? $"`{s.TopAgentTool}` ({Fmt(s.TopAgentToolTokens)})" : "—";
            var topExt = s.TopExternalCommand != null && s.TopExternalCommandTokens > 0
                ? $"`{s.TopExternalCommand}` ({Fmt(s.TopExternalCommandTokens)})"
                : "—";
            var idShort = s.SessionId.Length >= 8 ? s.SessionId[..8] : s.SessionId;
            var reportFile = DeepDiveFileName(s.SessionId, s.StartedAt);
            sb.Append("| ").Append(i + 1).Append(" | ");
            sb.Append(s.StartedAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(" | ");
            sb.Append(s.CwdBasename ?? "—").Append(" | ");
            sb.Append(s.Turns).Append(" | ");
            sb.Append(s.ToolCalls).Append(" | ");
            sb.Append(freshIn).Append(" | ");
            sb.Append(outTok).Append(" | ");
            sb.Append(Fmt(s.BashTokens)).Append(" | ");
            sb.Append(Fmt(s.DotNetTokens)).Append(" | ");
            sb.Append(dotnetPct).Append(" | ");
            sb.Append(topAgent).Append(" | ");
            sb.Append(topExt).Append(" | ");
            sb.Append($"[`{idShort}`]({reportFile}) |");
            sb.AppendLine();
        }
        sb.AppendLine();

        // Per-session details (compact)
        sb.AppendLine("## Per-session details");
        sb.AppendLine();
        for (int i = 0; i < summary.Sessions.Count; i++)
        {
            var s = summary.Sessions[i];
            var idShort = s.SessionId.Length >= 8 ? s.SessionId[..8] : s.SessionId;
            sb.AppendLine($"### {i + 1}. `{idShort}` — {s.StartedAt:yyyy-MM-dd HH:mm} UTC" +
                          (s.CwdBasename is { } cw ? $" · {cw}" : ""));
            sb.AppendLine();
            sb.Append("- Session ID: `").Append(s.SessionId).Append("`");
            sb.AppendLine();
            if (s.Duration.HasValue)
                sb.AppendLine($"- Duration: {FormatDuration(s.Duration.Value)}");
            if (!string.IsNullOrEmpty(s.CopilotVersion))
                sb.AppendLine($"- Copilot {s.CopilotVersion}");
            if (s.Models.Count > 0)
                sb.AppendLine($"- Model(s): {string.Join(", ", s.Models)}");
            sb.AppendLine($"- Turns: {s.Turns} · tool calls: {s.ToolCalls}");
            if (s.FreshInputTokens.HasValue)
                sb.AppendLine($"- Fresh input tokens (auth): {Fmt(s.FreshInputTokens.Value)}" +
                              (s.OutputTokens.HasValue ? $" · output: {Fmt(s.OutputTokens.Value)}" : ""));
            else if (summary.IncludedTelemetry)
                sb.AppendLine($"- Telemetry: not matched ({s.ProcessLogCoverage.MatchedTurns}/{s.ProcessLogCoverage.TotalTurns} turns)");
            else
                sb.AppendLine("- Telemetry: not collected");
            if (s.BashTokens > 0)
            {
                sb.Append($"- bash: {Fmt(s.BashTokens)} est. tokens");
                if (s.DotNetTokens > 0)
                    sb.Append($" · dotnet share: {Fmt(s.DotNetTokens)} ({s.DotNetSharePct:0.0}%)");
                sb.AppendLine();
            }
            if (s.TopAgentTool != null)
                sb.AppendLine($"- Top agent tool: `{s.TopAgentTool}` ({Fmt(s.TopAgentToolTokens)} est. tokens)");
            if (s.TopExternalCommand != null && s.TopExternalCommandTokens > 0)
                sb.AppendLine($"- Top external command: `{s.TopExternalCommand}` ({Fmt(s.TopExternalCommandTokens)} est. tokens)");
            var reportFile = DeepDiveFileName(s.SessionId, s.StartedAt);
            sb.AppendLine($"- Deep-dive: [{reportFile}]({reportFile})");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"_Generated by `dotnet-agent-analyze` v{MarkdownReporter.ToolVersion}._");
        return sb.ToString();
    }

    private static string Fmt(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    private static string? BuildDisclosure(MultiSessionSummary s)
    {
        // Only emit when something interesting happened: filter active and skipped
        // anything, parse errors, or returned fewer sessions than requested.
        bool filteredAny = s.SkippedBelowMinTurns > 0;
        bool errors = s.SkippedDueToReadError > 0;
        bool short_ = s.Sessions.Count < s.RequestedCount;
        if (!filteredAny && !errors && !short_) return null;

        var parts = new List<string>(4)
        {
            $"Examined {s.ExaminedCandidates} session dir(s); included {s.Sessions.Count} of {s.RequestedCount} requested",
        };
        if (filteredAny)
        {
            var note = s.MinTurns == 1
                ? " (older session formats lack `turnId` and parse as 0 turns)"
                : "";
            parts.Add($"{s.SkippedBelowMinTurns} skipped by `--min-turns={s.MinTurns}`{note}");
        }
        if (errors)
            parts.Add($"{s.SkippedDueToReadError} failed to parse");
        return string.Join("; ", parts) + ".";
    }

    private static string FormatDuration(TimeSpan d)
    {
        if (d.TotalHours >= 1) return $"{(int)d.TotalHours}h {d.Minutes}m";
        if (d.TotalMinutes >= 1) return $"{(int)d.TotalMinutes}m {d.Seconds}s";
        return $"{(int)d.TotalSeconds}s";
    }

    /// <summary>
    /// Returns the report file name a deep-dive run would produce for a given session id
    /// + start time. Mirrors <see cref="MarkdownReporter.FileNameFor"/>.
    /// </summary>
    private static string DeepDiveFileName(string sessionId, DateTime startedAt)
    {
        var ts = startedAt.ToUniversalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var idShort = sessionId.Length >= 8 ? sessionId[..8] : sessionId;
        return $"report-{ts}-{idShort}.md";
    }
}
