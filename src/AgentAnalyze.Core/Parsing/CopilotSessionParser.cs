using System.Globalization;
using System.Text.Json;
using AgentAnalyze.Core.Domain;

namespace AgentAnalyze.Core.Parsing;

/// <summary>
/// Reads a Copilot session-state directory (specifically <c>events.jsonl</c>) into a
/// structured <see cref="ParsedSession"/> with top-level turns and sub-agent activity.
/// </summary>
public static class CopilotSessionParser
{
    /// <summary>
    /// Parses the session at <paramref name="sessionDirectory"/> (the directory containing
    /// <c>events.jsonl</c>).
    /// </summary>
    public static ParsedSession Parse(string sessionDirectory)
    {
        var eventsPath = Path.Combine(sessionDirectory, "events.jsonl");
        if (!File.Exists(eventsPath))
            throw new FileNotFoundException($"events.jsonl not found in {sessionDirectory}", eventsPath);

        return ParseEventsFile(eventsPath, sessionDirectory);
    }

    /// <summary>
    /// Parses an events.jsonl file at <paramref name="eventsPath"/>. The
    /// <paramref name="sessionDirectory"/> is used only to derive a fallback session id
    /// when the file lacks a <c>session.start</c> event.
    /// </summary>
    public static ParsedSession ParseEventsFile(string eventsPath, string? sessionDirectory)
    {
        var raw = ReadAllRawEvents(eventsPath);

        // Session metadata
        string sessionId = Path.GetFileName(sessionDirectory ?? "") ?? "";
        DateTime startedAt = default;
        string? copilotVersion = null;
        string? cwd = null;
        var models = new List<string>();

        foreach (var evt in raw)
        {
            switch (evt.Type)
            {
                case "session.start":
                {
                    var data = evt.Data.Deserialize(CopilotJsonContext.Default.CopilotSessionStart);
                    if (data != null)
                    {
                        if (!string.IsNullOrEmpty(data.SessionId)) sessionId = data.SessionId;
                        copilotVersion = data.CopilotVersion;
                        cwd = data.Context?.Cwd;
                        startedAt = ParseTimestamp(data.StartTime) ?? ParseTimestamp(evt.Timestamp) ?? DateTime.UtcNow;
                    }
                    break;
                }
                case "session.model_change":
                {
                    var data = evt.Data.Deserialize(CopilotJsonContext.Default.CopilotModelChange);
                    if (!string.IsNullOrEmpty(data?.NewModel) && !models.Contains(data.NewModel))
                        models.Add(data.NewModel);
                    break;
                }
            }
        }

        // Index user.message events for per-turn user-text aggregation
        var topLevelUserChars = new List<(int Index, int Chars)>();
        var subAgentUserChars = new Dictionary<string, int>(StringComparer.Ordinal); // by parentAgentTaskId -> sum chars
        for (int i = 0; i < raw.Count; i++)
        {
            if (raw[i].Type != "user.message") continue;
            var data = raw[i].Data.Deserialize(CopilotJsonContext.Default.CopilotUserMessage);
            if (data == null) continue;
            // Prefer raw content over transformedContent (transformed contains injected datetime/system reminders).
            var text = data.Content ?? data.TransformedContent ?? string.Empty;
            int chars = text.Length;
            if (string.IsNullOrEmpty(data.ParentAgentTaskId))
                topLevelUserChars.Add((i, chars));
            else
            {
                subAgentUserChars.TryGetValue(data.ParentAgentTaskId, out var prev);
                subAgentUserChars[data.ParentAgentTaskId] = prev + chars;
            }
        }

        // Pre-collect tool starts and completes
        var toolStartByCallId = new Dictionary<string, ParsedToolStart>(StringComparer.Ordinal);
        var toolCompleteByCallId = new Dictionary<string, ParsedToolComplete>(StringComparer.Ordinal);
        // Captured raw output (ANSI-stripped) for bash calls that contain at least one
        // dotnet command (whether or not dotnet is the *primary* command of the bash
        // line — e.g. `git clean && dotnet build` or `timeout 120 dotnet test` count).
        var dotnetOutputByCallId = new Dictionary<string, string>(StringComparer.Ordinal);

        for (int i = 0; i < raw.Count; i++)
        {
            var evt = raw[i];
            if (evt.Type == "tool.execution_start")
            {
                var data = evt.Data.Deserialize(CopilotJsonContext.Default.CopilotToolStart);
                if (data?.ToolCallId == null || data.ToolName == null) continue;
                string? bashCommand = null;
                if (data.Arguments.ValueKind == JsonValueKind.Object &&
                    data.Arguments.TryGetProperty("command", out var cmd) &&
                    cmd.ValueKind == JsonValueKind.String)
                {
                    bashCommand = cmd.GetString();
                }
                toolStartByCallId[data.ToolCallId] = new ParsedToolStart(
                    data.ToolCallId, data.ToolName, data.TurnId, bashCommand, i);
            }
            else if (evt.Type == "tool.execution_complete")
            {
                var data = evt.Data.Deserialize(CopilotJsonContext.Default.CopilotToolComplete);
                if (data?.ToolCallId == null) continue;
                var content = data.Result?.Content;
                int chars = content?.Length ?? 0;
                toolCompleteByCallId[data.ToolCallId] = new ParsedToolComplete(
                    data.ToolCallId, data.Success, chars, i);

                // Capture dotnet output for later repetition analysis. Gate is "the
                // bash command contains a dotnet invocation", not "primary command is
                // dotnet" — so we don't miss `git clean && dotnet build` etc.
                if (!string.IsNullOrEmpty(content) &&
                    toolStartByCallId.TryGetValue(data.ToolCallId, out var start) &&
                    start.ToolName.Equals("bash", StringComparison.OrdinalIgnoreCase) &&
                    start.BashCommand is { } bashCmd &&
                    DotNetCommandExtractor.ExtractAll(bashCmd).Any())
                {
                    dotnetOutputByCallId[data.ToolCallId] = DotNetOutputNormalizer.StripAnsi(content);
                }
            }
        }

        // Walk events to build top-level turns, sub-agent activity, dotnet commands,
        // and bash invocation classifications.
        var turns = new List<Turn>();
        var dotnetCommands = new List<DotNetCommandUse>();
        var bashInvocations = new List<BashInvocation>();
        var subAgentByParentToolCallId = new Dictionary<string, SubAgentBuilder>(StringComparer.Ordinal);
        // Map parentToolCallId -> parent tool name for sub-agent attribution
        var parentToolNameByCallId = new Dictionary<string, string>(StringComparer.Ordinal);

        // Track which top-level turn each top-level user-message index belongs to.
        // Rule (per rubber-duck): each top-level user.message is attributed to the NEXT
        // top-level assistant.message (with non-null turnId, no parentToolCallId) in event order.
        // Multiple unattributed user messages aggregate onto that same turn.
        int pendingUserChars = 0;
        int userIdx = 0;

        for (int i = 0; i < raw.Count; i++)
        {
            var evt = raw[i];
            if (evt.Type == "user.message")
            {
                // Drain top-level user messages as we encounter them.
                while (userIdx < topLevelUserChars.Count && topLevelUserChars[userIdx].Index <= i)
                {
                    pendingUserChars += topLevelUserChars[userIdx].Chars;
                    userIdx++;
                }
            }

            if (evt.Type != "assistant.message") continue;
            var msg = evt.Data.Deserialize(CopilotJsonContext.Default.CopilotAssistantMessage);
            if (msg == null) continue;

            var isTopLevel = !string.IsNullOrEmpty(msg.TurnId) && string.IsNullOrEmpty(msg.ParentToolCallId);

            if (isTopLevel)
            {
                // Drain user messages up to this assistant.message
                while (userIdx < topLevelUserChars.Count && topLevelUserChars[userIdx].Index <= i)
                {
                    pendingUserChars += topLevelUserChars[userIdx].Chars;
                    userIdx++;
                }

                var ts = ParseTimestamp(evt.Timestamp) ?? DateTime.UtcNow;
                var calls = new List<ToolCall>();
                if (msg.ToolRequests != null)
                {
                    foreach (var req in msg.ToolRequests)
                    {
                        if (req.ToolCallId == null || req.Name == null) continue;
                        var call = BuildToolCall(req.ToolCallId, req.Name, msg.TurnId!, parentToolCallId: null,
                            toolStartByCallId, toolCompleteByCallId, dotnetCommands, bashInvocations);
                        calls.Add(call);
                        // If this is a task/explore-style sub-agent spawner, record name for later
                        parentToolNameByCallId[req.ToolCallId] = req.Name;
                    }
                }

                turns.Add(new Turn(
                    TurnId: msg.TurnId!,
                    AssistantTimestamp: ts,
                    RequestId: msg.RequestId,
                    OutputTokens: msg.OutputTokens ?? 0,
                    UserTextChars: pendingUserChars,
                    ToolCalls: calls));
                pendingUserChars = 0;
            }
            else if (!string.IsNullOrEmpty(msg.ParentToolCallId))
            {
                // Sub-agent assistant message — count its tool calls into the sub-agent bucket.
                var bucket = subAgentByParentToolCallId.TryGetValue(msg.ParentToolCallId, out var b)
                    ? b
                    : (subAgentByParentToolCallId[msg.ParentToolCallId] = new SubAgentBuilder(msg.ParentToolCallId));
                bucket.NestedAssistantMessages++;
                if (msg.ToolRequests != null)
                {
                    foreach (var req in msg.ToolRequests)
                    {
                        if (req.Name == null) continue;
                        bucket.NestedToolCalls++;
                        bucket.NestedToolNameCounts.TryGetValue(req.Name, out var c);
                        bucket.NestedToolNameCounts[req.Name] = c + 1;
                    }
                }
            }
            // Other shapes (no turnId, no parentToolCallId) are unusual; ignore.
        }

        // Drain any remaining top-level user messages onto a synthetic last-turn slot if turns exist
        if (pendingUserChars > 0 && turns.Count > 0)
        {
            // Attach trailing user text to the *last* turn we recorded, since there's no later turn.
            var last = turns[^1];
            turns[^1] = last with { UserTextChars = last.UserTextChars + pendingUserChars };
        }

        // Build sub-agent records, finalizing parent tool name from earlier tool starts.
        var subAgents = new List<SubAgentActivity>();
        foreach (var (parentId, b) in subAgentByParentToolCallId)
        {
            var parentName = parentToolNameByCallId.TryGetValue(parentId, out var n)
                ? n
                : toolStartByCallId.TryGetValue(parentId, out var s) ? s.ToolName : "?";
            subAgents.Add(new SubAgentActivity(
                ParentToolCallId: parentId,
                ParentToolName: parentName,
                NestedAssistantMessages: b.NestedAssistantMessages,
                NestedToolCalls: b.NestedToolCalls,
                NestedToolNameCounts: b.NestedToolNameCounts));
        }

        var unmatchedStarts = toolStartByCallId.Keys.Count(id => !toolCompleteByCallId.ContainsKey(id));
        var unmatchedCompletes = toolCompleteByCallId.Keys.Count(id => !toolStartByCallId.ContainsKey(id));

        var meta = new SessionMetadata(
            SessionId: sessionId,
            StartedAt: startedAt == default ? (turns.Count > 0 ? turns[0].AssistantTimestamp : DateTime.UtcNow) : startedAt,
            CopilotVersion: copilotVersion,
            Cwd: cwd,
            Models: models,
            OsUser: Environment.UserName);

        return new ParsedSession(meta, turns, subAgents, dotnetCommands, bashInvocations, unmatchedStarts, unmatchedCompletes, dotnetOutputByCallId);
    }

    private static ToolCall BuildToolCall(
        string toolCallId,
        string toolName,
        string turnId,
        string? parentToolCallId,
        Dictionary<string, ParsedToolStart> starts,
        Dictionary<string, ParsedToolComplete> completes,
        List<DotNetCommandUse> dotnetCommands,
        List<BashInvocation> bashInvocations)
    {
        starts.TryGetValue(toolCallId, out var start);
        completes.TryGetValue(toolCallId, out var complete);
        var resultChars = complete?.ResultChars ?? 0;

        // Bash classification + dotnet command extraction
        if (toolName.Equals("bash", StringComparison.OrdinalIgnoreCase) && start?.BashCommand != null)
        {
            var classification = BashCommandClassifier.Classify(start.BashCommand);
            bashInvocations.Add(new BashInvocation(
                TurnId: turnId,
                ToolCallId: toolCallId,
                ResultChars: resultChars,
                PrimaryCommand: classification.PrimaryCommand,
                Family: classification.Family));

            var matches = DotNetCommandExtractor.ExtractAll(start.BashCommand).ToList();
            var chained = matches.Count > 1;
            foreach (var (cmd, full) in matches)
            {
                dotnetCommands.Add(new DotNetCommandUse(
                    TurnId: turnId,
                    Command: cmd,
                    FullCommand: full,
                    ParentBashCallId: toolCallId,
                    ParentBashResultChars: resultChars,
                    IsChained: chained));
            }
        }

        return new ToolCall(
            ToolCallId: toolCallId,
            ToolName: toolName,
            TurnId: turnId,
            ResultChars: resultChars,
            Completed: complete != null,
            IsError: complete is { Success: false },
            DotNetCommand: null,
            ParentToolCallId: parentToolCallId);
    }

    private static List<CopilotEvent> ReadAllRawEvents(string path)
    {
        var list = new List<CopilotEvent>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            CopilotEvent? evt;
            try
            {
                evt = JsonSerializer.Deserialize(line, CopilotJsonContext.Default.CopilotEvent);
            }
            catch (JsonException)
            {
                continue;
            }
            if (evt != null) list.Add(evt);
        }
        return list;
    }

    private static DateTime? ParseTimestamp(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
            ? dt
            : null;
    }

    private sealed record ParsedToolStart(string ToolCallId, string ToolName, string? TurnId, string? BashCommand, int EventIndex);
    private sealed record ParsedToolComplete(string ToolCallId, bool Success, int ResultChars, int EventIndex);

    private sealed class SubAgentBuilder(string parentToolCallId)
    {
        public string ParentToolCallId { get; } = parentToolCallId;
        public int NestedAssistantMessages;
        public int NestedToolCalls;
        public Dictionary<string, int> NestedToolNameCounts { get; } = new(StringComparer.Ordinal);
    }
}

/// <summary>
/// The result of parsing a session — a metadata record plus turn-level data.
/// </summary>
/// <param name="DotNetOutputByToolCallId">Captured (ANSI-stripped) result content for each bash call that contained at least one dotnet invocation. Keyed by <c>toolCallId</c>. Used by <c>DotNetOutputAnalyzer</c> for repetition analysis; not persisted on the final <c>SessionAnalysis</c>.</param>
public sealed record ParsedSession(
    SessionMetadata Metadata,
    IReadOnlyList<Turn> Turns,
    IReadOnlyList<SubAgentActivity> SubAgents,
    IReadOnlyList<DotNetCommandUse> DotNetCommands,
    IReadOnlyList<BashInvocation> BashInvocations,
    int UnmatchedToolStarts,
    int UnmatchedToolCompletes,
    IReadOnlyDictionary<string, string> DotNetOutputByToolCallId
);
