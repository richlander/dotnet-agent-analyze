using AgentAnalyze.Core.Parsing;

namespace AgentAnalyze.Core.Tests;

public class DotNetOutputNormalizerTests
{
    [Theory]
    // Unix paths
    [InlineData("Build succeeded in /Users/rich/proj/foo.csproj",
                "Build succeeded in <path>")]
    [InlineData("  /tmp/x/y.dll restored",
                "  <path> restored")]
    // Windows paths
    [InlineData(@"Building C:\src\app\Foo.csproj",
                "Building <path>")]
    [InlineData(@"copy D:/build/out/bar.dll done",
                "copy <path> done")]
    // GUIDs
    [InlineData("project guid {12345678-90ab-cdef-1234-567890abcdef}",
                "project guid {<guid>}")]
    // Durations: short forms
    [InlineData("Done in 123ms",
                "Done in <dur>")]
    [InlineData("elapsed 1.23s total",
                "elapsed <dur> total")]
    [InlineData("(in 24 ms)",
                "(in <dur>)")]
    // Durations: hh:mm:ss(.ff)
    [InlineData("Time Elapsed 00:00:01.60",
                "Time Elapsed <dur>")]
    [InlineData("ran for 01:23:45",
                "ran for <dur>")]
    // Hex hashes
    [InlineData("commit abc1234 from upstream",
                "commit <hash> from upstream")]
    [InlineData("sha 0123456789abcdef end",
                "sha <hash> end")]
    // Trailing whitespace trimmed
    [InlineData("hello   ", "hello")]
    public void NormalizesVariableBitsToPlaceholders(string input, string expected)
    {
        Assert.Equal(expected, DotNetOutputNormalizer.Normalize(input));
    }

    [Theory]
    // Diagnostic IDs are signal — must survive.
    [InlineData("warning NETSDK1057: You are using a preview version of .NET.")]
    [InlineData("error CS0103: The name 'foo' does not exist in the current context")]
    [InlineData("error MSB4019: The imported project was not found.")]
    // Versions / TFMs / package names — also signal.
    [InlineData("Microsoft.NET.Sdk 8.0.100 restored")]
    [InlineData("net8.0 build for Foo.Bar")]
    [InlineData("Newtonsoft.Json 13.0.3 added")]
    public void PreservesDiagnosticAndVersionSignal(string input)
    {
        var s = DotNetOutputNormalizer.Normalize(input);
        Assert.DoesNotContain("<hash>", s);
        Assert.DoesNotContain("<guid>", s);
        Assert.DoesNotContain("<path>", s);
        Assert.DoesNotContain("<dur>", s);
    }

    [Fact]
    public void HandlesMixedReplacementsInOneLine()
    {
        // GUID + path + duration + hash all in one line — order must produce stable output.
        var input = "Restored /tmp/proj/Foo.csproj (guid 12345678-90ab-cdef-1234-567890abcdef hash deadbeef) in 123ms";
        var normalized = DotNetOutputNormalizer.Normalize(input);

        Assert.Contains("<path>", normalized);
        Assert.Contains("<guid>", normalized);
        Assert.Contains("<hash>", normalized);
        Assert.Contains("<dur>", normalized);
        // No raw variable content leaks through.
        Assert.DoesNotContain("12345678-", normalized);
        Assert.DoesNotContain("/tmp/", normalized);
        Assert.DoesNotContain("123ms", normalized);
        Assert.DoesNotContain("deadbeef", normalized);
    }

    [Fact]
    public void EmptyAndNullPassThrough()
    {
        Assert.Equal("", DotNetOutputNormalizer.Normalize(""));
        Assert.Null(DotNetOutputNormalizer.Normalize(null!));
    }

    [Fact]
    public void StripAnsiRemovesColorEscapes()
    {
        var input = "\x1B[31mError\x1B[0m: build failed";
        Assert.Equal("Error: build failed", DotNetOutputNormalizer.StripAnsi(input));
    }

    [Fact]
    public void StripAnsiPassesThroughPlainText()
    {
        Assert.Equal("plain", DotNetOutputNormalizer.StripAnsi("plain"));
        Assert.Equal("", DotNetOutputNormalizer.StripAnsi(""));
    }

    [Fact]
    public void SplitNonEmptyLinesHandlesCrlfAndBlankLines()
    {
        var input = "first\r\nsecond\n\n   \nthird\n";
        var lines = DotNetOutputNormalizer.SplitNonEmptyLines(input).ToList();
        Assert.Equal(["first", "second", "third"], lines);
    }

    [Fact]
    public void SplitNonEmptyLinesHandlesNoTrailingNewline()
    {
        var input = "alpha\nbeta";
        var lines = DotNetOutputNormalizer.SplitNonEmptyLines(input).ToList();
        Assert.Equal(["alpha", "beta"], lines);
    }

    [Fact]
    public void SplitNonEmptyLinesEmptyInputYieldsNothing()
    {
        Assert.Empty(DotNetOutputNormalizer.SplitNonEmptyLines(""));
        Assert.Empty(DotNetOutputNormalizer.SplitNonEmptyLines(null!));
    }
}
