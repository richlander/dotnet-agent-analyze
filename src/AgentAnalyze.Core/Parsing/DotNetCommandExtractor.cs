using System.Text.RegularExpressions;

namespace AgentAnalyze.Core.Parsing;

/// <summary>
/// Extracts a normalized "dotnet &lt;subcommand&gt;" identifier from a bash command string.
/// Adapted from the AgentLogs library.
/// </summary>
public static partial class DotNetCommandExtractor
{
    /// <summary>
    /// Returns each dotnet invocation found in <paramref name="command"/> as a tuple
    /// (subcommand, full text). Subcommand is lowercase. For <c>dotnet-foo</c> tools the
    /// subcommand is the full executable name ("dotnet-trace").
    /// </summary>
    public static IEnumerable<(string Command, string FullCommand)> ExtractAll(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            yield break;

        foreach (Match match in DotNetRegex().Matches(command))
        {
            var raw = match.Value.Trim().TrimStart('&', '|', ';', ' ');
            if (raw.Length == 0)
                continue;

            var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                continue;

            var executable = parts[0].ToLowerInvariant();
            if (executable.EndsWith(".exe", StringComparison.Ordinal))
                executable = executable[..^4];

            string sub;
            if (executable.StartsWith("dotnet-", StringComparison.Ordinal))
            {
                sub = executable; // dotnet-trace, dotnet-inspect, etc.
            }
            else if (parts.Length < 2)
            {
                sub = "dotnet"; // bare invocation
            }
            else
            {
                var second = parts[1].ToLowerInvariant();
                // Treat flags like --info / --version as a subcommand of their own.
                sub = second.StartsWith('-') ? second : second;
            }

            yield return (sub, raw);
        }
    }

    // (^ or && / || / ; / |) optional path prefix, then dotnet or dotnet-*, optional .exe,
    // then the rest until the next separator.
    [GeneratedRegex(@"(?:^|&&|\|\||;|\|)\s*(?:[\w/\\.-]*?)?\bdotnet(?:-[\w]+)?(?:\.exe)?(?:\s+[^&|;]+|\s*$)",
        RegexOptions.IgnoreCase)]
    private static partial Regex DotNetRegex();
}
