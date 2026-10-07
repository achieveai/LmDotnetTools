namespace Reviewd.Sample;

internal sealed class CliOptions
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public string Command { get; private init; } = "";
    public bool ShowHelp { get; private init; }
    public string? ConfigPath => Get("config");
    public IReadOnlyList<string> Argv { get; private init; } = [];

    public string? Get(string name) => _values.GetValueOrDefault(name);

    public bool Has(string name) => _values.ContainsKey(name);

    public string Required(string name) =>
        Get(name) is { Length: > 0 } value ? value : throw new ArgumentException($"--{name} is required.");

    public static CliOptions Parse(string[] args)
    {
        var separator = Array.IndexOf(args, "--");
        var commandArgs = separator < 0 ? args : args[..separator];
        if (commandArgs.Length == 0 || commandArgs.Any(arg => arg is "--help" or "-h"))
        {
            return new CliOptions { ShowHelp = true };
        }

        if (commandArgs.Length < 2)
        {
            throw new ArgumentException("Specify a command. Use --help for usage.");
        }

        var command = $"{commandArgs[0]} {commandArgs[1]}";
        var allowed = command switch
        {
            "pr list" => new[] { "repo", "repos", "db" },
            "pr check" => ["repo", "repos", "db"],
            "pr pick" => ["repo", "repos", "db"],
            "review run" =>
            [
                "repo",
                "pr",
                "slot",
                "db",
                "session",
                "workspace",
                "provider",
                "mode",
                "reasoning-effort",
                "execute",
                "resume",
                "cleanup-only",
                "head",
            ],
            "review batch" =>
            [
                "selection",
                "db",
                "session",
                "workspace",
                "provider",
                "mode",
                "reasoning-effort",
                "listen",
                "callback",
                "execute",
            ],
            "review reconcile-slot" => ["repo", "pr", "db", "evidence", "execute"],
            "review capacity" => ["repos", "db", "workspace", "session", "evidence"],
            "merge resolve" => ["request", "db", "session", "workspace", "provider", "execute"],
            "merge push" => ["request", "db", "session", "workspace", "provider", "result", "execute"],
            "pr bind-thread" => ["repo", "pr", "thread", "db"],
            "workspace list" => [],
            "workspace plugins" => ["workspace"],
            "workspace create" => ["name", "directory", "marketplaces", "enabled-plugins"],
            "workspace edit" => ["workspace", "marketplaces", "enabled-plugins"],
            "workspace exec" => ["workspace", "directory", "timeout"],
            "auth serve" => ["listen", "callback"],
            "auth run" => ["listen", "callback"],
            "sandbox create" => ["workspace", "callback", "marketplaces"],
            "sandbox exec" => ["session", "directory", "timeout"],
            "agent start" =>
            [
                "workspace",
                "directory",
                "prompt",
                "provider",
                "mode",
                "wait",
                "timeout",
                "repo",
                "pr",
                "db",
            ],
            _ => throw new ArgumentException($"Unknown command '{command}'. Use --help for usage."),
        };
        var result = new CliOptions { Command = command, Argv = separator < 0 ? [] : args[(separator + 1)..] };
        for (var i = 2; i < commandArgs.Length; i++)
        {
            var arg = commandArgs[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Options must use --name value; program arguments follow --.");
            }

            var name = arg[2..];
            if (name != "config" && !allowed.Contains(name, StringComparer.Ordinal))
            {
                throw new ArgumentException($"Unknown option --{name} for {command}.");
            }

            var value = "true";
            if (name is not ("wait" or "execute" or "resume" or "cleanup-only"))
            {
                if (++i >= commandArgs.Length || commandArgs[i].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"--{name} requires a value.");
                }

                value = commandArgs[i];
            }

            if (!result._values.TryAdd(name, value))
            {
                throw new ArgumentException($"Duplicate option --{name}.");
            }
        }

        if (result.Argv.Count > 0 && command is not ("workspace exec" or "sandbox exec" or "auth run"))
        {
            throw new ArgumentException("Program arguments are only supported by workspace exec or sandbox exec.");
        }

        if (result.Get("reasoning-effort") is { } effort && effort is not ("low" or "medium" or "high" or "xhigh"))
            throw new ArgumentException("--reasoning-effort must be low, medium, high or xhigh.");

        if (command.StartsWith("pr ", StringComparison.Ordinal))
        {
            _ = PrCommands.ParseRepos(result);
        }
        if (command == "pr bind-thread")
        {
            _ = result.Required("pr");
            _ = result.Required("thread");
        }
        if (command == "review run")
        {
            _ = PrCommands.ParseRepo(result.Required("repo"));
            foreach (var name in new[] { "pr", "slot", "db", "session", "workspace", "provider" })
            {
                _ = result.Required(name);
            }
            if (!int.TryParse(result.Required("slot"), out var slot) || slot is < 0 or > 5)
            {
                throw new ArgumentException("--slot must be between 0 and 5.");
            }
        }
        if (command is "pr bind-thread" or "agent start" or "review run" && result.Has("pr"))
        {
            if (!int.TryParse(result.Required("pr"), out var prId) || prId <= 0)
            {
                throw new ArgumentException("--pr must be a positive ADO pull request ID.");
            }
            result._values["pr"] = prId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        if (command == "workspace create")
        {
            _ = result.Required("name");
        }
        if (command == "workspace edit" && !result.Has("marketplaces") && !result.Has("enabled-plugins"))
        {
            throw new ArgumentException("workspace edit requires --marketplaces or --enabled-plugins.");
        }
        if (command is "workspace exec" or "workspace edit" or "agent start")
        {
            _ = result.Required("workspace");
        }
        if (command == "sandbox create")
        {
            _ = result.Required("workspace");
            _ = result.Required("callback");
        }
        if (command == "sandbox exec")
        {
            _ = result.Required("session");
        }
        if (command is "workspace exec" or "sandbox exec" or "auth run" && result.Argv.Count == 0)
        {
            throw new ArgumentException($"{command} requires -- <program> [args...].");
        }
        if (command == "agent start")
        {
            _ = result.Required("prompt");
            _ = result.Required("provider");
            if (result.Has("repo") != result.Has("pr") || (result.Has("db") && !result.Has("repo")))
            {
                throw new ArgumentException(
                    "Agent PR binding requires --repo and --pr together; --db requires this binding."
                );
            }
            if (result.Has("repo"))
            {
                _ = PrCommands.ParseRepo(result.Required("repo"));
            }
        }
        if (result.Get("timeout") is { } timeout && (!int.TryParse(timeout, out var seconds) || seconds <= 0))
        {
            throw new ArgumentException("--timeout must be a positive number of seconds.");
        }

        return result;
    }

    public const string HelpText = """
        Reviewd — small ADO / Gateway / S2S command-line client

        pr list  (--repo <org/project/repo> | --repos <comma-separated-repos>) [--db <path>]
        pr check (--repo <org/project/repo> | --repos <comma-separated-repos>) [--db <path>]
        pr pick  (--repo <org/project/repo> | --repos <comma-separated-repos>) [--db <path>]
        pr bind-thread --repo <org/project/repo> --pr <id> --thread <conversation-id> [--db <path>]
        auth serve [--listen <url>] [--callback <gateway-reachable-url>]
        auth run [--listen <url>] [--callback <gateway-reachable-url>] -- <local-program> [args...]
        sandbox create --workspace <logical-leaf> --callback <gateway-reachable-url> [--marketplaces <csv>]
        sandbox exec --session <id> [--directory <relative-dir>] [--timeout <seconds>] -- <program> [args...]
        workspace list
        workspace plugins [--workspace <id>]
        workspace create --name <name> [--directory <leaf>] [--marketplaces <csv>] [--enabled-plugins <json>]
        workspace edit --workspace <id> [--marketplaces <csv>] [--enabled-plugins <json>]
        workspace exec --workspace <id> [--directory <relative-dir>] [--timeout <seconds>] -- <program> [args...]
        agent start --workspace <id> --provider <id> --prompt <text> [--directory <relative-dir>]
                    [--mode <id>] [--wait] [--timeout <seconds>] [--repo <org/project/repo> --pr <id> [--db <path>]]

        review run --repo <org/project/repo> --pr <id> --slot <0-5> --db <pending.db>
                   --session <sandbox-session-id> --workspace <host-workspace-id> --provider <id> [--mode <id>] [--reasoning-effort <low|medium|high|xhigh>] [--execute] [--resume] [--cleanup-only]
        review batch --selection <json> --db <pending.db> --session <id> --workspace <id> --provider <id>
                     [--mode <id>] [--reasoning-effort <low|medium|high|xhigh>] [--listen <url>] [--callback <url>] --execute
        Batch resumes its fixed selected queue; failed PRs requeue, ambiguous owners hold slots. No automatic new PR discovery.
        review capacity --repos <comma-separated-org/project/repo> --db <path> --workspace <id> --session <id>
                        --evidence <native-and-thread-quiescence.json>
        Capacity is a local read-only preview, not a slot-release or reset authorization.
        merge resolve --request <json> --db <path> --session <id> --workspace <id> --provider <id> [--execute]
        merge push --request <same-json> --db <same-path> --session <id> --workspace <id> --provider <id>
                   --result <reviewed-result-digest> [--execute]
        Merge commands preview by default. Resolution never commits or pushes; publication requires separate authorization.

        All commands accept --config <existing appsettings.json>.
        Configuration uses existing Auth, SandboxGateway and CodeReviewDaemon sections, plus environment variables.
        ADO requires Auth:TokenStoreDir pointing to your already signed-in shared OAuth cache.
        PR checks default to reviewd.db. Active PRs include drafts; activity and detected-change timestamps are distinct.
        Thread bindings are persistent; an existing different conversation is never silently replaced.
        Directory defaults to workspace root. Scripts require an interpreter, e.g. -- python3 scripts/check.py.
        Plugin JSON: null = all, [] = none, [{"marketplace":"alias","plugin":"name"}] = subset.
        Workspace edits preserve omitted settings. Enabled-plugin changes may replace active sandbox sessions.
        agent start waits for the run and all descendants by default; --wait is accepted for compatibility.
        Agent waiting has no default deadline. --timeout limits local waiting after submission.
        Command execution defaults to 120 seconds of local waiting, not the remote execution deadline.
        Ctrl+C stops local waiting; remote work may continue.
        No sign-in, workspace deletion, PR comments, or deployment is performed.
        """;
}
