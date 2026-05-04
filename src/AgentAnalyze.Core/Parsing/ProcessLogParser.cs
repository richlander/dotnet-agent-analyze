using System.Text;
using System.Text.Json;
using AgentAnalyze.Core.Domain;

namespace AgentAnalyze.Core.Parsing;

/// <summary>
/// Extracts <c>assistant_usage</c> telemetry blocks from a Copilot CLI process log
/// (the multi-line text logs at <c>~/.copilot/logs/process-*.log</c>).
/// </summary>
public static class ProcessLogParser
{
    /// <summary>
    /// Searches all process logs in <paramref name="logsDirectory"/> (default
    /// <c>~/.copilot/logs</c>) for ones that contain the given <paramref name="sessionId"/>
    /// and returns the merged list of usage records found, plus the list of log files matched.
    /// Pass <paramref name="logModifiedAfter"/> to skip log files whose mtime predates a known
    /// session start (a useful prefilter when scanning dozens of GB of historical logs).
    /// </summary>
    public static ProcessLogScanResult ScanForSession(string sessionId, string? logsDirectory = null, DateTime? logModifiedAfter = null)
    {
        logsDirectory ??= DefaultLogsDirectory();
        var matchedLogs = new List<string>();
        var usages = new List<AssistantUsage>();

        if (!Directory.Exists(logsDirectory))
            return new ProcessLogScanResult(usages, matchedLogs);

        foreach (var logPath in Directory.EnumerateFiles(logsDirectory, "process-*.log"))
        {
            if (logModifiedAfter is { } cutoff && File.GetLastWriteTimeUtc(logPath) < cutoff)
                continue;
            if (!FileContainsSessionId(logPath, sessionId))
                continue;
            matchedLogs.Add(logPath);
            usages.AddRange(ExtractUsages(logPath, sessionId));
        }

        return new ProcessLogScanResult(usages, matchedLogs);
    }

    /// <summary>
    /// Builds an index of <c>assistant_usage</c> events keyed by <c>session_id</c> by
    /// streaming all process logs once. Useful when reporting on many sessions at once.
    /// </summary>
    /// <param name="sessionIds">Restrict the index to these session ids only.</param>
    /// <param name="logsDirectory">Defaults to <c>~/.copilot/logs</c>.</param>
    /// <param name="logModifiedAfter">Skip log files older than this. Recommended.</param>
    public static ProcessLogIndex BuildIndex(
        ISet<string> sessionIds,
        string? logsDirectory = null,
        DateTime? logModifiedAfter = null)
    {
        logsDirectory ??= DefaultLogsDirectory();
        var usagesBySession = new Dictionary<string, List<AssistantUsage>>(StringComparer.Ordinal);
        var logsBySession = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        if (!Directory.Exists(logsDirectory) || sessionIds.Count == 0)
            return new ProcessLogIndex(usagesBySession, logsBySession, ScannedLogs: 0);

        int scanned = 0;
        foreach (var logPath in Directory.EnumerateFiles(logsDirectory, "process-*.log"))
        {
            if (logModifiedAfter is { } cutoff && File.GetLastWriteTimeUtc(logPath) < cutoff)
                continue;

            // Quick filter: which of our session ids does this file mention at all?
            // (One pass over the file, looking for any of the wanted ids.)
            var present = SessionIdsPresent(logPath, sessionIds);
            if (present.Count == 0) continue;

            scanned++;
            foreach (var id in present)
            {
                if (!logsBySession.TryGetValue(id, out var set))
                    logsBySession[id] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(logPath);

                foreach (var u in ExtractUsages(logPath, id))
                {
                    if (!usagesBySession.TryGetValue(id, out var list))
                        usagesBySession[id] = list = [];
                    list.Add(u);
                }
            }
        }

        return new ProcessLogIndex(usagesBySession, logsBySession, scanned);
    }

    private static HashSet<string> SessionIdsPresent(string path, ISet<string> sessionIds)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                // Cheap check: does the line contain any wanted session id? Most lines won't.
                foreach (var id in sessionIds)
                {
                    if (found.Contains(id)) continue;
                    if (line.Contains(id, StringComparison.Ordinal))
                    {
                        found.Add(id);
                        if (found.Count == sessionIds.Count) return found;
                    }
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return found;
    }

    /// <summary>Default directory for Copilot CLI process logs.</summary>
    public static string DefaultLogsDirectory()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "logs");

    private static bool FileContainsSessionId(string path, string sessionId)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Contains(sessionId, StringComparison.Ordinal))
                    return true;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return false;
    }

    /// <summary>
    /// Extracts <c>assistant_usage</c> telemetry events that belong to the given session.
    /// </summary>
    public static IEnumerable<AssistantUsage> ExtractUsages(string logPath, string sessionId)
    {
        // Pattern in logs:
        //   <ts> [INFO] [Telemetry] cli.telemetry:
        //   {
        //     "kind": "assistant_usage",
        //     ...
        //   }
        // We look for the marker line and then capture lines until the JSON object's
        // braces balance.
        using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        string? line;
        bool capturing = false;
        var buf = new StringBuilder();
        int depth = 0;
        while ((line = reader.ReadLine()) != null)
        {
            if (!capturing)
            {
                if (line.Contains("cli.telemetry:", StringComparison.Ordinal))
                {
                    capturing = true;
                    buf.Clear();
                    depth = 0;
                }
                continue;
            }

            // Strip log timestamp/level prefix from each line of the JSON payload.
            // Lines look like: "2026-05-03T21:50:39.913Z [INFO] {" or "  \"kind\": ..."
            // The first JSON-payload line starts with '{'; subsequent ones are pure JSON.
            var trimmed = StripLogPrefix(line);
            buf.AppendLine(trimmed);
            depth += CountBraces(trimmed, '{') - CountBraces(trimmed, '}');
            if (depth <= 0 && buf.Length > 0 && trimmed.Contains('}'))
            {
                // Try to parse
                AssistantUsage? usage = null;
                try
                {
                    var json = buf.ToString();
                    using var doc = JsonDocument.Parse(json);
                    usage = ParseUsage(doc.RootElement, sessionId);
                }
                catch (JsonException)
                {
                    // skip malformed
                }
                if (usage != null)
                    yield return usage;
                capturing = false;
                buf.Clear();
                depth = 0;
            }
        }
    }

    private static AssistantUsage? ParseUsage(JsonElement root, string sessionId)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("kind", out var kind) || kind.GetString() != "assistant_usage") return null;
        if (!root.TryGetProperty("session_id", out var sid) || sid.GetString() != sessionId) return null;

        string? providerCallId = null;
        string? apiCallId = null;
        string? model = null;
        if (root.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            if (props.TryGetProperty("provider_call_id", out var pcid)) providerCallId = pcid.GetString();
            if (props.TryGetProperty("api_call_id", out var acid)) apiCallId = acid.GetString();
            if (props.TryGetProperty("model", out var m)) model = m.GetString();
        }

        int? inputTokens = null, outputTokens = null, cacheRead = null, cacheWrite = null, inputUncached = null;
        int? duration = null;
        if (root.TryGetProperty("metrics", out var metrics) && metrics.ValueKind == JsonValueKind.Object)
        {
            inputTokens = TryInt(metrics, "input_tokens");
            outputTokens = TryInt(metrics, "output_tokens");
            cacheRead = TryInt(metrics, "cache_read_tokens");
            cacheWrite = TryInt(metrics, "cache_write_tokens");
            inputUncached = TryInt(metrics, "input_tokens_uncached");
            duration = TryInt(metrics, "duration");
        }

        return new AssistantUsage(
            ProviderCallId: providerCallId,
            ApiCallId: apiCallId,
            Model: model,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            CacheReadTokens: cacheRead,
            CacheWriteTokens: cacheWrite,
            InputTokensUncached: inputUncached,
            DurationMs: duration);
    }

    private static int? TryInt(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
    }

    private static string StripLogPrefix(string line)
    {
        // Match "2026-05-03T21:50:39.913Z [LEVEL] " prefix; if present, drop it.
        int bracketEnd = -1;
        if (line.Length > 25 && line[10] == 'T' && line[13] == ':' && line[16] == ':')
        {
            bracketEnd = line.IndexOf(']', 0);
            if (bracketEnd > 0 && bracketEnd + 1 < line.Length && line[bracketEnd + 1] == ' ')
                return line[(bracketEnd + 2)..];
        }
        return line;
    }

    private static int CountBraces(string s, char b)
    {
        int n = 0;
        bool inString = false;
        bool escape = false;
        foreach (var c in s)
        {
            if (escape) { escape = false; continue; }
            if (c == '\\') { escape = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (!inString && c == b) n++;
        }
        return n;
    }
}

/// <summary>
/// One <c>assistant_usage</c> telemetry record.
/// </summary>
public sealed record AssistantUsage(
    string? ProviderCallId,
    string? ApiCallId,
    string? Model,
    int? InputTokens,
    int? OutputTokens,
    int? CacheReadTokens,
    int? CacheWriteTokens,
    int? InputTokensUncached,
    int? DurationMs
);

/// <summary>
/// Result of scanning process logs for a given session.
/// </summary>
public sealed record ProcessLogScanResult(
    IReadOnlyList<AssistantUsage> Usages,
    IReadOnlyList<string> LogFilesScanned
);

/// <summary>
/// Pre-built index of process-log assistant_usage events keyed by session id.
/// </summary>
public sealed record ProcessLogIndex(
    IReadOnlyDictionary<string, List<AssistantUsage>> UsagesBySession,
    IReadOnlyDictionary<string, HashSet<string>> LogsBySession,
    int ScannedLogs
);
