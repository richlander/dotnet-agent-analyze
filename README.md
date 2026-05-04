# dotnet-agent-analyze

Tools for analyzing **AI agent session logs** to find which tools consume the most input tokens.

The first goal: prove (or disprove) the intuition that some developer tools — especially `dotnet build` — are big input-token wasters in agent sessions. Reports are intended to be shared, so we can compare across users and build a stronger case for tool changes.

## Currently supported

- **GitHub Copilot CLI** session logs at `~/.copilot/session-state/<sessionId>/events.jsonl`, optionally enriched with authoritative token counts from `~/.copilot/logs/process-*.log` `assistant_usage` telemetry.

Planned: Claude Code (`~/.claude/projects/...`), and similar.

## Build

```sh
dotnet build
```

Outputs land in `./artifacts/` (configured via `Directory.Build.props`).

## Use

Analyze the most recent Copilot session:

```sh
dotnet run --project src/AgentAnalyze.Cli -- --latest
```

Analyze a specific session:

```sh
dotnet run --project src/AgentAnalyze.Cli -- --session <session-id-or-path>
```

Generate a multi-session summary (default: last 12 sessions, with authoritative
telemetry, plus per-session deep-dive reports):

```sh
dotnet run --project src/AgentAnalyze.Cli -- --summary
dotnet run --project src/AgentAnalyze.Cli -- --summary -n 24
dotnet run --project src/AgentAnalyze.Cli -- --summary --no-telemetry --no-deep-dives  # fast
```

Sessions with fewer than `--min-turns` top-level turns (default: 1) are skipped.
This silently filters out older Copilot session formats that the parser reads as
empty (pre-`1.0.40`); pass `--min-turns 0` to include them anyway, or
`--min-turns 5` to focus on substantive sessions.

The Markdown reports are written to `./reports/`. The summary table links to each
session's deep-dive report so you can find the most expensive sessions and drill in.

## Library

`AgentAnalyze.Core` is the reusable library. It exposes:

- `Parsing.CopilotSessionParser` — reads `events.jsonl` into a structured session model.
- `Parsing.ProcessLogParser` — extracts `assistant_usage` telemetry blocks from process logs (single-session and pre-indexed batch modes).
- `Parsing.BashCommandClassifier` — classifies bash invocations by primary external command and family.
- `Analysis.SessionAnalyzer` — single-session analysis.
- `Analysis.MultiSessionAnalyzer` — batch analysis of recent sessions.
- `Analysis.TokenAttributor` — per-turn and cumulative token attribution by tool.
- `Reporting.MarkdownReporter` — single-session Markdown report.
- `Reporting.MultiSessionMarkdownReporter` — multi-session summary report.

## Privacy

The report contains only sizes, counts, tool names, and dotnet **subcommand keywords** (e.g. `build`, `test`). It does not include file contents, command arguments, user message text, or tool result text.
