using System.Text.RegularExpressions;

namespace AgentAnalyze.Core.Parsing;

/// <summary>
/// Normalizes a single line of dotnet CLI output by replacing the variable bits
/// (paths, durations, GUIDs, hex hashes) with placeholders so that lines whose only
/// difference is e.g. an absolute path can be grouped as identical.
/// </summary>
/// <remarks>
/// Diagnostic identifiers (e.g. <c>NETSDK1057</c>, <c>CS0103</c>, <c>MSB4019</c>),
/// package names, and version numbers are deliberately preserved — those carry signal.
/// </remarks>
public static partial class DotNetOutputNormalizer
{
    /// <summary>Strip ANSI/CSI escape sequences from <paramref name="text"/>.</summary>
    public static string StripAnsi(string text)
        => string.IsNullOrEmpty(text) ? text : AnsiRegex().Replace(text, string.Empty);

    /// <summary>
    /// Splits <paramref name="text"/> into lines on <c>\n</c>, drops trailing <c>\r</c>,
    /// and skips blank / whitespace-only lines.
    /// </summary>
    public static IEnumerable<string> SplitNonEmptyLines(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            int end = i;
            if (end > start && text[end - 1] == '\r') end--;
            if (end > start)
            {
                var line = text[start..end];
                if (HasNonWhitespace(line)) yield return line;
            }
            start = i + 1;
        }
        if (start < text.Length)
        {
            var tail = text[start..];
            if (tail.Length > 0 && tail[^1] == '\r') tail = tail[..^1];
            if (tail.Length > 0 && HasNonWhitespace(tail)) yield return tail;
        }
    }

    /// <summary>
    /// Returns the canonical form of <paramref name="line"/>:
    /// <list type="bullet">
    ///   <item>Absolute paths (Linux + Windows) → <c>&lt;path&gt;</c></item>
    ///   <item>Durations (<c>123ms</c>, <c>1.23s</c>, <c>00:00:01.60</c>) → <c>&lt;dur&gt;</c></item>
    ///   <item>GUIDs → <c>&lt;guid&gt;</c></item>
    ///   <item>Long hex hashes (≥ 7 chars, not part of a longer word) → <c>&lt;hash&gt;</c></item>
    /// </list>
    /// Trailing whitespace is trimmed.
    /// </summary>
    public static string Normalize(string line)
    {
        if (string.IsNullOrEmpty(line)) return line;
        var s = line.TrimEnd();
        // Order matters: GUIDs before hashes (or the GUID's hex run gets eaten as <hash>);
        // paths before durations (a Windows path can contain colons).
        s = GuidRegex().Replace(s, "<guid>");
        s = WindowsPathRegex().Replace(s, "<path>");
        s = UnixPathRegex().Replace(s, "<path>");
        s = ElapsedTimestampRegex().Replace(s, "<dur>");
        s = ShortDurationRegex().Replace(s, "<dur>");
        s = HexHashRegex().Replace(s, "<hash>");
        return s;
    }

    private static bool HasNonWhitespace(ReadOnlySpan<char> s)
    {
        foreach (var c in s) if (!char.IsWhiteSpace(c)) return true;
        return false;
    }

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex AnsiRegex();

    // 8-4-4-4-12 hex GUID
    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex GuidRegex();

    // /foo/bar, /foo/bar.cs, but not bare slashes or root only.
    // Match a leading slash followed by at least one path segment (non-whitespace, non-quotes/parens).
    [GeneratedRegex(@"(?<![A-Za-z0-9_./])/(?:[A-Za-z0-9_./@+~%\-]+/)*[A-Za-z0-9_.@+~%\-]+(?:\.[A-Za-z0-9]+)*")]
    private static partial Regex UnixPathRegex();

    // C:\foo\bar.cs or D:/foo/bar.cs
    [GeneratedRegex(@"\b[A-Za-z]:[\\/](?:[A-Za-z0-9_.\-+%@~]+[\\/])*[A-Za-z0-9_.\-+%@~]+(?:\.[A-Za-z0-9]+)?")]
    private static partial Regex WindowsPathRegex();

    // "Time Elapsed 00:00:01.60" / "elapsed 00:00:01.60" / bare "00:00:01.60"
    [GeneratedRegex(@"\b\d{1,2}:\d{2}:\d{2}(?:\.\d+)?\b")]
    private static partial Regex ElapsedTimestampRegex();

    // "123ms" or "1.23s" or "(in 24 ms)" — short-form durations.
    [GeneratedRegex(@"\b\d+(?:\.\d+)?\s?(?:ms|s)\b")]
    private static partial Regex ShortDurationRegex();

    // 7+ hex chars not adjacent to other word chars; catches commit-like hashes.
    [GeneratedRegex(@"(?<![A-Za-z0-9])[0-9a-fA-F]{7,}(?![A-Za-z0-9])")]
    private static partial Regex HexHashRegex();
}
