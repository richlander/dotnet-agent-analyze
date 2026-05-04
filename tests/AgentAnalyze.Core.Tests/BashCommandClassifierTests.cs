using AgentAnalyze.Core.Domain;
using AgentAnalyze.Core.Parsing;

namespace AgentAnalyze.Core.Tests;

public class BashCommandClassifierTests
{
    [Theory]
    [InlineData("dotnet build", BashFamily.DotNet, "dotnet")]
    [InlineData("cd src && dotnet build", BashFamily.DotNet, "dotnet")]
    [InlineData("cd src && cd nested && dotnet test", BashFamily.DotNet, "dotnet")]
    [InlineData("DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet build", BashFamily.DotNet, "dotnet")]
    [InlineData("env FOO=bar dotnet test", BashFamily.DotNet, "dotnet")]
    [InlineData("dotnet-trace collect", BashFamily.Other, "dotnet-trace")]
    [InlineData("dotnet-inspect search Foo", BashFamily.Other, "dotnet-inspect")]
    [InlineData("/usr/local/bin/dotnet --info", BashFamily.DotNet, "dotnet")]
    [InlineData("git log --oneline | head", BashFamily.Vcs, "git")]
    [InlineData("gh pr list", BashFamily.Vcs, "gh")]
    [InlineData("npm install", BashFamily.PackageManager, "npm")]
    [InlineData("pnpm run build", BashFamily.PackageManager, "pnpm")]
    [InlineData("cargo build --release", BashFamily.PackageManager, "cargo")]
    [InlineData("make all", BashFamily.BuildTool, "make")]
    [InlineData("./gradlew test", BashFamily.BuildTool, "gradlew")]
    [InlineData("python script.py", BashFamily.Runtime, "python")]
    [InlineData("node app.js", BashFamily.Runtime, "node")]
    [InlineData("docker ps", BashFamily.Container, "docker")]
    [InlineData("kubectl get pods", BashFamily.Container, "kubectl")]
    [InlineData("aws s3 ls", BashFamily.Cloud, "aws")]
    [InlineData("terraform plan", BashFamily.Cloud, "terraform")]
    [InlineData("curl https://example.com", BashFamily.Network, "curl")]
    [InlineData("rg --type cs Foo", BashFamily.SearchUtility, "rg")]
    [InlineData("grep -r foo .", BashFamily.SearchUtility, "grep")]
    [InlineData("ls -la", BashFamily.ShellUtility, "ls")]
    [InlineData("cat README.md | jq .", BashFamily.ShellUtility, "cat")]
    public void ClassifiesPrimaryCommand(string input, BashFamily expectedFamily, string expectedCommand)
    {
        var c = BashCommandClassifier.Classify(input);
        Assert.Equal(expectedFamily, c.Family);
        Assert.Equal(expectedCommand, c.PrimaryCommand);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void EmptyOrNullIsUnknown(string? input)
    {
        var c = BashCommandClassifier.Classify(input);
        Assert.Equal(BashFamily.Unknown, c.Family);
    }

    [Fact]
    public void SkipsPureMkdirCdLeadIns()
    {
        // mkdir/cd/echo etc. are skipped; classifier looks for the meaningful command
        var c = BashCommandClassifier.Classify("mkdir -p out && cd out && cargo build");
        Assert.Equal(BashFamily.PackageManager, c.Family);
        Assert.Equal("cargo", c.PrimaryCommand);
    }
}
