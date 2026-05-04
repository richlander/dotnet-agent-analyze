using AgentAnalyze.Core.Analysis;
using AgentAnalyze.Core.Domain;
using AgentAnalyze.Core.Parsing;

namespace AgentAnalyze.Core.Tests;

public class DotNetOutputAnalyzerTests
{
    [Fact]
    public void EmptyInputReturnsEmptyAnalysis()
    {
        var parsed = MakeSession([], new Dictionary<string, string>());
        var result = DotNetOutputAnalyzer.Analyze(parsed);
        Assert.Same(DotNetOutputAnalysis.Empty, result);
    }

    [Fact]
    public void SingleInvocationProducesNoBuckets()
    {
        // Below MinSupport (= 2): no buckets, no overall.
        var parsed = MakeSession(
            [DotNetUse("c1", "build", chained: false)],
            new Dictionary<string, string> { ["c1"] = "Build succeeded.\n" });

        var result = DotNetOutputAnalyzer.Analyze(parsed);
        Assert.Empty(result.PerSubcommand);
        Assert.Null(result.Overall);
        Assert.Equal(1, result.TotalDistinctInvocations);
    }

    [Fact]
    public void RepeatedLineAcrossInvocationsAppearsInBucket()
    {
        // Two builds, same boilerplate line ("Build succeeded.") + a unique path each.
        var parsed = MakeSession(
            [
                DotNetUse("c1", "build", chained: false),
                DotNetUse("c2", "build", chained: false),
            ],
            new Dictionary<string, string>
            {
                ["c1"] = "Restoring /tmp/a.csproj\nBuild succeeded.\n",
                ["c2"] = "Restoring /tmp/b.csproj\nBuild succeeded.\n",
            });

        var result = DotNetOutputAnalyzer.Analyze(parsed);
        var build = Assert.Single(result.PerSubcommand);
        Assert.Equal("build", build.Bucket);
        Assert.Equal(2, build.Invocations);

        // Both lines normalize to the same form across invocations → both repeat.
        Assert.Equal(2, build.TopRepeatedLines.Count);
        Assert.All(build.TopRepeatedLines, r =>
        {
            Assert.Equal(2, r.Support);
            Assert.Equal(2, r.TotalOccurrences);
        });
        Assert.Contains(build.TopRepeatedLines, r => r.Normalized == "Build succeeded.");
        Assert.Contains(build.TopRepeatedLines, r => r.Normalized == "Restoring <path>");
    }

    [Fact]
    public void WithinInvocationRepetitionDoesNotCountAsSupport()
    {
        // "Build succeeded." appears 10× in one invocation, 0× in the other → support = 1, dropped.
        var parsed = MakeSession(
            [
                DotNetUse("c1", "build", chained: false),
                DotNetUse("c2", "build", chained: false),
            ],
            new Dictionary<string, string>
            {
                ["c1"] = string.Concat(Enumerable.Repeat("Build succeeded.\n", 10)) + "shared line\n",
                ["c2"] = "shared line\n",
            });

        var result = DotNetOutputAnalyzer.Analyze(parsed);
        var build = Assert.Single(result.PerSubcommand);
        var line = Assert.Single(build.TopRepeatedLines);
        Assert.Equal("shared line", line.Normalized);
        Assert.Equal(2, line.Support);
        Assert.DoesNotContain(build.TopRepeatedLines, r => r.Normalized == "Build succeeded.");
    }

    [Fact]
    public void ChainedCallExcludedFromPerSubcommandButCountedInOverall()
    {
        // c1: build alone. c2: build + restore in one bash → chained.
        // c3: build alone. The chained call must NOT contribute to "build" or "restore" buckets,
        // but DOES contribute to overall support.
        var parsed = MakeSession(
            [
                DotNetUse("c1", "build", chained: false),
                DotNetUse("c2", "build", chained: true),
                DotNetUse("c2", "restore", chained: true),
                DotNetUse("c3", "build", chained: false),
            ],
            new Dictionary<string, string>
            {
                ["c1"] = "Build succeeded.\n",
                ["c2"] = "mixed output line\nBuild succeeded.\n",
                ["c3"] = "Build succeeded.\n",
            });

        var result = DotNetOutputAnalyzer.Analyze(parsed);

        // build bucket: only c1 + c3 (2 single-subcommand invocations); c2 chained excluded.
        var build = Assert.Single(result.PerSubcommand, b => b.Bucket == "build");
        Assert.Equal(2, build.Invocations);
        Assert.Equal(1, build.ExcludedChainedCount);
        Assert.True(build.ExcludedChainedChars > 0);
        var buildLine = Assert.Single(build.TopRepeatedLines);
        Assert.Equal("Build succeeded.", buildLine.Normalized);
        Assert.Equal(2, buildLine.Support);

        // restore bucket: only chained → not enough singles to form a bucket.
        Assert.DoesNotContain(result.PerSubcommand, b => b.Bucket == "restore");

        // Overall: 3 invocations, "Build succeeded." appears in all three → support 3.
        Assert.NotNull(result.Overall);
        Assert.Equal(3, result.Overall!.Invocations);
        var overallLine = result.Overall.TopRepeatedLines
            .Single(r => r.Normalized == "Build succeeded.");
        Assert.Equal(3, overallLine.Support);
        Assert.Equal(3, overallLine.TotalOccurrences);
        // Chained counts only show up on per-subcommand buckets, never on overall.
        Assert.Equal(0, result.Overall.ExcludedChainedCount);
        Assert.Equal(0, result.Overall.ExcludedChainedChars);
    }

    [Fact]
    public void SuppressibleAndRecurringCharsMatchHandComputed()
    {
        // Three identical invocations, each emitting "Build succeeded." (16 chars) twice.
        // Across the bucket: occ=6, len=16, support=3.
        //   recurring   = 6 * 16 = 96
        //   suppressible = (6 - 1) * 16 = 80
        const string Line = "Build succeeded."; // 16 chars
        var body = Line + "\n" + Line + "\n";

        var parsed = MakeSession(
            [
                DotNetUse("c1", "build", chained: false),
                DotNetUse("c2", "build", chained: false),
                DotNetUse("c3", "build", chained: false),
            ],
            new Dictionary<string, string>
            {
                ["c1"] = body, ["c2"] = body, ["c3"] = body,
            });

        var result = DotNetOutputAnalyzer.Analyze(parsed);
        var build = Assert.Single(result.PerSubcommand);
        Assert.Equal(96, build.RecurringChars);
        Assert.Equal(80, build.SuppressibleChars);

        var line = Assert.Single(build.TopRepeatedLines);
        Assert.Equal(3, line.Support);
        Assert.Equal(6, line.TotalOccurrences);
        Assert.Equal(Line.Length, line.Length);
    }

    [Fact]
    public void TopRepeatedLinesCappedAndOrderedBySuppressibleCost()
    {
        // Construct >20 distinct repeating normalized lines with varying lengths so the
        // ranking by suppressible chars (= (occ-1) * len) is exercised. Each pair of
        // invocations shares all the lines, so support = 2 and occ = 2 → suppressible = len.
        var lineA = new string('z', 100); // long, non-hex → highest suppressible
        var lines = new List<string> { lineA };
        for (int i = 0; i < 30; i++)
        {
            lines.Add($"line-{i:D2}-{new string('z', 10 + i)}"); // increasing length, non-hex
        }

        var body = string.Join('\n', lines) + "\n";
        var parsed = MakeSession(
            [
                DotNetUse("c1", "build", chained: false),
                DotNetUse("c2", "build", chained: false),
            ],
            new Dictionary<string, string> { ["c1"] = body, ["c2"] = body });

        var result = DotNetOutputAnalyzer.Analyze(parsed);
        var build = Assert.Single(result.PerSubcommand);

        Assert.Equal(DotNetOutputAnalyzer.TopLinesPerBucket, build.TopRepeatedLines.Count);
        // Rank 1 must be the longest line (lineA).
        Assert.Equal(lineA, build.TopRepeatedLines[0].Normalized);
        // Strictly non-increasing suppressible cost down the list.
        long prev = long.MaxValue;
        foreach (var r in build.TopRepeatedLines)
        {
            var sup = (long)(r.TotalOccurrences - 1) * r.Length;
            Assert.True(sup <= prev, "TopRepeatedLines must be ordered by suppressible cost desc.");
            prev = sup;
        }
    }

    [Fact]
    public void OutputForUnknownCallIdIsSkipped()
    {
        // c1 has output but no DotNetCommands entry → must not crash, must not contribute.
        // c2 is a normal pair so we have something to verify the rest of the pipeline still ran.
        var parsed = MakeSession(
            [DotNetUse("c2", "build", chained: false)],
            new Dictionary<string, string>
            {
                ["c1-orphan"] = "orphan\n",
                ["c2"] = "Build succeeded.\n",
            });

        var result = DotNetOutputAnalyzer.Analyze(parsed);
        Assert.Equal(1, result.TotalDistinctInvocations);
        Assert.Empty(result.PerSubcommand); // only one valid invocation → below MinSupport
    }

    // --- helpers ---

    private static DotNetCommandUse DotNetUse(string callId, string subcommand, bool chained) =>
        new(
            TurnId: "t1",
            Command: subcommand,
            FullCommand: $"dotnet {subcommand}",
            ParentBashCallId: callId,
            ParentBashResultChars: 0,
            IsChained: chained);

    private static ParsedSession MakeSession(
        IReadOnlyList<DotNetCommandUse> uses,
        IReadOnlyDictionary<string, string> outputByCallId)
    {
        var meta = new SessionMetadata(
            SessionId: "test",
            StartedAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CopilotVersion: null,
            Cwd: null,
            Models: [],
            OsUser: null);
        return new ParsedSession(
            Metadata: meta,
            Turns: [],
            SubAgents: [],
            DotNetCommands: uses,
            BashInvocations: [],
            UnmatchedToolStarts: 0,
            UnmatchedToolCompletes: 0,
            DotNetOutputByToolCallId: outputByCallId);
    }
}
