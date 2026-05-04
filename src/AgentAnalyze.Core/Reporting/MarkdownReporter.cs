using System.Globalization;
using System.Text;
using AgentAnalyze.Core.Analysis;
using AgentAnalyze.Core.Domain;

namespace AgentAnalyze.Core.Reporting;

/// <summary>
/// Renders a privacy-conscious Markdown report for a single session.
/// </summary>
public static class MarkdownReporter
{
    /// <summary>The version stamped in the report footer.</summary>
    public const string ToolVersion = "0.1.0";

    /// <summary>
    /// Returns the standard report file name for a given session
    /// (e.g. <c>report-20260503-141103-48e13b3e.md</c>).
    /// </summary>
    public static string FileNameFor(SessionAnalysis analysis)
    {
        var ts = analysis.Metadata.StartedAt.ToUniversalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var idShort = analysis.Metadata.SessionId.Length >= 8
            ? analysis.Metadata.SessionId[..8]
            : analysis.Metadata.SessionId;
        return $"report-{ts}-{idShort}.md";
    }

    /// <summary>
    /// Renders the report as Markdown text.
    /// </summary>
    public static string Render(SessionAnalysis analysis, AttributionResult attribution)
    {
        var sb = new StringBuilder();
        var meta = analysis.Metadata;

        sb.AppendLine("# Agent session token-attribution report");
        sb.AppendLine();
        sb.AppendLine("## Session");
        sb.AppendLine();
        sb.AppendLine($"- **Session ID**: `{meta.SessionId}`");
        sb.AppendLine($"- **Started (UTC)**: {meta.StartedAt.ToUniversalTime():yyyy-MM-dd HH:mm:ss} Z");
        sb.AppendLine($"- **Started (local)**: {meta.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"- **Agent**: GitHub Copilot CLI" +
                      (meta.CopilotVersion is { } v ? $" {v}" : ""));
        if (meta.Models.Count > 0)
            sb.AppendLine($"- **Model(s)**: {string.Join(", ", meta.Models)}");
        if (!string.IsNullOrEmpty(meta.OsUser))
            sb.AppendLine($"- **OS user**: {meta.OsUser}");
        if (!string.IsNullOrEmpty(meta.Cwd))
            sb.AppendLine($"- **Working directory**: `{meta.Cwd}`");
        sb.AppendLine();

        // Totals
        sb.AppendLine("## Totals");
        sb.AppendLine();
        sb.AppendLine($"- **Top-level turns**: {analysis.Turns.Count}");
        var distinctTools = analysis.Turns.SelectMany(t => t.ToolCalls).Select(c => c.ToolName).Distinct(StringComparer.Ordinal).Count();
        var totalToolCalls = analysis.Turns.Sum(t => t.ToolCalls.Count);
        sb.AppendLine($"- **Tool calls (top-level)**: {totalToolCalls}");
        sb.AppendLine($"- **Distinct tools observed**: {distinctTools}");
        if (analysis.SubAgents.Count > 0)
        {
            sb.AppendLine($"- **Sub-agent runs**: {analysis.SubAgents.Count} " +
                $"({analysis.SubAgents.Sum(s => s.NestedToolCalls)} nested tool calls)");
        }

        var coverage = analysis.ProcessLogCoverage;
        if (attribution.AuthoritativeInputTokens.HasValue)
        {
            sb.AppendLine();
            sb.AppendLine("### Authoritative token totals (from `assistant_usage` telemetry)");
            sb.AppendLine();
            var input = attribution.AuthoritativeInputTokens.Value;
            var cacheRead = attribution.AuthoritativeCacheReadTokens ?? 0;
            var freshInput = input - cacheRead;
            sb.AppendLine($"- **Input tokens (sum across all turns)**: {Fmt(input)}");
            sb.AppendLine($"- **Fresh input tokens (input − cache reads)**: {Fmt(freshInput)}  ← the real per-session ingest cost");
            if (attribution.AuthoritativeOutputTokens.HasValue)
                sb.AppendLine($"- **Output tokens**: {Fmt(attribution.AuthoritativeOutputTokens.Value)}");
            if (attribution.AuthoritativeCacheReadTokens.HasValue)
                sb.AppendLine($"- **Cache-read tokens**: {Fmt(attribution.AuthoritativeCacheReadTokens.Value)}");
            if (attribution.AuthoritativeCacheWriteTokens.HasValue)
                sb.AppendLine($"- **Cache-write tokens**: {Fmt(attribution.AuthoritativeCacheWriteTokens.Value)}");
            sb.AppendLine($"- **Coverage**: {coverage.MatchedTurns}/{coverage.TotalTurns} turns matched across {coverage.LogsScanned.Count} log file(s)");

            // Cache efficiency (only meaningful when we have authoritative numbers)
            if (input > 0)
            {
                double overallHitRate = (double)cacheRead / input;
                sb.AppendLine();
                sb.AppendLine($"- **Overall cache hit rate**: {overallHitRate * 100:0.0}% of input tokens were served from cache");
            }
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine($"- **Output tokens (sum from events.jsonl)**: {Fmt(attribution.SumOutputTokensFromEvents)}");
            sb.AppendLine($"- **Authoritative input-token telemetry**: not available " +
                          $"(scanned {coverage.LogsScanned.Count} log file(s); matched {coverage.MatchedTurns}/{coverage.TotalTurns} turns)");
        }

        sb.AppendLine();
        sb.AppendLine("> All other token figures below are **estimated** from character counts " +
                      $"using a rough chars/token ratio of {TokenEstimate.CharsPerToken:0.#}. They are intended for ranking, not absolute accounting.");
        sb.AppendLine();

        // Headline
        sb.AppendLine("## Headline");
        sb.AppendLine();
        if (attribution.CumulativeToolRanking.Count > 0)
        {
            var top = attribution.CumulativeToolRanking[0];
            sb.AppendLine($"- **Top agent built-in tool (cumulative)**: `{top.ToolName}` — ~{Fmt(top.Tokens)} tokens across {top.Calls} call(s) (~{Fmt(top.Chars)} chars).");
        }
        if (attribution.ToolSingleTurnPeaks.Count > 0)
        {
            var peak = attribution.ToolSingleTurnPeaks[0];
            sb.AppendLine($"- **Top agent built-in tool in any single turn**: `{peak.ToolName}` — ~{Fmt(peak.Tokens)} tokens in turn `{peak.PeakTurnId}` (~{Fmt(peak.Chars)} chars).");
        }

        // External-command share — the headline that actually answers "is dotnet wasteful?"
        if (attribution.TotalBashChars > 0)
        {
            var totalBashTokens = TokenEstimate.FromChars(attribution.TotalBashChars);
            var dotnetShareTokens = TokenEstimate.FromChars(attribution.DotNetParentBashChars);
            var dotnetPct = (double)attribution.DotNetParentBashChars / attribution.TotalBashChars * 100;
            sb.AppendLine($"- **External commands (via `bash`)**: ~{Fmt(totalBashTokens)} tokens cumulative.");
            if (attribution.DotNetParentBashChars > 0)
            {
                sb.AppendLine($"- **`dotnet` share of `bash`**: ~{Fmt(dotnetShareTokens)} tokens ({dotnetPct:0.0}% of bash output).");
            }
        }

        sb.AppendLine($"- **User-provided text — single-turn peak**: ~{Fmt(attribution.PeakUserTurnTokensEstimated)} tokens" +
            (attribution.PeakUserTurnId is { } pu ? $" in turn `{pu}`" : "") +
            $" (~{Fmt(attribution.PeakUserTurnChars)} chars).");
        sb.AppendLine($"- **User-provided text — cumulative**: ~{Fmt(attribution.CumulativeUserTokensEstimated)} tokens (~{Fmt(attribution.CumulativeUserChars)} chars).");
        sb.AppendLine();

        // External commands via bash — the section the user actually wants to compare
        sb.AppendLine("## External commands run via `bash`");
        sb.AppendLine();
        sb.AppendLine("> The agent's built-in tools (`view`, `create`, `edit`, `bash`, `task`, ...) are agent scaffolding. " +
                      "The interesting question — \"which developer tool wastes the most tokens?\" — lives inside `bash`. " +
                      "These tables break the `bash` bucket down by what was actually being run.");
        sb.AppendLine();
        sb.AppendLine("### By family");
        sb.AppendLine();
        if (attribution.BashFamilyRanking.Count == 0)
        {
            sb.AppendLine("_No bash invocations._");
        }
        else
        {
            WriteTable(sb,
                ["Rank", "Family", "Calls", "Est. tokens", "Chars", "% of bash"],
                attribution.BashFamilyRanking.Select((r, i) =>
                {
                    double pct = attribution.TotalBashChars > 0 ? (double)r.Chars / attribution.TotalBashChars * 100 : 0;
                    return new[] { (i + 1).ToString(), FamilyLabel(r.Family),
                        r.Calls.ToString(CultureInfo.InvariantCulture),
                        Fmt(r.Tokens), Fmt(r.Chars), $"{pct:0.0}%" };
                }));
        }
        sb.AppendLine();

        sb.AppendLine("### Top 12 primary commands");
        sb.AppendLine();
        if (attribution.BashPrimaryRanking.Count == 0)
        {
            sb.AppendLine("_No bash invocations._");
        }
        else
        {
            WriteTable(sb,
                ["Rank", "Command", "Calls", "Est. tokens", "Chars"],
                attribution.BashPrimaryRanking.Take(12).Select((r, i) => new[] {
                    (i + 1).ToString(), Code(r.PrimaryCommand),
                    r.Calls.ToString(CultureInfo.InvariantCulture),
                    Fmt(r.Tokens), Fmt(r.Chars) }));
        }
        sb.AppendLine();

        // Top dotnet subcommands by tokens
        sb.AppendLine("## `dotnet` subcommands — by tokens");
        sb.AppendLine();
        if (attribution.DotNetCommandRanking.Count == 0)
        {
            sb.AppendLine("_None observed._");
        }
        else
        {
            WriteTable(sb,
                ["Rank", "dotnet subcommand", "Invocations", "Est. tokens", "Chars"],
                attribution.DotNetCommandRanking.Take(12).Select((r, i) => new[] {
                    (i + 1).ToString(), Code(r.Command),
                    r.Count.ToString(CultureInfo.InvariantCulture),
                    Fmt(r.Tokens), Fmt(r.Chars) }));
        }
        sb.AppendLine();

        // dotnet output repetition (constant overhead)
        WriteDotNetRepetitionSection(sb, analysis.DotNetOutputAnalysis);

        // Top tools — agent built-ins
        sb.AppendLine("## Agent built-in tools — top 6, single-turn peak");
        sb.AppendLine();
        WriteTable(sb,
            ["Rank", "Tool", "Peak turn", "Est. tokens", "Chars"],
            attribution.ToolSingleTurnPeaks
                .Take(6)
                .Select((p, i) => new[] { (i + 1).ToString(), Code(p.ToolName), Code(p.PeakTurnId), Fmt(p.Tokens), Fmt(p.Chars) }));
        sb.AppendLine();

        sb.AppendLine("## Agent built-in tools — top 12, cumulative");
        sb.AppendLine();
        WriteTable(sb,
            ["Rank", "Tool", "Calls", "Est. tokens", "Chars"],
            attribution.CumulativeToolRanking
                .Take(12)
                .Select((r, i) => new[] { (i + 1).ToString(), Code(r.ToolName), r.Calls.ToString(CultureInfo.InvariantCulture), Fmt(r.Tokens), Fmt(r.Chars) }));
        sb.AppendLine();

        // Sub-agent breakdown
        if (analysis.SubAgents.Count > 0)
        {
            sb.AppendLine("## Sub-agent activity (non-additive)");
            sb.AppendLine();
            sb.AppendLine("> The parent `task`/`explore`/etc. tool's result already counts toward this session's input. " +
                          "The breakdown below shows the *internal* tool usage that produced that result — do not add to the totals above.");
            sb.AppendLine();
            foreach (var sa in analysis.SubAgents.OrderByDescending(s => s.NestedToolCalls))
            {
                sb.AppendLine($"### Sub-agent via `{sa.ParentToolName}` ({sa.ParentToolCallId})");
                sb.AppendLine();
                sb.AppendLine($"- Nested assistant messages: {sa.NestedAssistantMessages}");
                sb.AppendLine($"- Nested tool calls: {sa.NestedToolCalls}");
                if (sa.NestedToolNameCounts.Count > 0)
                {
                    var top = sa.NestedToolNameCounts.OrderByDescending(kv => kv.Value).Take(8);
                    sb.AppendLine();
                    sb.AppendLine("| Tool | Calls |");
                    sb.AppendLine("|---|---:|");
                    foreach (var kv in top)
                        sb.AppendLine($"| {Code(kv.Key)} | {kv.Value} |");
                }
                sb.AppendLine();
            }
        }

        // Caveats / methodology
        sb.AppendLine("## Methodology and caveats");
        sb.AppendLine();
        sb.AppendLine("- Tool tokens were estimated from `tool.execution_complete.result.content` lengths via `chars / 4`. " +
                      "Different tokenizers (Anthropic vs. OpenAI) will give different exact figures; rankings should still be valid.");
        sb.AppendLine("- The agent's built-in tools (`bash`, `view`, `create`, `edit`, etc.) are scaffolding common to any coding agent. " +
                      "**External commands run via `bash`** are the more interesting ranking for evaluating developer-tool token cost.");
        sb.AppendLine("- A bash invocation is classified by its primary command. Leading `cd`, env-var assignments, and trivial " +
                      "lead-ins (`mkdir`, `echo`, …) are skipped to find the meaningful command. Pipelines are classified by their first stage.");
        sb.AppendLine("- For chained dotnet commands (e.g. `dotnet restore && dotnet build`), the bash result chars are attributed " +
                      "to *each* listed subcommand (so the dotnet subcommand totals may double-count chained calls). The " +
                      "`dotnet` share of `bash` figure de-dupes by parent bash call to avoid that.");
        sb.AppendLine("- The `dotnet` output repetition section uses normalized lines (paths/durations/GUIDs/hashes replaced) " +
                      "and reports cross-invocation repetition only — a line repeated 10× within a single invocation has " +
                      "support = 1 and is not counted here. Per-subcommand buckets exclude chained invocations (their output " +
                      "mixes content from multiple subcommands); the overall view de-duplicates at the parent bash call.");
        sb.AppendLine("- A turn is one top-level `assistant.message` (non-null `turnId`, no `parentToolCallId`). " +
                      "Sub-agent assistant messages and their tool calls are tracked separately and never added to the top-level totals.");
        sb.AppendLine("- Each tool result is attributed exactly once to the turn that produced it. Subsequent cache reads are NOT amplified into the cumulative ranking.");
        if (analysis.UnmatchedToolStarts > 0 || analysis.UnmatchedToolCompletes > 0)
            sb.AppendLine($"- Unmatched tool executions: {analysis.UnmatchedToolStarts} starts without completes, {analysis.UnmatchedToolCompletes} completes without starts. These were excluded from token attribution.");
        sb.AppendLine();
        sb.AppendLine("## Privacy");
        sb.AppendLine();
        sb.AppendLine("This report contains sizes, counts, tool names, dotnet subcommand keywords, the session UUID, the agent version, timestamps, and **normalized excerpts of dotnet CLI output** (paths/durations/GUIDs/hashes replaced with placeholders). It does **not** contain the contents of source files, command argument strings, raw user message text, or non-dotnet tool result text.");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"_Generated by `dotnet-agent-analyze` v{ToolVersion}._");

        return sb.ToString();
    }

    private static void WriteTable(StringBuilder sb, string[] headers, IEnumerable<string[]> rows)
    {
        sb.Append('|');
        foreach (var h in headers) sb.Append(' ').Append(h).Append(" |");
        sb.AppendLine();
        sb.Append('|');
        for (int i = 0; i < headers.Length; i++)
        {
            // right-align numeric-looking columns
            var h = headers[i];
            var alignRight = h.Contains("tokens", StringComparison.OrdinalIgnoreCase)
                || h.Contains("chars", StringComparison.OrdinalIgnoreCase)
                || h.Contains("count", StringComparison.OrdinalIgnoreCase)
                || h.Contains("calls", StringComparison.OrdinalIgnoreCase)
                || h.Contains("invocations", StringComparison.OrdinalIgnoreCase)
                || h.Equals("rank", StringComparison.OrdinalIgnoreCase);
            sb.Append(alignRight ? "---:|" : "---|");
        }
        sb.AppendLine();
        bool any = false;
        foreach (var r in rows)
        {
            any = true;
            sb.Append('|');
            foreach (var c in r) sb.Append(' ').Append(c).Append(" |");
            sb.AppendLine();
        }
        if (!any)
        {
            sb.Append('|');
            for (int i = 0; i < headers.Length; i++) sb.Append(" — |");
            sb.AppendLine();
        }
    }

    private static void WriteDotNetRepetitionSection(StringBuilder sb, DotNetOutputAnalysis a)
    {
        if (a.TotalDistinctInvocations == 0) return;

        sb.AppendLine("## `dotnet` output repetition");
        sb.AppendLine();
        sb.AppendLine("> Cross-invocation repetition reveals tokens spent re-ingesting content the agent already saw.");
        sb.AppendLine("> This includes both true CLI boilerplate (restore preambles, build-succeeded footers) and");
        sb.AppendLine("> repeated diagnostics (warnings/errors that fire on every build). Either way, the agent does");
        sb.AppendLine("> not need them more than once. Lines are normalized: paths → `<path>`, durations → `<dur>`,");
        sb.AppendLine("> GUIDs → `<guid>`, hex hashes → `<hash>`. Diagnostic IDs (`NETSDK1057`, `CS####`, `MSB####`)");
        sb.AppendLine("> and version numbers are preserved.");
        sb.AppendLine();

        var totalTokens = TokenEstimate.FromChars(a.TotalChars);
        sb.AppendLine($"- **Total dotnet output captured**: ~{Fmt(totalTokens)} tokens across {a.TotalDistinctInvocations} bash call(s) (~{Fmt(a.TotalChars)} chars).");
        if (a.Overall is { } o)
        {
            var supTokens = TokenEstimate.FromChars(o.SuppressibleChars);
            var pct = o.TotalChars > 0 ? (double)o.SuppressibleChars / o.TotalChars * 100 : 0;
            sb.AppendLine($"- **Suppressible (overall)**: ~{Fmt(supTokens)} tokens ({pct:0.0}% of dotnet output) " +
                          $"would be saved if every recurring line were ingested only once across the session.");
        }
        sb.AppendLine();

        // Per-subcommand breakdowns: only when the subcommand has ≥ 2 invocations.
        foreach (var bucket in a.PerSubcommand)
        {
            WriteRepetitionBucket(sb, $"`{bucket.Bucket}` subcommand", bucket, strongHeadline: bucket.Invocations >= 3);
        }

        // Overall last (it's the umbrella view).
        if (a.Overall is { } overall)
        {
            WriteRepetitionBucket(sb, "Overall (across all dotnet invocations)", overall, strongHeadline: overall.Invocations >= 3);
        }
    }

    private static void WriteRepetitionBucket(StringBuilder sb, string heading, DotNetSubcommandRepetition b, bool strongHeadline)
    {
        sb.AppendLine($"### {heading}");
        sb.AppendLine();
        var totalTokens = TokenEstimate.FromChars(b.TotalChars);
        var supTokens = TokenEstimate.FromChars(b.SuppressibleChars);
        sb.AppendLine($"- **Invocations**: {b.Invocations}");
        sb.AppendLine($"- **Total output**: ~{Fmt(totalTokens)} tokens (~{Fmt(b.TotalChars)} chars)");
        if (strongHeadline && b.TotalChars > 0)
        {
            var pct = (double)b.SuppressibleChars / b.TotalChars * 100;
            sb.AppendLine($"- **Suppressible**: ~{Fmt(supTokens)} tokens ({pct:0.0}% of this bucket) would be saved if recurring lines were ingested only once.");
        }
        else if (b.SuppressibleChars > 0)
        {
            sb.AppendLine($"- **Suppressible**: ~{Fmt(supTokens)} tokens (low confidence — only {b.Invocations} invocations).");
        }
        if (b.ExcludedChainedCount > 0)
        {
            var excTokens = TokenEstimate.FromChars(b.ExcludedChainedChars);
            sb.AppendLine($"- **Excluded chained calls**: {b.ExcludedChainedCount} ({excTokens:N0} tokens) — these mixed multiple subcommands and are counted in the overall view only.");
        }
        sb.AppendLine();

        if (b.TopRepeatedLines.Count == 0)
        {
            sb.AppendLine("_No lines recurred across invocations in this bucket._");
            sb.AppendLine();
            return;
        }

        sb.AppendLine($"Top {b.TopRepeatedLines.Count} repeating line(s) (ranked by token savings):");
        sb.AppendLine();
        WriteTable(sb,
            ["Rank", "Runs", "Occurrences", "Tokens", "Savings", "Line"],
            b.TopRepeatedLines.Select((r, i) =>
            {
                var recurring = TokenEstimate.FromChars((long)r.TotalOccurrences * r.Length);
                var suppress = TokenEstimate.FromChars((long)(r.TotalOccurrences - 1) * r.Length);
                return new[]
                {
                    (i + 1).ToString(),
                    $"{r.Support}/{b.Invocations}",
                    r.TotalOccurrences.ToString(CultureInfo.InvariantCulture),
                    Fmt(recurring),
                    Fmt(suppress),
                    "`" + Truncate(r.Normalized, 120).Replace("`", "\\`") + "`",
                };
            }));
        sb.AppendLine();
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    private static string Code(string s) => $"`{s}`";
    private static string Fmt(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    private static string FamilyLabel(BashFamily f) => f switch
    {
        BashFamily.DotNet => ".NET CLI",
        BashFamily.PackageManager => "Package manager",
        BashFamily.BuildTool => "Build tool",
        BashFamily.Runtime => "Language runtime",
        BashFamily.Vcs => "Version control",
        BashFamily.Container => "Container/orchestration",
        BashFamily.Cloud => "Cloud/IaC",
        BashFamily.Network => "Network",
        BashFamily.SearchUtility => "Search utility",
        BashFamily.ShellUtility => "Shell utility",
        BashFamily.Other => "Other",
        _ => "Unknown",
    };
}
