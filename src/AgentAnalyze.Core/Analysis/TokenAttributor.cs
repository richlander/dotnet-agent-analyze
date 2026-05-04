using AgentAnalyze.Core.Domain;

namespace AgentAnalyze.Core.Analysis;

/// <summary>
/// Computes ranked token-attribution views from a <see cref="SessionAnalysis"/>.
/// </summary>
public static class TokenAttributor
{
    /// <summary>
    /// Builds the rankings used by the report.
    /// </summary>
    public static AttributionResult Compute(SessionAnalysis analysis)
    {
        // Cumulative tool tokens (each tool result counted exactly once).
        var cumulativeChars = new Dictionary<string, long>(StringComparer.Ordinal);
        var cumulativeCalls = new Dictionary<string, int>(StringComparer.Ordinal);
        // Per-turn-and-tool sums for "single-turn peak" metric.
        var perTurnTool = new Dictionary<(string TurnId, string Tool), long>();

        foreach (var turn in analysis.Turns)
        {
            foreach (var call in turn.ToolCalls)
            {
                if (!call.Completed) continue; // skip unmatched
                cumulativeChars.TryGetValue(call.ToolName, out var prev);
                cumulativeChars[call.ToolName] = prev + call.ResultChars;
                cumulativeCalls.TryGetValue(call.ToolName, out var c);
                cumulativeCalls[call.ToolName] = c + 1;
                var key = (turn.TurnId, call.ToolName);
                perTurnTool.TryGetValue(key, out var t);
                perTurnTool[key] = t + call.ResultChars;
            }
        }

        // User-text tokens per turn
        var perTurnUserChars = analysis.Turns
            .Select(t => (TurnId: t.TurnId, Chars: (long)t.UserTextChars))
            .ToList();
        long cumulativeUserChars = perTurnUserChars.Sum(x => x.Chars);
        var peakUserTurn = perTurnUserChars.OrderByDescending(x => x.Chars).FirstOrDefault();

        // Cumulative ranking
        var cumulativeRanking = cumulativeChars
            .Select(kv => new ToolRanking(
                ToolName: kv.Key,
                Tokens: TokenEstimate.FromChars(kv.Value),
                Chars: kv.Value,
                Calls: cumulativeCalls.GetValueOrDefault(kv.Key)))
            .OrderByDescending(r => r.Chars)
            .ToList();

        // Single-turn peak per tool: for each tool, the turnId that maxes its own contribution.
        var perToolPeak = perTurnTool
            .GroupBy(kv => kv.Key.Tool)
            .Select(g =>
            {
                var max = g.MaxBy(kv => kv.Value);
                return new ToolPeak(
                    ToolName: g.Key,
                    PeakTurnId: max.Key.TurnId,
                    Tokens: TokenEstimate.FromChars(max.Value),
                    Chars: max.Value);
            })
            .OrderByDescending(p => p.Chars)
            .ToList();

        // Top-of-turn: in any single turn, the top tool by chars.
        var perTurnDominant = analysis.Turns
            .Select(t =>
            {
                var top = t.ToolCalls
                    .Where(c => c.Completed)
                    .GroupBy(c => c.ToolName)
                    .Select(g => (Tool: g.Key, Chars: (long)g.Sum(x => x.ResultChars)))
                    .OrderByDescending(x => x.Chars)
                    .FirstOrDefault();
                return new TurnTopTool(t.TurnId, top.Tool ?? "(none)", top.Chars);
            })
            .Where(x => x.Chars > 0)
            .OrderByDescending(x => x.Chars)
            .ToList();

        // Bash family ranking (by total result chars). Each invocation counted once.
        var bashFamilyChars = new Dictionary<BashFamily, long>();
        var bashFamilyCalls = new Dictionary<BashFamily, int>();
        var bashPrimaryChars = new Dictionary<string, long>(StringComparer.Ordinal);
        var bashPrimaryCalls = new Dictionary<string, int>(StringComparer.Ordinal);
        long bashTotalChars = 0;
        foreach (var bi in analysis.BashInvocations)
        {
            bashTotalChars += bi.ResultChars;
            bashFamilyChars.TryGetValue(bi.Family, out var fc);
            bashFamilyChars[bi.Family] = fc + bi.ResultChars;
            bashFamilyCalls.TryGetValue(bi.Family, out var fk);
            bashFamilyCalls[bi.Family] = fk + 1;

            var key = bi.PrimaryCommand.Length > 0 ? bi.PrimaryCommand : "(unknown)";
            bashPrimaryChars.TryGetValue(key, out var pc);
            bashPrimaryChars[key] = pc + bi.ResultChars;
            bashPrimaryCalls.TryGetValue(key, out var pk);
            bashPrimaryCalls[key] = pk + 1;
        }

        var bashFamilyRanking = bashFamilyChars
            .Select(kv => new BashFamilyRanking(
                Family: kv.Key,
                Calls: bashFamilyCalls[kv.Key],
                Chars: kv.Value,
                Tokens: TokenEstimate.FromChars(kv.Value)))
            .OrderByDescending(r => r.Chars)
            .ToList();

        var bashPrimaryRanking = bashPrimaryChars
            .Select(kv => new BashPrimaryRanking(
                PrimaryCommand: kv.Key,
                Calls: bashPrimaryCalls[kv.Key],
                Chars: kv.Value,
                Tokens: TokenEstimate.FromChars(kv.Value)))
            .OrderByDescending(r => r.Chars)
            .ToList();

        // Dotnet subcommands — count, plus token attribution from the parent bash call.
        // When a single bash call chains multiple dotnet invocations, we attribute the
        // bash result chars to each of them (over-counts if you sum across rows; that's
        // documented in the report's methodology).
        var dotnetByCommand = analysis.DotNetCommands
            .GroupBy(c => c.Command, StringComparer.OrdinalIgnoreCase)
            .Select(g => new DotNetRanking(
                Command: g.Key,
                Count: g.Count(),
                Chars: g.Sum(x => (long)x.ParentBashResultChars),
                Tokens: TokenEstimate.FromChars(g.Sum(x => (long)x.ParentBashResultChars))))
            .OrderByDescending(r => r.Chars)
            .ToList();

        // Total bash result chars whose parent call ran one or more dotnet commands —
        // de-duplicated by parent bash call id so chained calls aren't double-counted.
        long dotnetParentBashChars = analysis.DotNetCommands
            .GroupBy(c => c.ParentBashCallId, StringComparer.Ordinal)
            .Sum(g => (long)g.First().ParentBashResultChars);

        long totalBashChars = bashTotalChars;

        // Authoritative totals (if any)
        long authInput = analysis.AuthoritativeTokens.Where(t => t.InputTokens.HasValue).Sum(t => (long)t.InputTokens!.Value);
        long authOutput = analysis.AuthoritativeTokens.Where(t => t.OutputTokens.HasValue).Sum(t => (long)t.OutputTokens!.Value);
        long authCacheRead = analysis.AuthoritativeTokens.Where(t => t.CacheReadTokens.HasValue).Sum(t => (long)t.CacheReadTokens!.Value);
        long authCacheWrite = analysis.AuthoritativeTokens.Where(t => t.CacheWriteTokens.HasValue).Sum(t => (long)t.CacheWriteTokens!.Value);
        long sumOutputFromEvents = analysis.Turns.Sum(t => (long)t.OutputTokens);

        // Cache-break detection: only flag if cache hit rate dropped sharply AND the new
        // rate is low enough to suggest real cache invalidation (not just a model change
        // landing on a cold cache).
        var cacheBreakTurns = new List<CacheBreakTurn>();
        var withTokens = analysis.AuthoritativeTokens
            .Where(t => t.InputTokens is > 0)
            .Select(t => (TurnId: t.TurnId,
                          HitRate: (double)(t.CacheReadTokens ?? 0) / t.InputTokens!.Value))
            .ToList();
        if (withTokens.Count >= 4)
        {
            for (int i = 2; i < withTokens.Count; i++)
            {
                var prev = withTokens[i - 1].HitRate;
                var cur = withTokens[i].HitRate;
                if (prev > 0.85 && cur < 0.50 && cur < prev - 0.40)
                {
                    cacheBreakTurns.Add(new CacheBreakTurn(withTokens[i].TurnId, cur));
                }
            }
        }

        return new AttributionResult(
            CumulativeToolRanking: cumulativeRanking,
            ToolSingleTurnPeaks: perToolPeak,
            TurnTopTools: perTurnDominant,
            DotNetCommandRanking: dotnetByCommand,
            BashFamilyRanking: bashFamilyRanking,
            BashPrimaryRanking: bashPrimaryRanking,
            TotalBashChars: totalBashChars,
            DotNetParentBashChars: dotnetParentBashChars,
            CumulativeUserChars: cumulativeUserChars,
            CumulativeUserTokensEstimated: TokenEstimate.FromChars(cumulativeUserChars),
            PeakUserTurnId: peakUserTurn.TurnId,
            PeakUserTurnChars: peakUserTurn.Chars,
            PeakUserTurnTokensEstimated: TokenEstimate.FromChars(peakUserTurn.Chars),
            AuthoritativeInputTokens: authInput > 0 ? authInput : null,
            AuthoritativeOutputTokens: authOutput > 0 ? authOutput : null,
            AuthoritativeCacheReadTokens: authCacheRead > 0 ? authCacheRead : null,
            AuthoritativeCacheWriteTokens: authCacheWrite > 0 ? authCacheWrite : null,
            SumOutputTokensFromEvents: sumOutputFromEvents,
            CacheBreakTurns: cacheBreakTurns);
    }
}

public sealed record AttributionResult(
    IReadOnlyList<ToolRanking> CumulativeToolRanking,
    IReadOnlyList<ToolPeak> ToolSingleTurnPeaks,
    IReadOnlyList<TurnTopTool> TurnTopTools,
    IReadOnlyList<DotNetRanking> DotNetCommandRanking,
    IReadOnlyList<BashFamilyRanking> BashFamilyRanking,
    IReadOnlyList<BashPrimaryRanking> BashPrimaryRanking,
    long TotalBashChars,
    long DotNetParentBashChars,
    long CumulativeUserChars,
    long CumulativeUserTokensEstimated,
    string? PeakUserTurnId,
    long PeakUserTurnChars,
    long PeakUserTurnTokensEstimated,
    long? AuthoritativeInputTokens,
    long? AuthoritativeOutputTokens,
    long? AuthoritativeCacheReadTokens,
    long? AuthoritativeCacheWriteTokens,
    long SumOutputTokensFromEvents,
    IReadOnlyList<CacheBreakTurn> CacheBreakTurns
);

public sealed record ToolRanking(string ToolName, long Tokens, long Chars, int Calls);
public sealed record ToolPeak(string ToolName, string PeakTurnId, long Tokens, long Chars);
public sealed record TurnTopTool(string TurnId, string ToolName, long Chars);
public sealed record DotNetRanking(string Command, int Count, long Chars, long Tokens);
public sealed record BashFamilyRanking(BashFamily Family, int Calls, long Chars, long Tokens);
public sealed record BashPrimaryRanking(string PrimaryCommand, int Calls, long Chars, long Tokens);
public sealed record CacheBreakTurn(string TurnId, double HitRate);
