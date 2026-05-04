using AgentAnalyze.Core.Domain;
using AgentAnalyze.Core.Parsing;

namespace AgentAnalyze.Core.Analysis;

/// <summary>
/// Cross-invocation repetition analysis for dotnet CLI output. For each subcommand
/// (and an "overall" view that spans them), identifies normalized lines that recur
/// across multiple invocations — the constant overhead the agent re-ingests every
/// time the CLI runs.
/// </summary>
public static class DotNetOutputAnalyzer
{
    /// <summary>Minimum distinct-invocation support required for a line to be reported.</summary>
    public const int MinSupport = 2;

    /// <summary>Cap on top-N repeating lines returned per bucket.</summary>
    public const int TopLinesPerBucket = 20;

    /// <summary>
    /// Build a <see cref="DotNetOutputAnalysis"/> from a parsed session's captured
    /// dotnet output (see <c>ParsedSession.DotNetOutputByToolCallId</c>).
    /// </summary>
    public static DotNetOutputAnalysis Analyze(ParsedSession parsed)
    {
        if (parsed.DotNetOutputByToolCallId.Count == 0)
            return DotNetOutputAnalysis.Empty;

        // Group dotnet command uses by parent bash call to learn each call's subcommand set.
        // A "chained" call is one whose bash command contained ≥ 2 dotnet invocations
        // (potentially different subcommands); per-subcommand stats exclude these because
        // their captured output mixes content from multiple subcommands.
        var subcmdsByCallId = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var use in parsed.DotNetCommands)
        {
            if (!subcmdsByCallId.TryGetValue(use.ParentBashCallId, out var set))
                subcmdsByCallId[use.ParentBashCallId] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(use.Command);
        }

        // Build a per-invocation record — one entry per distinct parent bash call id
        // that has captured output.
        var invocations = new List<Invocation>(parsed.DotNetOutputByToolCallId.Count);
        foreach (var (callId, output) in parsed.DotNetOutputByToolCallId)
        {
            // A bash call could in principle contain a dotnet command we couldn't extract
            // (parser disagreement) — skip those defensively.
            if (!subcmdsByCallId.TryGetValue(callId, out var subcmds) || subcmds.Count == 0)
                continue;

            // Count normalized line occurrences within this invocation. We sum
            // occurrences (so a line repeated 10× within one build counts as 10), but
            // "support" is computed at the invocation level downstream.
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var raw in DotNetOutputNormalizer.SplitNonEmptyLines(output))
            {
                var norm = DotNetOutputNormalizer.Normalize(raw);
                if (norm.Length == 0) continue;
                counts.TryGetValue(norm, out var prev);
                counts[norm] = prev + 1;
            }

            invocations.Add(new Invocation(
                CallId: callId,
                Subcommands: subcmds,
                IsChained: subcmds.Count > 1,
                TotalChars: output.Length,
                LineCounts: counts));
        }

        if (invocations.Count == 0) return DotNetOutputAnalysis.Empty;

        var totalChars = invocations.Sum(i => i.TotalChars);

        // Per-subcommand: group single-subcommand invocations by their subcommand.
        // Chained invocations are counted as "excluded" against every subcommand they touch.
        var perSubcmd = new List<DotNetSubcommandRepetition>();
        var singles = invocations.Where(i => !i.IsChained).ToList();
        var chained = invocations.Where(i => i.IsChained).ToList();

        var allSubcmds = singles.SelectMany(i => i.Subcommands)
            .Concat(chained.SelectMany(i => i.Subcommands))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal);

        foreach (var sub in allSubcmds)
        {
            var bucketSingles = singles.Where(i => i.Subcommands.Contains(sub)).ToList();
            var bucketChained = chained.Where(i => i.Subcommands.Contains(sub)).ToList();
            if (bucketSingles.Count < MinSupport) continue;

            perSubcmd.Add(BuildBucket(
                bucket: sub,
                contributing: bucketSingles,
                excludedChainedCount: bucketChained.Count,
                excludedChainedChars: bucketChained.Sum(i => i.TotalChars)));
        }

        // Overall: every distinct parent bash call contributes once (chained de-dup'd).
        DotNetSubcommandRepetition? overall = invocations.Count >= MinSupport
            ? BuildBucket("overall", invocations, excludedChainedCount: 0, excludedChainedChars: 0)
            : null;

        return new DotNetOutputAnalysis(
            PerSubcommand: perSubcmd,
            Overall: overall,
            TotalDistinctInvocations: invocations.Count,
            TotalChars: totalChars);
    }

    private static DotNetSubcommandRepetition BuildBucket(
        string bucket,
        IReadOnlyList<Invocation> contributing,
        int excludedChainedCount,
        long excludedChainedChars)
    {
        // Aggregate across the contributing invocations:
        //   support[line]      = number of distinct invocations the line appeared in
        //   occurrences[line]  = total occurrences across all those invocations
        var support = new Dictionary<string, int>(StringComparer.Ordinal);
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var inv in contributing)
        {
            foreach (var (line, n) in inv.LineCounts)
            {
                support.TryGetValue(line, out var s);
                support[line] = s + 1;
                occurrences.TryGetValue(line, out var o);
                occurrences[line] = o + n;
            }
        }

        long recurringChars = 0;
        long suppressibleChars = 0;
        var repeating = new List<RepeatedLine>();
        foreach (var (line, sup) in support)
        {
            if (sup < MinSupport) continue;
            var occ = occurrences[line];
            recurringChars += (long)occ * line.Length;
            // If we kept a single copy across the whole bucket and dropped every other
            // emission, savings = (occ - 1) × len.
            suppressibleChars += (long)(occ - 1) * line.Length;
            repeating.Add(new RepeatedLine(line, sup, occ, line.Length));
        }

        // Rank by suppressible cost; cap at TopLinesPerBucket.
        var top = repeating
            .OrderByDescending(r => (long)(r.TotalOccurrences - 1) * r.Length)
            .ThenByDescending(r => r.Support)
            .Take(TopLinesPerBucket)
            .ToList();

        return new DotNetSubcommandRepetition(
            Bucket: bucket,
            Invocations: contributing.Count,
            TotalChars: contributing.Sum(i => i.TotalChars),
            RecurringChars: recurringChars,
            SuppressibleChars: suppressibleChars,
            ExcludedChainedCount: excludedChainedCount,
            ExcludedChainedChars: excludedChainedChars,
            TopRepeatedLines: top);
    }

    private sealed record Invocation(
        string CallId,
        HashSet<string> Subcommands,
        bool IsChained,
        long TotalChars,
        Dictionary<string, int> LineCounts);
}
