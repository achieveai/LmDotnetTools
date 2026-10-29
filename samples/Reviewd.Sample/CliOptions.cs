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

    /// <summary>A dollar option: null when absent, a usage error unless it is a positive number such as 6 or 0.5.</summary>
    public decimal? GetDollars(string name) =>
        Get(name) is not { } text ? null
        : decimal.TryParse(
            text,
            System.Globalization.NumberStyles.AllowDecimalPoint,
            System.Globalization.CultureInfo.InvariantCulture,
            out var dollars
        )
        && dollars > 0
            ? dollars
        : throw new ArgumentException($"--{name} must be a positive number of dollars, e.g. 6 or 0.5.");

    /// <summary>A count option: null when absent, a usage error unless it is a positive integer.</summary>
    public int? GetCount(string name) =>
        Get(name) is not { } text ? null
        : int.TryParse(
            text,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var count
        )
        && count > 0
            ? count
        : throw new ArgumentException($"--{name} must be a positive integer.");

    public static CliOptions Parse(string[] args)
    {
        var separator = Array.IndexOf(args, "--");
        var commandArgs = separator < 0 ? args : args[..separator];
        if (commandArgs.Length == 0 || commandArgs.Any(arg => arg is "--help" or "-h"))
        {
            return new CliOptions { ShowHelp = true, Command = commandArgs is ["judge", ..] ? "judge run" : "" };
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
            "pr pick" => ["repo", "repos", "db", "max-age-hours", "selection-out"],
            "review run" =>
            [
                "repo",
                "pr",
                "slot",
                "slots",
                "db",
                "session",
                "workspace",
                "provider",
                "mode",
                "reasoning-effort",
                "max-age-hours",
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
                "max-age-hours",
                "listen",
                "callback",
                "execute",
            ],
            "review reconcile-slot" => ["repo", "pr", "db", "evidence", "execute"],
            "review reclaim-slot" => ["repo", "pr", "slot", "db", "workspace", "session", "execute"],
            "review reap-slots" => ["db", "workspace", "session", "include-undated", "execute"],
            "review capacity" => ["repos", "db", "workspace", "session", "evidence"],
            "review slots" => ["repo", "repos", "db", "workspace", "slots"],
            "merge resolve" => ["request", "db", "session", "workspace", "provider", "execute"],
            "merge push" => ["request", "db", "session", "workspace", "provider", "result", "execute"],
            "pr bind-thread" => ["repo", "pr", "thread", "db"],
            "judge run" =>
            [
                "repo",
                "pr",
                "db",
                "session",
                "workspace",
                "provider",
                "reasoning-effort",
                "mode",
                "prompt-file",
                "max-usd",
                "rejudge",
                "execute",
            ],
            "judge batch" =>
            [
                "targets",
                "provider",
                "reasoning-effort",
                "max-usd",
                "max-usd-per-day",
                "avg-usd-target",
                "max-age-days",
                "limit",
                "stop-file",
                "execute",
            ],
            "judge close" => ["repo", "pr", "db", "store", "judgment", "remote", "execute"],
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
                "reasoning-effort",
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
            if (name is not ("wait" or "execute" or "resume" or "cleanup-only" or "rejudge"))
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

        if (result.Get("max-age-hours") is { } age)
        {
            if (
                !int.TryParse(
                    age,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var hours
                )
                || hours <= 0
            )
                throw new ArgumentException("--max-age-hours must be a positive integer.");
            result._values["max-age-hours"] = hours.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

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
            foreach (var name in new[] { "pr", "db", "session", "workspace", "provider" })
            {
                _ = result.Required(name);
            }
            if (result.Has("slot"))
            {
                if (!int.TryParse(result.Required("slot"), out var slot) || slot is < 0 or > 5)
                {
                    throw new ArgumentException("--slot must be between 0 and 5.");
                }
                if (result.Has("slots"))
                {
                    throw new ArgumentException("--slots applies only without --slot.");
                }
            }
            _ = ReviewSlots(result);
        }
        if (command == "review slots")
        {
            _ = PrCommands.ParseRepos(result);
            _ = result.Required("db");
            _ = result.Required("workspace");
            _ = ReviewSlots(result);
        }
        if (
            command is "pr bind-thread" or "agent start" or "review run" or "judge run" or "judge close"
            && result.Has("pr")
        )
        {
            if (!int.TryParse(result.Required("pr"), out var prId) || prId <= 0)
            {
                throw new ArgumentException("--pr must be a positive ADO pull request ID.");
            }
            result._values["pr"] = prId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        if (command == "judge run")
        {
            _ = PrCommands.ParseRepo(result.Required("repo"));
            foreach (var name in new[] { "pr", "db", "session", "workspace" })
            {
                _ = result.Required(name);
            }
        }
        if (command == "judge close")
        {
            _ = PrCommands.ParseRepo(result.Required("repo"));
            foreach (var name in new[] { "pr", "db", "store" })
            {
                _ = result.Required(name);
            }
            _ = result.GetCount("judgment");
        }
        if (command.StartsWith("judge ", StringComparison.Ordinal))
        {
            foreach (var name in new[] { "max-usd", "max-usd-per-day", "avg-usd-target" })
            {
                _ = result.GetDollars(name);
            }
        }
        if (command == "judge batch")
        {
            _ = result.Required("targets");
            _ = result.GetCount("max-age-days");
            _ = result.GetCount("limit");
            // One judge's cap above the day cap would make every start wait forever.
            var cap = result.GetDollars("max-usd") ?? JudgeRunner.DefaultMaxUsd;
            var day = result.GetDollars("max-usd-per-day") ?? JudgeBatch.DefaultMaxUsdPerDay;
            if (cap > day)
            {
                throw new ArgumentException(
                    $"--max-usd {cap.ToString(System.Globalization.CultureInfo.InvariantCulture)} (one judge's cap) is above "
                        + $"--max-usd-per-day {day.ToString(System.Globalization.CultureInfo.InvariantCulture)}; no judge could start."
                );
            }
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

    /// <summary>
    ///     The slots an automatic <c>review run</c> may try, in order: <c>--slots</c>, else the first
    ///     <paramref name="lanes"/> slots (<see cref="ReviewdConfig.LanesPerRepo"/>).
    /// </summary>
    internal static int[] ReviewSlots(CliOptions options, int lanes = 6)
    {
        if (options.Get("slots") is not { } list)
            return [.. Enumerable.Range(0, lanes)];
        var slots = list.Split(',', StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out var slot) && slot is >= 0 and <= 5 ? slot : -1)
            .ToArray();
        if (slots.Length == 0 || slots.Contains(-1) || slots.Distinct().Count() != slots.Length)
            throw new ArgumentException("--slots must be distinct comma-separated slots between 0 and 5.");
        return slots;
    }

    /// <summary>A copy with each named option set, or removed when its value is null.</summary>
    internal CliOptions With(params (string Name, string? Value)[] changes)
    {
        var copy = new CliOptions { Command = Command, Argv = Argv };
        foreach (var (name, value) in _values)
            copy._values[name] = value;
        foreach (var (name, value) in changes)
        {
            if (value is null)
                copy._values.Remove(name);
            else
                copy._values[name] = value;
        }
        return copy;
    }

    /// <summary>The help for <paramref name="command"/>: judge commands have their own page.</summary>
    public static string HelpFor(string command) => command == "judge run" ? JudgeHelpText : HelpText;

    public const string JudgeHelpText = """
        Reviewd judge run — judge one merged PR's verified review with a hosted agent

        judge run --repo <org/project/repo> --pr <id> --db <path> --session <sandbox-session-id> --workspace <host-workspace-id>
                  [--provider <id> (default gpt-6.1-sol)] [--reasoning-effort <low|medium|high|xhigh> (default xhigh)]
                  [--mode <id> (default: the configured mode)] [--prompt-file <path> (default: built-in prompt)]
                  [--max-usd <dollars> (default 15)] [--rejudge] [--execute]

        Preview (no --execute): checks provider, mode, effort and prompt size on the host, builds the judge inputs in
        the sandbox's /tmp (removed afterwards) and prints them with the action an execute would take. It starts no
        agent and writes no receipt, marker or store file.
        --execute: one judge at a time per judge lock (judge.lock; REVIEWD_JUDGE_LOCK names a lane's). Every step is saved in the receipt
        reviewd/judge-run/<repo>/<pr>/<n>; rerun the same command to resume where it stopped. A finished judgment is
        skipped. --rejudge starts a new judgment after a finished, failed or capped one, except while the last one
        still needs its sub-agent settle and post-check (a capped judgment not yet settled or without its ADO compare,
        or a failed one whose sub-agents had not settled): then those steps repeat instead (exit 1 until they pass).
        --max-usd freezes the judge when its cost reaches the cap (sub-agents already running may still spend).
        --workspace must be the workspace the review ran in; --session must be that workspace's sandbox session.

        Exit codes:
          0    completed, completed-draft, skip, or preview done
          1    failed, failed-precondition, failed-no-output, capped, refused, or still running (rerun to resume)
          2    usage error
          3    ADO threads changed while the judge ran (inspect the PR by hand)
          4    PR is not completed yet (nothing to judge; no judgment written)
          130  Ctrl+C (remote work may continue)

        judge batch --targets <file> [--provider <id>] [--reasoning-effort <level>] [--max-usd <dollars> (default 15)]
                    [--max-usd-per-day <dollars> (default 30)] [--avg-usd-target <dollars> (default 4)]
                    [--max-age-days <n> (default 7)] [--limit <n>] [--stop-file <path>] [--execute]

        Judges merged PRs with a verified review across installs, one at a time. Each targets line is
        name<TAB>db<TAB>session<TAB>workspace (# comments allowed). Receipts are the only state; each pass re-reads
        every database read-only. Order: resume judges the host already accepted, post-check unsettled capped ones,
        then new starts (newest merge first, targets taking turns) only while no judge is in flight anywhere.
        New starts obey the stop file (resume-only while it exists), --limit, the rolling 24 h cap
        (spent + --max-usd <= --max-usd-per-day; an unknown cost counts its cap; --max-usd above the day cap is a
        usage error) and the average of the last 10 finished judgments (on at 5).
        A resume keeps its receipt's provider, effort, mode and cap; one under another prompt is listed, not run.
        Preview (no --execute): database reads and host GETs only; prints the plan. --execute takes the judge
        lock (REVIEWD_JUDGE_LOCK, else judge.lock; busy: exit 0) and writes only reviewd/judge-skip/<repo>/<pr> when a PR is not completed (24 h).
        Output: markdown, then one JSON line. A broken target is skipped and listed under Needs you; while any
        target is skipped, readable ones still resume and post-check, but nothing new starts.

        judge batch exit codes:
          0    done, nothing to do, busy, in flight (resumes next run), cap or limit reached, resume-only
          1    the targets file cannot be read
          2    usage error
          3    a judge ended ado-changed (stops at once)
          5    guard stop: 2 consecutive failures, or the average is above --avg-usd-target

        judge close --repo <org/project/repo> --pr <id> --db <path> --store <store dir> [--judgment <n>]
                    [--remote <url or name> (default origin)] [--execute]

        Files one completed final judgment and merges every review round of the PR into the store's remote main as one
        commit: parents [main, round 1 tip, ...]. Without --judgment it runs
        only when exactly one completed final judgment exists; with several it lists them and refuses.
        It adds write-now lessons to the KnowledgeBase (append, with a "Seen in" line), one ledger line per miss under
        Improvements/, the PR author's review habits under DeveloperLearnings/<author>/review-behaviors.md (one marked
        section per habit; a bot or unknown author is not filed), PRs/<dir>/CLOSING.md (cost, what was added, what was
        not filed) and judgment-<n>/judgment.{json,md}.
        It fetches main and the rounds into private refs and builds the commit in a temporary index: the store's HEAD,
        index, local main and working tree never move. Preview (no --execute) builds the commit and prints it;
        --execute also pushes, never forced. It runs on the host (git through a local python3 in --store); the host's
        git credential helper does the push. It refuses a judgment whose files or inputs/packet.json changed since the
        judge. A credential-like value in the text it adds is filed as **REDACTED** (it prints the path and a count,
        not the text); a private key is refused. It first removes its own private refs and scratch left by a close
        that ended over an hour ago, and prints what it removed. When main moves between the fetch and the push, it
        fetches the new main and composes again, 3 tries in all. Once main holds every round (closed now or before), it
        deletes each round's review branch on the remote, with a lease on the round's tip: a branch that moved keeps its
        new work. A preview only lists the branches it would delete.

        judge close exit codes:
          0    preview done, closed, or already closed (main has this judgment's CLOSING.md and every round), and no
               review branch is left
          1    failed or refused (no final judgment or several, changed files or packet, PR dir changed on main,
               round tip moved, private key, ...)
          2    usage error
          6    main kept moving: it moved on all 3 tries (nothing pushed; run close again; one JSON line on stdout)
          7    closed, but a review branch is left: its delete failed, it moved, or main does not hold it (stderr says
               which and why; run close again to retry)
          130  Ctrl+C (before the commit: "Cancelled before the commit; nothing was committed or pushed.";
               during it: if the push had started it may have landed; run close again)

        All commands accept --config <existing appsettings.json>.
        """;

    public const string HelpText = """
        Reviewd — small ADO / Gateway / S2S command-line client

        pr list  (--repo <org/project/repo> | --repos <comma-separated-repos>) [--db <path>]
        pr check (--repo <org/project/repo> | --repos <comma-separated-repos>) [--db <path>]
        pr pick  (--repo <org/project/repo> | --repos <comma-separated-repos>) [--db <path>] [--max-age-hours <positive-integer>]
                 [--selection-out <file>]   (writes a review batch selection: all picked PRs, lanes 0-5)
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
                    [--mode <id>] [--reasoning-effort <low|medium|high|xhigh>] [--wait] [--timeout <seconds>] [--repo <org/project/repo> --pr <id> [--db <path>]]

        review run --repo <org/project/repo> --pr <id> [--slot <0-5> | --slots <csv>] --db <pending.db>
                   --session <sandbox-session-id> --workspace <host-workspace-id> --provider <id> [--mode <id>] [--reasoning-effort <low|medium|high|xhigh>] [--max-age-hours <positive-integer>] [--execute] [--resume] [--cleanup-only]
        Without --slot, run takes the first free slot, or one whose lease has lapsed (reclaimed like reap-slots);
        a PR that already owns a slot keeps it. No usable slot exits 3 with per-slot reasons on stderr.
        It tries --slots in order, else the first Reviewd:LanesPerRepo slots (configuration, 1-6, default 6).
        review slots (--repo <org/project/repo> | --repos <csv>) --db <path> --workspace <id> [--slots <csv>]
        Read-only: one JSON line per slot, state free | queued | owned | expired, with the owner PR and lease.
        review batch --selection <json> --db <pending.db> --session <id> --workspace <id> --provider <id>
                     [--mode <id>] [--reasoning-effort <low|medium|high|xhigh>] [--max-age-hours <positive-integer>] [--listen <url>] [--callback <url>] --execute
        Batch resumes its fixed selected queue; failed PRs requeue, ambiguous owners hold slots. No automatic new PR discovery.
        review capacity --repos <comma-separated-org/project/repo> --db <path> --workspace <id> --session <id>
                        --evidence <native-and-thread-quiescence.json>
        Capacity is a local read-only preview, not a slot-release or reset authorization.
        judge run --repo <org/project/repo> --pr <id> --db <path> --session <sandbox-session-id> --workspace <host-workspace-id>
                  [--provider <id>] [--reasoning-effort <level>] [--mode <id>] [--prompt-file <path>] [--max-usd <dollars>]
                  [--rejudge] [--execute]
        judge batch --targets <file> [--max-usd-per-day <dollars>] [--avg-usd-target <dollars>] [--limit <n>] [--execute] ...
        judge close --repo <org/project/repo> --pr <id> --db <path> --store <dir> [--judgment <n>] [--remote <url>] [--execute]
        Judges one merged PR's verified review with a hosted agent; batch picks PRs across installs; close files a judgment and merges the review into main. See judge run --help.
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
