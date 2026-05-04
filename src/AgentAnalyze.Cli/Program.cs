using AgentAnalyze.Core.Analysis;
using AgentAnalyze.Core.Parsing;
using AgentAnalyze.Core.Reporting;

namespace AgentAnalyze.Cli;

internal static class Program
{
    private const int Ok = 0;
    private const int UsageError = 64;
    private const int NotFound = 66;

    private static int Main(string[] args)
    {
        try
        {
            var opts = ParseArgs(args);
            if (opts == null)
            {
                PrintUsage();
                return UsageError;
            }

            return opts.Mode switch
            {
                Mode.SingleSession => RunSingle(opts),
                Mode.Summary => RunSummary(opts),
                _ => UsageError,
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int RunSingle(Options opts)
    {
        var sessionDir = ResolveSessionDirectory(opts);
        if (sessionDir == null)
        {
            Console.Error.WriteLine("error: could not locate a Copilot session-state directory.");
            Console.Error.WriteLine("       try `--session <id-or-path>` or `--latest`.");
            return NotFound;
        }

        Console.Error.WriteLine($"Analyzing session: {sessionDir}");
        var analysis = SessionAnalyzer.Analyze(sessionDir, opts.LogsDirectory);
        var attribution = TokenAttributor.Compute(analysis);
        var markdown = MarkdownReporter.Render(analysis, attribution);

        var outDir = opts.OutputDirectory ?? Path.Combine(Environment.CurrentDirectory, "reports");
        Directory.CreateDirectory(outDir);
        var fileName = MarkdownReporter.FileNameFor(analysis);
        var path = Path.Combine(outDir, fileName);
        File.WriteAllText(path, markdown);
        Console.WriteLine(path);
        return Ok;
    }

    private static int RunSummary(Options opts)
    {
        var outDir = opts.OutputDirectory ?? Path.Combine(Environment.CurrentDirectory, "reports");
        Directory.CreateDirectory(outDir);
        var includeTelemetry = !opts.NoTelemetry;
        Console.Error.WriteLine($"Analyzing last {opts.Count} sessions" +
                                 (includeTelemetry ? " (with telemetry)" : " (no telemetry)") +
                                 (opts.MinTurns > 0 ? $", min-turns={opts.MinTurns}" : "") + "...");

        var summary = MultiSessionAnalyzer.AnalyzeRecent(
            count: opts.Count,
            sessionStateRoot: opts.SessionStateRoot,
            logsDirectory: opts.LogsDirectory,
            includeTelemetry: includeTelemetry,
            minTurns: opts.MinTurns);

        // Also write each per-session deep-dive so links resolve.
        // Use the same telemetry preference as the summary itself.
        if (!opts.SkipDeepDives)
        {
            ProcessLogIndex deepDiveIndex;
            if (includeTelemetry && summary.Sessions.Count > 0)
            {
                var earliest = summary.Sessions.Min(s => s.StartedAt).AddDays(-7);
                var ids = new HashSet<string>(summary.Sessions.Select(s => s.SessionId), StringComparer.Ordinal);
                deepDiveIndex = ProcessLogParser.BuildIndex(ids, opts.LogsDirectory, logModifiedAfter: earliest);
            }
            else
            {
                deepDiveIndex = new ProcessLogIndex(
                    new Dictionary<string, List<AssistantUsage>>(),
                    new Dictionary<string, HashSet<string>>(),
                    ScannedLogs: 0);
            }

            foreach (var s in summary.Sessions)
            {
                var sessDir = Path.Combine(
                    opts.SessionStateRoot ?? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".copilot", "session-state"),
                    s.SessionId);
                if (!Directory.Exists(sessDir)) continue;
                try
                {
                    var analysis = SessionAnalyzer.Analyze(sessDir, deepDiveIndex);
                    var attribution = TokenAttributor.Compute(analysis);
                    var md = MarkdownReporter.Render(analysis, attribution);
                    var fn = MarkdownReporter.FileNameFor(analysis);
                    File.WriteAllText(Path.Combine(outDir, fn), md);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    Console.Error.WriteLine($"warning: failed deep-dive for {s.SessionId[..8]}: {ex.Message}");
                }
            }
        }

        var summaryMd = MultiSessionMarkdownReporter.Render(summary);
        var summaryPath = Path.Combine(outDir, MultiSessionMarkdownReporter.FileNameFor(summary));
        File.WriteAllText(summaryPath, summaryMd);
        Console.WriteLine(summaryPath);
        return Ok;
    }

    private static string? ResolveSessionDirectory(Options o)
    {
        var stateRoot = o.SessionStateRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot", "session-state");

        if (o.Session is { } s)
        {
            if (Directory.Exists(s)) return s;
            if (File.Exists(s) && Path.GetFileName(s) == "events.jsonl")
                return Path.GetDirectoryName(s);
            var bySession = Path.Combine(stateRoot, s);
            if (Directory.Exists(bySession)) return bySession;
            return null;
        }

        if (o.UseLatest)
        {
            if (!Directory.Exists(stateRoot)) return null;
            return Directory.EnumerateDirectories(stateRoot)
                .Select(d => new { Dir = d, Events = Path.Combine(d, "events.jsonl") })
                .Where(x => File.Exists(x.Events))
                .OrderByDescending(x => File.GetLastWriteTimeUtc(x.Events))
                .Select(x => x.Dir)
                .FirstOrDefault();
        }

        return null;
    }

    private static Options? ParseArgs(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--session" or "-s":
                    if (++i >= args.Length) return null;
                    o.Session = args[i];
                    break;
                case "--latest" or "-l":
                    o.UseLatest = true;
                    break;
                case "--summary":
                    o.Mode = Mode.Summary;
                    break;
                case "-n" or "--count":
                    if (++i >= args.Length) return null;
                    if (!int.TryParse(args[i], out var n) || n <= 0) return null;
                    o.Count = n;
                    break;
                case "--min-turns":
                    if (++i >= args.Length) return null;
                    if (!int.TryParse(args[i], out var m) || m < 0) return null;
                    o.MinTurns = m;
                    break;
                case "--no-telemetry":
                    o.NoTelemetry = true;
                    break;
                case "--no-deep-dives":
                    o.SkipDeepDives = true;
                    break;
                case "--output" or "-o":
                    if (++i >= args.Length) return null;
                    o.OutputDirectory = args[i];
                    break;
                case "--logs-dir":
                    if (++i >= args.Length) return null;
                    o.LogsDirectory = args[i];
                    break;
                case "--state-dir":
                    if (++i >= args.Length) return null;
                    o.SessionStateRoot = args[i];
                    break;
                case "--help" or "-h":
                    return null;
                default:
                    Console.Error.WriteLine($"error: unrecognized argument: {args[i]}");
                    return null;
            }
        }
        if (o.Mode == Mode.SingleSession && o.Session == null && !o.UseLatest)
            return null;
        return o;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""
            agent-analyze — token attribution for AI agent session logs

            usage:
              agent-analyze (--session <id|path> | --latest) [--output <dir>] [--logs-dir <dir>]
              agent-analyze --summary [-n <N>] [--min-turns <N>] [--no-telemetry] [--no-deep-dives] [--output <dir>]

            single-session options:
              -s, --session <id|path>   session id (under ~/.copilot/session-state)
                                        or a path to the session directory or events.jsonl
              -l, --latest              analyze the most recently active Copilot session

            summary options:
                  --summary             generate a one-page summary of recent sessions
              -n, --count <N>           number of most recent sessions to include (default: 12)
                  --min-turns <N>       skip sessions with fewer than N top-level turns
                                        (default: 1; 0 includes empty/unparseable sessions)
                  --no-telemetry        skip authoritative input-token scan (faster)
                  --no-deep-dives       don't (re)write per-session reports for the summary

            common options:
              -o, --output <dir>        directory for the report(s) (default: ./reports)
                  --logs-dir <dir>      directory of process-*.log files
                                        (default: ~/.copilot/logs)
                  --state-dir <dir>     directory of session-state subdirs
                                        (default: ~/.copilot/session-state)
              -h, --help                show this help

            examples:
              agent-analyze --latest
              agent-analyze --session 48e13b3e-66f1-43e1-86f6-5725120b4ed8
              agent-analyze --summary
              agent-analyze --summary -n 24 --no-telemetry
              agent-analyze --summary --min-turns 5  # only substantive sessions
            """);
    }

    private enum Mode { SingleSession, Summary }

    private sealed class Options
    {
        public Mode Mode = Mode.SingleSession;
        public string? Session;
        public bool UseLatest;
        public int Count = 12;
        public int MinTurns = 1;
        public bool NoTelemetry;
        public bool SkipDeepDives;
        public string? OutputDirectory;
        public string? LogsDirectory;
        public string? SessionStateRoot;
    }
}
