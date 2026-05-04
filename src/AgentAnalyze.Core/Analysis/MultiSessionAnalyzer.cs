using AgentAnalyze.Core.Domain;
using AgentAnalyze.Core.Parsing;

namespace AgentAnalyze.Core.Analysis;

/// <summary>
/// Runs <see cref="SessionAnalyzer"/> across the most recent N sessions and produces
/// a compact <see cref="MultiSessionSummary"/> for one-page comparison.
/// </summary>
public static class MultiSessionAnalyzer
{
    /// <summary>
    /// Analyzes the most recent <paramref name="count"/> sessions in
    /// <paramref name="sessionStateRoot"/> (default <c>~/.copilot/session-state</c>),
    /// ordered by their <c>events.jsonl</c> mtime. Sessions with fewer than
    /// <paramref name="minTurns"/> top-level turns are skipped (defaults to 1, which
    /// hides empty / pre-1.0.40-format sessions). The returned
    /// <see cref="MultiSessionSummary"/> records skip stats so callers can surface them.
    /// </summary>
    public static MultiSessionSummary AnalyzeRecent(
        int count = 12,
        string? sessionStateRoot = null,
        string? logsDirectory = null,
        bool includeTelemetry = true,
        int minTurns = 1)
    {
        sessionStateRoot ??= DefaultSessionStateRoot();

        // Walk in mtime-desc order, parsing each candidate, and keep the first `count`
        // that pass the min-turns filter. We avoid materializing the full discovery list
        // up-front so a user with thousands of sessions doesn't pay for them all.
        var orderedCandidates = DiscoverSessions(sessionStateRoot)
            .OrderByDescending(s => s.LastActivityUtc);

        var keepers = new List<(DiscoveredSession Discovered, ParsedSession Parsed)>(count);
        int examined = 0;
        int skippedBelowMinTurns = 0;
        int skippedDueToReadError = 0;

        foreach (var sd in orderedCandidates)
        {
            if (keepers.Count >= count) break;
            examined++;

            ParsedSession parsed;
            try
            {
                parsed = CopilotSessionParser.Parse(sd.Directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                skippedDueToReadError++;
                continue;
            }

            if (parsed.Turns.Count < minTurns)
            {
                skippedBelowMinTurns++;
                continue;
            }

            keepers.Add((sd, parsed));
        }

        ProcessLogIndex index;
        if (keepers.Count > 0 && includeTelemetry)
        {
            // Use parsed StartedAt (rather than events.jsonl mtime) for the cutoff —
            // long-running sessions could have telemetry written well before the file's
            // last write time, so this is slightly safer.
            DateTime earliest = keepers.Min(k => k.Parsed.Metadata.StartedAt).AddDays(-7);
            var ids = new HashSet<string>(keepers.Select(k => k.Parsed.Metadata.SessionId), StringComparer.Ordinal);
            index = ProcessLogParser.BuildIndex(ids, logsDirectory, logModifiedAfter: earliest);
        }
        else
        {
            // Empty index means no telemetry will be matched; skips per-session log scans.
            index = new ProcessLogIndex(
                new Dictionary<string, List<AssistantUsage>>(),
                new Dictionary<string, HashSet<string>>(),
                ScannedLogs: 0);
        }

        var rows = new List<SessionSummary>(keepers.Count);
        foreach (var (_, parsed) in keepers)
        {
            var analysis = SessionAnalyzer.Analyze(parsed, index);
            var attribution = TokenAttributor.Compute(analysis);
            rows.Add(BuildSummary(analysis, attribution));
        }

        return new MultiSessionSummary(
            GeneratedAt: DateTime.UtcNow,
            Sessions: rows,
            ScannedLogs: index.ScannedLogs,
            IncludedTelemetry: includeTelemetry,
            RequestedCount: count,
            MinTurns: minTurns,
            ExaminedCandidates: examined,
            SkippedBelowMinTurns: skippedBelowMinTurns,
            SkippedDueToReadError: skippedDueToReadError);
    }

    private static SessionSummary BuildSummary(SessionAnalysis analysis, AttributionResult attribution)
    {
        var meta = analysis.Metadata;
        var topAgentTool = attribution.CumulativeToolRanking.FirstOrDefault();
        var topExternal = attribution.BashPrimaryRanking.FirstOrDefault();
        var totalToolCalls = analysis.Turns.Sum(t => t.ToolCalls.Count);

        long? freshInput = null;
        if (attribution.AuthoritativeInputTokens is { } input)
            freshInput = input - (attribution.AuthoritativeCacheReadTokens ?? 0);

        double? dotnetSharePct = null;
        if (attribution.TotalBashChars > 0 && attribution.DotNetParentBashChars > 0)
            dotnetSharePct = (double)attribution.DotNetParentBashChars / attribution.TotalBashChars * 100;

        DateTime? lastTurnAt = analysis.Turns.LastOrDefault()?.AssistantTimestamp;
        TimeSpan? duration = lastTurnAt.HasValue ? lastTurnAt - meta.StartedAt : null;

        return new SessionSummary(
            SessionId: meta.SessionId,
            StartedAt: meta.StartedAt,
            Duration: duration,
            CopilotVersion: meta.CopilotVersion,
            Models: meta.Models,
            CwdBasename: !string.IsNullOrEmpty(meta.Cwd) ? Path.GetFileName(meta.Cwd.TrimEnd('/', '\\')) : null,
            Turns: analysis.Turns.Count,
            ToolCalls: totalToolCalls,
            FreshInputTokens: freshInput,
            OutputTokens: attribution.AuthoritativeOutputTokens,
            BashTokens: TokenEstimate.FromChars(attribution.TotalBashChars),
            DotNetTokens: TokenEstimate.FromChars(attribution.DotNetParentBashChars),
            DotNetSharePct: dotnetSharePct,
            TopAgentTool: topAgentTool?.ToolName,
            TopAgentToolTokens: topAgentTool?.Tokens ?? 0,
            TopExternalCommand: topExternal?.PrimaryCommand,
            TopExternalCommandTokens: topExternal?.Tokens ?? 0,
            ProcessLogCoverage: analysis.ProcessLogCoverage);
    }

    private static IEnumerable<DiscoveredSession> DiscoverSessions(string root)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var events = Path.Combine(dir, "events.jsonl");
            if (!File.Exists(events)) continue;
            var sessionId = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(sessionId)) continue;
            DateTime mtime;
            try { mtime = File.GetLastWriteTimeUtc(events); }
            catch { continue; }
            yield return new DiscoveredSession(sessionId, dir, mtime);
        }
    }

    private static string DefaultSessionStateRoot()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "session-state");

    private sealed record DiscoveredSession(string SessionId, string Directory, DateTime LastActivityUtc);
}
