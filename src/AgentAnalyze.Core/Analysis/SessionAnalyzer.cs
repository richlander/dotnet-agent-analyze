using AgentAnalyze.Core.Domain;
using AgentAnalyze.Core.Parsing;

namespace AgentAnalyze.Core.Analysis;

/// <summary>
/// Orchestrates parsing + token attribution for a Copilot session directory.
/// </summary>
public static class SessionAnalyzer
{
    /// <summary>
    /// Analyzes the session at <paramref name="sessionDirectory"/> and (best-effort) merges
    /// authoritative input-token telemetry from process logs in <paramref name="logsDirectory"/>
    /// (default <c>~/.copilot/logs</c>).
    /// </summary>
    public static SessionAnalysis Analyze(string sessionDirectory, string? logsDirectory = null)
    {
        var parsed = CopilotSessionParser.Parse(sessionDirectory);
        var scan = ProcessLogParser.ScanForSession(parsed.Metadata.SessionId, logsDirectory);
        return Combine(parsed, scan.Usages, scan.LogFilesScanned);
    }

    /// <summary>
    /// Variant that pulls authoritative telemetry from a pre-built
    /// <see cref="ProcessLogIndex"/> instead of re-scanning logs. Use for batch analysis.
    /// </summary>
    public static SessionAnalysis Analyze(string sessionDirectory, ProcessLogIndex telemetryIndex)
    {
        var parsed = CopilotSessionParser.Parse(sessionDirectory);
        return Analyze(parsed, telemetryIndex);
    }

    /// <summary>
    /// Variant that combines an already-parsed session with telemetry from a pre-built
    /// <see cref="ProcessLogIndex"/>. Useful when the caller has already parsed (e.g. to
    /// pre-filter by turn count) and wants to avoid re-parsing.
    /// </summary>
    public static SessionAnalysis Analyze(ParsedSession parsed, ProcessLogIndex telemetryIndex)
    {
        IReadOnlyList<AssistantUsage> usages =
            telemetryIndex.UsagesBySession.TryGetValue(parsed.Metadata.SessionId, out var u) ? u : [];
        IReadOnlyList<string> logs =
            telemetryIndex.LogsBySession.TryGetValue(parsed.Metadata.SessionId, out var l)
                ? l.ToList()
                : [];
        return Combine(parsed, usages, logs);
    }

    private static SessionAnalysis Combine(
        ParsedSession parsed,
        IReadOnlyList<AssistantUsage> usages,
        IReadOnlyList<string> logs)
    {
        // Map provider_call_id -> AssistantUsage (last one wins on dupes).
        var byProvider = new Dictionary<string, AssistantUsage>(StringComparer.Ordinal);
        foreach (var u in usages)
        {
            if (!string.IsNullOrEmpty(u.ProviderCallId))
                byProvider[u.ProviderCallId] = u;
        }

        var turnTokens = new List<TurnTokens>();
        int matched = 0;
        foreach (var t in parsed.Turns)
        {
            if (t.RequestId != null && byProvider.TryGetValue(t.RequestId, out var u))
            {
                matched++;
                turnTokens.Add(new TurnTokens(
                    TurnId: t.TurnId,
                    RequestId: t.RequestId,
                    InputTokens: u.InputTokens,
                    OutputTokens: u.OutputTokens,
                    CacheReadTokens: u.CacheReadTokens,
                    CacheWriteTokens: u.CacheWriteTokens,
                    InputTokensUncached: u.InputTokensUncached));
            }
            else
            {
                turnTokens.Add(new TurnTokens(t.TurnId, t.RequestId, null, null, null, null, null));
            }
        }

        var models = parsed.Metadata.Models.ToList();
        foreach (var u in usages)
        {
            if (!string.IsNullOrEmpty(u.Model) && !models.Contains(u.Model))
                models.Add(u.Model);
        }
        var meta = parsed.Metadata with { Models = models };

        return new SessionAnalysis(
            Metadata: meta,
            Turns: parsed.Turns,
            SubAgents: parsed.SubAgents,
            DotNetCommands: parsed.DotNetCommands,
            BashInvocations: parsed.BashInvocations,
            AuthoritativeTokens: turnTokens,
            UnmatchedToolStarts: parsed.UnmatchedToolStarts,
            UnmatchedToolCompletes: parsed.UnmatchedToolCompletes,
            ProcessLogCoverage: new ProcessLogCoverage(matched, parsed.Turns.Count, logs),
            DotNetOutputAnalysis: DotNetOutputAnalyzer.Analyze(parsed));
    }
}
