using AgentAnalyze.Core.Domain;

namespace AgentAnalyze.Core.Parsing;

/// <summary>
/// Classifies a bash command string by its <em>primary</em> external command.
/// Used to break the dominant <c>bash</c> tool bucket down into "what was actually
/// being run" — the question the agent-scaffolding tool ranking can't answer.
/// </summary>
public static class BashCommandClassifier
{
    /// <summary>
    /// Returns the family + primary command for a bash command string.
    /// Skips leading <c>cd</c> / <c>env VAR=...</c> / variable-assignment prefixes.
    /// For chained commands joined by <c>&amp;&amp;</c> / <c>||</c> / <c>;</c>, classifies the
    /// first <em>non-trivial</em> command (one that's not a directory move or env setup).
    /// </summary>
    public static BashClassification Classify(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return BashClassification.Unknown;

        var segments = SplitTopLevel(command).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

        for (int i = 0; i < segments.Count; i++)
        {
            var withoutEnv = StripLeadingEnv(segments[i]);
            if (withoutEnv.Length == 0) continue;

            var firstToken = FirstToken(withoutEnv);
            if (firstToken.Length == 0) continue;

            var lower = NormalizeExecutable(firstToken);

            // Skip "trivial" lead-ins (cd / pushd / set / etc.) regardless of chain length.
            if (s_trivialLeadCommands.Contains(lower)) continue;

            // For "candidate lead-in" commands (mkdir, rm, echo, ...), skip them only
            // if more segments follow — they're likely setup for a real command.
            // If they're the LAST segment, they ARE the real command (classified as ShellUtility).
            if (s_candidateLeadIns.Contains(lower) && i < segments.Count - 1)
                continue;

            return ClassifyToken(firstToken);
        }

        return BashClassification.Unknown;
    }

    private static string NormalizeExecutable(string token)
    {
        var slash = Math.Max(token.LastIndexOf('/'), token.LastIndexOf('\\'));
        var name = slash >= 0 ? token[(slash + 1)..] : token;
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name.ToLowerInvariant();
    }

    private static BashClassification ClassifyToken(string token)
    {
        var lower = NormalizeExecutable(token);

        // Family lookup (longest-match for prefixed tools like dotnet-trace).
        if (lower == "dotnet" || lower.StartsWith("dotnet-", StringComparison.Ordinal))
            return new BashClassification(BashFamily.DotNet, lower);

        if (s_packageManager.Contains(lower))
            return new BashClassification(BashFamily.PackageManager, lower);

        if (s_buildTool.Contains(lower))
            return new BashClassification(BashFamily.BuildTool, lower);

        if (s_runtime.Contains(lower))
            return new BashClassification(BashFamily.Runtime, lower);

        if (s_vcs.Contains(lower))
            return new BashClassification(BashFamily.Vcs, lower);

        if (s_container.Contains(lower))
            return new BashClassification(BashFamily.Container, lower);

        if (s_cloud.Contains(lower))
            return new BashClassification(BashFamily.Cloud, lower);

        if (s_network.Contains(lower))
            return new BashClassification(BashFamily.Network, lower);

        if (s_searchUtil.Contains(lower))
            return new BashClassification(BashFamily.SearchUtility, lower);

        if (s_shellUtil.Contains(lower))
            return new BashClassification(BashFamily.ShellUtility, lower);

        return new BashClassification(BashFamily.Other, lower);
    }

    private static IEnumerable<string> SplitTopLevel(string s)
    {
        // Split on top-level &&, ||, ; (no quote/escape awareness — bash from agent
        // logs is overwhelmingly simple). Pipes are intentionally NOT split: a pipeline's
        // first command is still the primary one.
        var start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == ';')
            {
                yield return s[start..i];
                start = i + 1;
            }
            else if (i + 1 < s.Length && (
                (c == '&' && s[i + 1] == '&') ||
                (c == '|' && s[i + 1] == '|')))
            {
                yield return s[start..i];
                start = i + 2;
                i++;
            }
        }
        if (start < s.Length) yield return s[start..];
    }

    private static string StripLeadingEnv(string s)
    {
        // env A=1 B=2 cmd  ->  cmd
        // A=1 B=2 cmd      ->  cmd
        int i = 0;
        while (i < s.Length)
        {
            // skip whitespace
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length) break;

            // optional leading "env "
            if (i + 4 <= s.Length && s.AsSpan(i, 4).SequenceEqual("env ".AsSpan()))
            {
                i += 4;
                continue;
            }

            // VAR=VALUE token (VAR matches [A-Za-z_][A-Za-z0-9_]*=)
            int j = i;
            if (j < s.Length && (char.IsLetter(s[j]) || s[j] == '_'))
            {
                int k = j + 1;
                while (k < s.Length && (char.IsLetterOrDigit(s[k]) || s[k] == '_')) k++;
                if (k < s.Length && s[k] == '=')
                {
                    // consume up to next whitespace (or end)
                    int v = k + 1;
                    while (v < s.Length && !char.IsWhiteSpace(s[v])) v++;
                    i = v;
                    continue;
                }
            }
            break;
        }
        return s[i..].TrimStart();
    }

    private static string FirstToken(string s)
    {
        int i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        int start = i;
        while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
        return s[start..i];
    }

    private static bool IsTrivialLead(string firstToken)
        => s_trivialLeadCommands.Contains(NormalizeExecutable(firstToken));

    private static readonly HashSet<string> s_trivialLeadCommands = new(StringComparer.Ordinal)
    {
        // Pure flow-control / shell-no-op leads: always skip these when there's something after.
        // We do this even at the end of a chain — they tell us nothing.
        "cd", "pushd", "popd", "true", "false", ":", "set",
    };

    /// <summary>
    /// "Candidate lead-in" commands — common setup commands that often precede a real
    /// command in a chain. They're skipped when followed by another segment, but
    /// classified normally (ShellUtility) when they ARE the only command.
    /// </summary>
    private static readonly HashSet<string> s_candidateLeadIns = new(StringComparer.Ordinal)
    {
        "mkdir", "rm", "rmdir", "echo", "printf", "export", "unset", "touch",
    };

    private static readonly HashSet<string> s_packageManager = new(StringComparer.Ordinal)
    {
        "npm", "yarn", "pnpm", "bun", "pip", "pip3", "pipx", "uv", "poetry", "conda",
        "gem", "bundle", "bundler", "composer", "nuget", "cargo", "rustup",
        "apt", "apt-get", "dpkg", "yum", "dnf", "brew", "pacman", "apk", "snap", "flatpak",
        "choco", "winget", "scoop", "asdf",
    };

    private static readonly HashSet<string> s_buildTool = new(StringComparer.Ordinal)
    {
        "make", "cmake", "ninja", "bazel", "buck", "buck2", "pants", "gradle", "gradlew",
        "mvn", "maven", "ant", "sbt", "msbuild", "xcodebuild", "scons", "meson",
        "tsc", "webpack", "vite", "rollup", "esbuild", "swc", "turbo", "nx", "lerna", "rush",
        "rustc", "swiftc", "go", "rebar3", "mix",
    };

    private static readonly HashSet<string> s_runtime = new(StringComparer.Ordinal)
    {
        "node", "deno", "python", "python2", "python3", "ruby", "perl", "php", "java",
        "ipython", "elixir", "iex", "erl", "lua", "swift", "kotlin", "kotlinc", "clojure",
    };

    private static readonly HashSet<string> s_vcs = new(StringComparer.Ordinal)
    {
        "git", "gh", "hg", "svn", "fossil", "bzr",
    };

    private static readonly HashSet<string> s_container = new(StringComparer.Ordinal)
    {
        "docker", "podman", "buildah", "skopeo", "kubectl", "helm", "kustomize", "minikube",
        "kind", "k3s", "k3d", "compose", "docker-compose",
    };

    private static readonly HashSet<string> s_cloud = new(StringComparer.Ordinal)
    {
        "aws", "az", "gcloud", "terraform", "tofu", "pulumi", "ansible", "ansible-playbook",
        "vagrant", "packer",
    };

    private static readonly HashSet<string> s_network = new(StringComparer.Ordinal)
    {
        "curl", "wget", "ssh", "scp", "rsync", "nc", "ping", "dig", "nslookup",
    };

    private static readonly HashSet<string> s_searchUtil = new(StringComparer.Ordinal)
    {
        "grep", "rg", "ripgrep", "ag", "ack", "find", "fd", "fdfind", "tree", "locate",
    };

    private static readonly HashSet<string> s_shellUtil = new(StringComparer.Ordinal)
    {
        "ls", "cat", "head", "tail", "wc", "sort", "uniq", "cut", "awk", "sed", "tr",
        "xargs", "tee", "diff", "patch", "less", "more", "stat", "file", "which", "whereis",
        "type", "command", "test", "[",
        "tar", "zip", "unzip", "gzip", "gunzip", "bzip2", "xz", "7z",
        "mkdir", "rm", "rmdir", "chmod", "chown", "chgrp", "ln", "cp", "mv", "touch", "df", "du",
        "ps", "kill", "pgrep", "pkill", "lsof", "top", "htop", "uptime",
        "echo", "printf", "date", "sleep", "watch", "time", "timeout", "env", "printenv",
        "export", "unset",
        "jq", "yq", "xq", "base64", "md5sum", "sha1sum", "sha256sum",
        "uname", "hostname", "whoami", "id", "groups", "users",
        "history", "alias",
    };
}

/// <summary>
/// Outcome of classifying a single bash command.
/// </summary>
public sealed record BashClassification(BashFamily Family, string PrimaryCommand)
{
    public static BashClassification Unknown { get; } = new(BashFamily.Unknown, "");
}
