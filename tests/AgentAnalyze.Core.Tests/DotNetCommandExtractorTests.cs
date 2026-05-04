using AgentAnalyze.Core.Parsing;

namespace AgentAnalyze.Core.Tests;

public class DotNetCommandExtractorTests
{
    [Theory]
    [InlineData("dotnet build", "build")]
    [InlineData("dotnet test --logger trx", "test")]
    [InlineData("cd src && dotnet build", "build")]
    [InlineData("dotnet run --project foo -- --bar", "run")]
    [InlineData("dotnet --info", "--info")]
    [InlineData("dotnet-trace collect", "dotnet-trace")]
    [InlineData("dotnet", "dotnet")]
    public void ExtractsSubcommand(string input, string expected)
    {
        var result = DotNetCommandExtractor.ExtractAll(input).ToList();
        Assert.NotEmpty(result);
        Assert.Equal(expected, result[0].Command);
    }

    [Theory]
    [InlineData("ls -la")]
    [InlineData("git status")]
    [InlineData("echo hello world")]
    [InlineData("")]
    public void IgnoresNonDotnet(string input)
    {
        Assert.Empty(DotNetCommandExtractor.ExtractAll(input));
    }

    [Fact]
    public void ExtractsMultipleCommandsFromChain()
    {
        var result = DotNetCommandExtractor.ExtractAll("dotnet restore && dotnet build && dotnet test").ToList();
        Assert.Equal(3, result.Count);
        Assert.Equal("restore", result[0].Command);
        Assert.Equal("build", result[1].Command);
        Assert.Equal("test", result[2].Command);
    }
}
