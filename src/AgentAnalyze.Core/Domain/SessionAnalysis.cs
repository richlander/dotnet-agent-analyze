namespace AgentAnalyze.Core.Domain;

/// <summary>
/// Identifying information about an agent session.
/// </summary>
public sealed record SessionMetadata(
    string SessionId,
    DateTime StartedAt,
    string? CopilotVersion,
    string? Cwd,
    IReadOnlyList<string> Models,
    string? OsUser
);

/// <summary>
/// A single top-level turn — one round trip to the assistant model that the user
/// (or another turn's tool result) initiated. Sub-agent activity is attached via
/// <see cref="SubAgentActivity"/> and is NOT counted as additional turns.
/// </summary>
public sealed record Turn(
    string TurnId,
    DateTime AssistantTimestamp,
    string? RequestId,
    int OutputTokens,
    int UserTextChars,
    IReadOnlyList<ToolCall> ToolCalls
);

/// <summary>
/// One tool invocation paired (when possible) with its result.
/// </summary>
public sealed record ToolCall(
    string ToolCallId,
    string ToolName,
    string TurnId,
    int ResultChars,
    bool Completed,
    bool IsError,
    string? DotNetCommand,
    string? ParentToolCallId
);

/// <summary>
/// Sub-agent run, identified by the parent tool call (e.g. a <c>task</c> or <c>explore</c>
/// invocation). Counted separately from main-thread tool calls to avoid double-counting.
/// </summary>
public sealed record SubAgentActivity(
    string ParentToolCallId,
    string ParentToolName,
    int NestedAssistantMessages,
    int NestedToolCalls,
    IReadOnlyDictionary<string, int> NestedToolNameCounts
);

/// <summary>
/// Authoritative per-turn token counts pulled from process-*.log assistant_usage
/// telemetry. Populated only when the .log file is available and the requestId
/// matches a provider_call_id.
/// </summary>
public sealed record TurnTokens(
    string TurnId,
    string? RequestId,
    int? InputTokens,
    int? OutputTokens,
    int? CacheReadTokens,
    int? CacheWriteTokens,
    int? InputTokensUncached
);

/// <summary>
/// A normalized .NET CLI invocation extracted from a bash tool call.
/// </summary>
/// <param name="TurnId">Turn the parent bash call belongs to.</param>
/// <param name="Command">Subcommand keyword (e.g. <c>build</c>, <c>test</c>) or the full executable name for <c>dotnet-foo</c> tools.</param>
/// <param name="FullCommand">The dotnet portion of the bash command, used for diagnostics only — not included in reports.</param>
/// <param name="ParentBashCallId">Tool call id of the bash call that ran this command.</param>
/// <param name="ParentBashResultChars">Result-char count of the parent bash call. When the bash chain runs multiple dotnet subcommands, the same value is repeated on each — see <see cref="IsChained"/>.</param>
/// <param name="IsChained">True when the parent bash call contained more than one dotnet invocation. Aggregate consumers should de-dup by <see cref="ParentBashCallId"/> if they want to avoid double counting result chars.</param>
public sealed record DotNetCommandUse(
    string TurnId,
    string Command,
    string FullCommand,
    string ParentBashCallId,
    int ParentBashResultChars,
    bool IsChained
);

/// <summary>
/// One bash invocation, with its classification by primary external command.
/// </summary>
public sealed record BashInvocation(
    string TurnId,
    string ToolCallId,
    int ResultChars,
    string PrimaryCommand,
    BashFamily Family
);

/// <summary>
/// Coarse classification of a bash command's primary executable.
/// </summary>
public enum BashFamily
{
    Unknown = 0,
    /// <summary>The .NET CLI: <c>dotnet</c>, <c>dotnet-trace</c>, etc.</summary>
    DotNet,
    /// <summary>npm, pip, cargo, brew, apt, etc.</summary>
    PackageManager,
    /// <summary>make, cmake, mvn, gradle, tsc, webpack, msbuild, etc.</summary>
    BuildTool,
    /// <summary>Language runtimes: node, python, ruby, java, etc.</summary>
    Runtime,
    /// <summary>git, gh, hg, svn.</summary>
    Vcs,
    /// <summary>docker, kubectl, helm, podman.</summary>
    Container,
    /// <summary>aws, az, gcloud, terraform, ansible.</summary>
    Cloud,
    /// <summary>curl, wget, ssh, rsync, etc.</summary>
    Network,
    /// <summary>grep, rg, find, fd, tree.</summary>
    SearchUtility,
    /// <summary>ls, cat, jq, tar, sed, awk, chmod, ps, etc.</summary>
    ShellUtility,
    /// <summary>Anything else recognized as an external command.</summary>
    Other,
}

/// <summary>
/// The full analysis of a single session.
/// </summary>
public sealed record SessionAnalysis(
    SessionMetadata Metadata,
    IReadOnlyList<Turn> Turns,
    IReadOnlyList<SubAgentActivity> SubAgents,
    IReadOnlyList<DotNetCommandUse> DotNetCommands,
    IReadOnlyList<BashInvocation> BashInvocations,
    IReadOnlyList<TurnTokens> AuthoritativeTokens,
    int UnmatchedToolStarts,
    int UnmatchedToolCompletes,
    ProcessLogCoverage ProcessLogCoverage,
    DotNetOutputAnalysis DotNetOutputAnalysis
);

/// <summary>
/// Cross-invocation repetition analysis of dotnet CLI output: identifies lines that
/// recur across multiple dotnet invocations (the "constant overhead" of the CLI).
/// </summary>
/// <param name="PerSubcommand">One bucket per dotnet subcommand (e.g. <c>build</c>, <c>restore</c>) that was invoked at least twice as a non-chained call.</param>
/// <param name="Overall">Cross-subcommand view, deduplicated at parent bash call. <c>null</c> when fewer than two distinct dotnet bash calls have captured output.</param>
/// <param name="TotalDistinctInvocations">Distinct parent bash calls with captured dotnet output.</param>
/// <param name="TotalChars">Sum of captured output chars across all dotnet bash calls.</param>
public sealed record DotNetOutputAnalysis(
    IReadOnlyList<DotNetSubcommandRepetition> PerSubcommand,
    DotNetSubcommandRepetition? Overall,
    int TotalDistinctInvocations,
    long TotalChars
)
{
    /// <summary>An empty analysis (no dotnet activity captured).</summary>
    public static DotNetOutputAnalysis Empty { get; } =
        new([], null, 0, 0);
}

/// <summary>
/// Repetition stats for a single bucket of dotnet invocations (one subcommand, or the
/// "overall" view).
/// </summary>
/// <param name="Bucket">Either a subcommand keyword (e.g. <c>build</c>) or <c>"overall"</c>.</param>
/// <param name="Invocations">Distinct invocations counted in this bucket.</param>
/// <param name="TotalChars">Sum of captured output chars in this bucket.</param>
/// <param name="RecurringChars">Total chars contributed by lines whose support is ≥ 2 invocations (i.e. cross-invocation repetition).</param>
/// <param name="SuppressibleChars">Chars that could be saved if each repeating line were retained only once across the session.</param>
/// <param name="ExcludedChainedCount">For per-subcommand buckets only: number of chained bash calls whose output was excluded (because it mixed multiple subcommands' output). Always 0 for "overall".</param>
/// <param name="ExcludedChainedChars">Total char volume of those excluded chained calls. Always 0 for "overall".</param>
/// <param name="TopRepeatedLines">Top repeating lines (already filtered to support ≥ 2), ranked by suppressible chars.</param>
public sealed record DotNetSubcommandRepetition(
    string Bucket,
    int Invocations,
    long TotalChars,
    long RecurringChars,
    long SuppressibleChars,
    int ExcludedChainedCount,
    long ExcludedChainedChars,
    IReadOnlyList<RepeatedLine> TopRepeatedLines
);

/// <summary>
/// One normalized line that recurred across multiple dotnet invocations.
/// </summary>
/// <param name="Normalized">Normalized form of the line; the value displayed in reports.</param>
/// <param name="Support">Distinct invocations the line appeared in (≥ 2).</param>
/// <param name="TotalOccurrences">Total occurrences across all those invocations (a single invocation may emit the line multiple times).</param>
/// <param name="Length">Length of <see cref="Normalized"/>.</param>
public sealed record RepeatedLine(
    string Normalized,
    int Support,
    int TotalOccurrences,
    int Length
);

/// <summary>
/// How much of the session was covered by authoritative process-log telemetry.
/// </summary>
public sealed record ProcessLogCoverage(
    int MatchedTurns,
    int TotalTurns,
    IReadOnlyList<string> LogsScanned
);

/// <summary>
/// One row of a multi-session summary. All token counts are estimates unless
/// <see cref="FreshInputTokens"/> / <see cref="OutputTokens"/> are populated, which come
/// from authoritative <c>assistant_usage</c> telemetry.
/// </summary>
public sealed record SessionSummary(
    string SessionId,
    DateTime StartedAt,
    TimeSpan? Duration,
    string? CopilotVersion,
    IReadOnlyList<string> Models,
    string? CwdBasename,
    int Turns,
    int ToolCalls,
    long? FreshInputTokens,
    long? OutputTokens,
    long BashTokens,
    long DotNetTokens,
    double? DotNetSharePct,
    string? TopAgentTool,
    long TopAgentToolTokens,
    string? TopExternalCommand,
    long TopExternalCommandTokens,
    ProcessLogCoverage ProcessLogCoverage
);

/// <summary>
/// A summary of the most recent N sessions.
/// </summary>
/// <param name="RequestedCount">The number of sessions the caller asked for (the <c>-n</c> value). May exceed <c>Sessions.Count</c> if filtering or scanning depleted the candidate pool.</param>
/// <param name="MinTurns">The min-turns filter applied. Sessions with fewer top-level turns were excluded.</param>
/// <param name="ExaminedCandidates">How many session directories were actually opened and parsed while looking for keepers.</param>
/// <param name="SkippedBelowMinTurns">Count of parsed sessions that were rejected by the <see cref="MinTurns"/> filter.</param>
/// <param name="SkippedDueToReadError">Count of session directories that failed to parse (corrupt JSONL, IO error, etc.).</param>
public sealed record MultiSessionSummary(
    DateTime GeneratedAt,
    IReadOnlyList<SessionSummary> Sessions,
    int ScannedLogs,
    bool IncludedTelemetry,
    int RequestedCount,
    int MinTurns,
    int ExaminedCandidates,
    int SkippedBelowMinTurns,
    int SkippedDueToReadError
);
