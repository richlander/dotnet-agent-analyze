using AgentAnalyze.Core.Analysis;
using AgentAnalyze.Core.Parsing;
using AgentAnalyze.Core.Reporting;

namespace AgentAnalyze.Core.Tests;

public class CopilotSessionParserTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void ParsesSyntheticSessionTopLevelTurnsOnly()
    {
        var path = FixturePath("synthetic-events.jsonl");
        var parsed = CopilotSessionParser.ParseEventsFile(path, sessionDirectory: null);

        Assert.Equal("test-session-id-aaaaaaaa", parsed.Metadata.SessionId);
        Assert.Equal("1.0.40", parsed.Metadata.CopilotVersion);
        Assert.Contains("claude-test-model", parsed.Metadata.Models);

        // Two top-level turns; sub-agent assistant.message must not become a third.
        Assert.Equal(2, parsed.Turns.Count);

        // Turn 0 has the user text attributed to it.
        Assert.Equal("Please look at things and run dotnet build.".Length, parsed.Turns[0].UserTextChars);

        // Tool calls listed for the top-level turns
        Assert.Equal(2, parsed.Turns[0].ToolCalls.Count);
        Assert.Equal("bash", parsed.Turns[0].ToolCalls[0].ToolName);
        Assert.Equal("view", parsed.Turns[0].ToolCalls[1].ToolName);
        // result chars
        Assert.Equal(100, parsed.Turns[0].ToolCalls[0].ResultChars);
        Assert.Equal(4, parsed.Turns[0].ToolCalls[1].ResultChars);

        // Turn 1 has the task tool call
        Assert.Single(parsed.Turns[1].ToolCalls);
        Assert.Equal("task", parsed.Turns[1].ToolCalls[0].ToolName);
        Assert.Equal(76, parsed.Turns[1].ToolCalls[0].ResultChars);
    }

    [Fact]
    public void RecordsSubAgentActivitySeparately()
    {
        var parsed = CopilotSessionParser.ParseEventsFile(FixturePath("synthetic-events.jsonl"), null);

        var sub = Assert.Single(parsed.SubAgents);
        Assert.Equal("t3", sub.ParentToolCallId);
        Assert.Equal("task", sub.ParentToolName);
        Assert.Equal(1, sub.NestedAssistantMessages);
        Assert.Equal(2, sub.NestedToolCalls);
        Assert.Equal(1, sub.NestedToolNameCounts["grep"]);
        Assert.Equal(1, sub.NestedToolNameCounts["view"]);

        // No nested tool call should leak into top-level turn tool lists.
        var allTopLevelTools = parsed.Turns.SelectMany(t => t.ToolCalls).Select(c => c.ToolName).ToList();
        Assert.DoesNotContain("grep", allTopLevelTools);
    }

    [Fact]
    public void ExtractsDotnetCommandFromBash()
    {
        var parsed = CopilotSessionParser.ParseEventsFile(FixturePath("synthetic-events.jsonl"), null);
        var dn = Assert.Single(parsed.DotNetCommands);
        Assert.Equal("build", dn.Command);
        Assert.Equal("0", dn.TurnId);
    }
}

public class TokenAttributorTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void RanksToolsByCumulativeChars()
    {
        var parsed = CopilotSessionParser.ParseEventsFile(FixturePath("synthetic-events.jsonl"), null);
        var analysis = new Domain.SessionAnalysis(
            Metadata: parsed.Metadata,
            Turns: parsed.Turns,
            SubAgents: parsed.SubAgents,
            DotNetCommands: parsed.DotNetCommands,
            BashInvocations: parsed.BashInvocations,
            AuthoritativeTokens: [],
            UnmatchedToolStarts: parsed.UnmatchedToolStarts,
            UnmatchedToolCompletes: parsed.UnmatchedToolCompletes,
            ProcessLogCoverage: new Domain.ProcessLogCoverage(0, parsed.Turns.Count, []));

        var attribution = TokenAttributor.Compute(analysis);

        // bash has 100 chars, task has 76, view has 4 → bash > task > view
        var ranking = attribution.CumulativeToolRanking.Select(r => r.ToolName).ToList();
        Assert.Equal(["bash", "task", "view"], ranking);

        // bash family: only the bash call (dotnet build) — should be classified DotNet
        var bashFamilies = attribution.BashFamilyRanking.Select(r => r.Family).ToList();
        Assert.Contains(Domain.BashFamily.DotNet, bashFamilies);
    }

    [Fact]
    public void RendersMarkdownReportWithoutThrowing()
    {
        var parsed = CopilotSessionParser.ParseEventsFile(FixturePath("synthetic-events.jsonl"), null);
        var analysis = new Domain.SessionAnalysis(
            parsed.Metadata, parsed.Turns, parsed.SubAgents, parsed.DotNetCommands,
            parsed.BashInvocations, [], parsed.UnmatchedToolStarts, parsed.UnmatchedToolCompletes,
            new Domain.ProcessLogCoverage(0, parsed.Turns.Count, []));
        var attribution = TokenAttributor.Compute(analysis);
        var md = MarkdownReporter.Render(analysis, attribution);

        Assert.Contains("# Agent session token-attribution report", md);
        Assert.Contains("test-session-id-aaaaaaaa", md);
        Assert.Contains("External commands run via", md);
        Assert.Contains("Agent built-in tools", md);
        Assert.Contains("`bash`", md);
        // Sub-agent section must appear
        Assert.Contains("Sub-agent activity", md);
        // Privacy-sensitive things must NOT appear
        Assert.DoesNotContain("AAAAAAAAAA", md);
        Assert.DoesNotContain("Please look at things", md);
    }

    [Fact]
    public void FileNameIsDeterministicPerSession()
    {
        var parsed = CopilotSessionParser.ParseEventsFile(FixturePath("synthetic-events.jsonl"), null);
        var analysis = new Domain.SessionAnalysis(
            parsed.Metadata, parsed.Turns, parsed.SubAgents, parsed.DotNetCommands,
            parsed.BashInvocations, [], 0, 0, new Domain.ProcessLogCoverage(0, parsed.Turns.Count, []));
        var name = MarkdownReporter.FileNameFor(analysis);
        Assert.StartsWith("report-20260503-211103-test-ses", name);
        Assert.EndsWith(".md", name);
    }
}
