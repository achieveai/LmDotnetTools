using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.Misc.Storage;
using CodeReviewDaemon.Sample.Agents;
using Microsoft.Data.Sqlite;

namespace Reviewd.Sample;

/// <summary>
///     <c>judge batch</c>: judges merged PRs with a verified review across every install in a targets file, one at a
///     time, through <see cref="JudgeRunner" />. Receipts are the only state: each pass re-reads every target database
///     read-only, plus one host read per judge that may be running. Its only write of its own is the
///     <c>reviewd/judge-skip/{repo}/{pr}</c> marker after a PR turns out not completed (exit 4).
///     <para>
///     Order: resume judges the host has accepted (no guards; their spend is committed), then post-check unsettled
///     capped ones ($0), then, only while no judge is in flight anywhere and the stop file is absent, new starts under
///     the day cap, the average guard and --limit, taking turns across targets.
///     </para>
///     <para>With <c>--shard i/n</c> the batch sees only its share of PRs (see <see cref="Shard" />).</para>
/// </summary>
internal static class JudgeBatch
{
    internal const int GuardStopExitCode = 5;
    internal const decimal DefaultMaxUsdPerDay = 30m;
    internal const decimal DefaultAvgUsdTarget = 4m;
    internal const int DefaultMaxAgeDays = 7;
    private const string Schema = "reviewd-judge-batch/1";
    private const int AvgWindow = 10;
    private const int AvgMinimum = 5;
    private const int PlanRows = 30;
    private const int TableRows = 20;
    private const int NamesShown = 3;

    // Error text goes to the batch's stderr, which the hourly tick leaves in this journal.
    private const string Journal = "journalctl --user -u 'reviewd-judge*'";
    private const string Details = $"(details: {Journal})";
    private const string NotRetried =
        $"and is not retried; see why in {Journal}, then judge run --rejudge to judge it again";
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);
    private static readonly TimeSpan Week = TimeSpan.FromDays(7);
    private static readonly TimeSpan LongInFlight = TimeSpan.FromHours(3);

    internal sealed record Target(string Name, string Db, string Session, string Workspace);

    internal sealed record Settings(
        decimal MaxUsd,
        decimal MaxUsdPerDay,
        decimal AvgUsdTarget,
        int MaxAgeDays,
        string PromptSha256,
        Shard? Shard = null
    );

    /// <summary>
    ///     <c>--shard i/n</c>: the batch takes only the PRs whose number is i modulo n. n lanes over the same installs,
    ///     each with its own judge lock and its own i, then judge side by side: none picks another's PR or waits for
    ///     another's judge, and each lane's spend, caps and guards count its own judgments. A PR id that is not a number
    ///     goes to shard 0.
    /// </summary>
    internal sealed record Shard(int Index, int Count)
    {
        internal bool Takes(string prId) =>
            long.TryParse(prId, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? number % Count == Index
                : Index == 0;

        internal static Shard? Parse(string? value)
        {
            if (value is null)
                return null;
            var parts = value.Split('/');
            if (
                parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var count)
                && index < count
            )
                return new(index, count);
            throw new ArgumentException(
                $"--shard {value}: expected i/n with 0 <= i < n, as 0/3, 1/3 and 2/3 for three lanes."
            );
        }
    }

    /// <summary>One verified review in one target: its judge receipts (oldest first) and its snapshot facts.</summary>
    internal sealed record PrState(
        Target Target,
        string Repository,
        string Key,
        string Pr,
        IReadOnlyList<JsonObject> Judgments,
        string? Lifecycle,
        DateTimeOffset? LastChangedAt,
        DateTimeOffset? SkippedAt
    )
    {
        public JsonObject? Latest => Judgments.Count == 0 ? null : Judgments[^1];
        public string Label { get; } = $"{PrCommands.ParseRepo(Repository).RepoName} !{Pr}";

        // The label is for people; two installs may hold the same repo, so a run tracks PRs by this.
        public string Id => $"{Target.Name}/{Key}/{Pr}";
    }

    internal enum Kind
    {
        Resume,
        PostCheck,
        Start,
        Skip,
        NeedsYou,
    }

    /// <summary>
    ///     What a run does with one PR. <c>Stored</c>: run with the newest receipt's provider, effort, mode and cap.
    ///     <c>Live</c>: the host says this resume's judge is running now. It stays planned, so the batch re-attaches if
    ///     its process stopped, but the summary shows it only as running.
    /// </summary>
    internal sealed record Pick(PrState Pr, Kind Kind, string Reason, bool Stored = false, bool Live = false)
    {
        public decimal MaxUsd(Settings settings) =>
            Stored && Pr.Latest?["caps"]?["maxUsd"] is { } cap ? cap.GetValue<decimal>() : settings.MaxUsd;
    }

    /// <summary>
    ///     One judge receipt's spend. A lower bound when sub-agents may still add to it. <c>Unknown</c>: the cost
    ///     cannot be measured, so the day cap counts the run's cap, as the runner's freeze does. <c>Read</c>: the
    ///     receipt holds a usage reading.
    /// </summary>
    internal sealed record Spend(
        string Label,
        string? Thread,
        DateTimeOffset? StartedAt,
        DateTimeOffset? FinishedAt,
        decimal Usd,
        bool LowerBound,
        decimal Cap,
        bool Unknown,
        bool Read = true
    )
    {
        public decimal Charged => Unknown ? Math.Max(Usd, Cap) : Usd;
    }

    internal sealed record GuardState(decimal Spent24h, decimal? Avg10, int AvgCount);

    /// <summary>One Needs-you line in plain words: who it is about and what a person should know. Details go to stderr.</summary>
    internal sealed record Note(string Who, string What)
    {
        public override string ToString() => $"{Who}: {What}";
    }

    internal sealed class Scan
    {
        public List<Pick> Picks { get; } = [];
        public List<Spend> Spends { get; } = [];
        public List<(string Label, JsonObject Receipt)> Receipts { get; } = [];
        public List<string> InFlight { get; } = [];
        public Dictionary<string, DateTimeOffset?> LastStart { get; } = new(StringComparer.Ordinal);
        public List<Note> NeedsYou { get; } = [];

        /// <summary>
        ///     Installs that could not be read, each with one plain cause. Their judges may be in flight, so
        ///     nothing new starts.
        /// </summary>
        public List<(string Name, string Reason)> Skipped { get; } = [];
        public SortedDictionary<string, int> Counts { get; } = new(StringComparer.Ordinal);
        public GuardState Guard { get; set; } = new(0m, null, 0);

        /// <summary>The HTTP status (401 or 403) when the host refused the batch's sign-in, else null.</summary>
        public int? SignInRefused { get; set; }
    }

    private enum Outcome
    {
        Done,
        Failed,
        InFlight,
        AdoChanged,
        NotCompleted,
        Refused,
    }

    public static async Task<int> RunAsync(CliOptions options, ReviewdConfig config, CancellationToken ct)
    {
        var execute = options.Has("execute");
        var targetsPath = options.Required("targets");
        string[] lines;
        try
        {
            lines = await File.ReadAllLinesAsync(targetsPath, ct);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"judge batch: cannot read --targets {targetsPath}: {failure.Message}");
            return 1;
        }
        var settings = new Settings(
            options.GetDollars("max-usd") ?? JudgeRunner.DefaultMaxUsd,
            options.GetDollars("max-usd-per-day") ?? DefaultMaxUsdPerDay,
            options.GetDollars("avg-usd-target") ?? DefaultAvgUsdTarget,
            options.GetCount("max-age-days") ?? DefaultMaxAgeDays,
            await JudgeRunner.EmbeddedPromptSha256Async(ct),
            Shard.Parse(options.Get("shard"))
        );
        var limit = options.GetCount("limit") ?? int.MaxValue;
        var stopFile = options.Get("stop-file");
        var (targets, broken) = ParseTargets(lines);
        foreach (var (name, reason) in broken)
            Console.Error.WriteLine($"judge batch: target {name} skipped: {reason}");

        FileStream? judgeLock = null;
        if (execute && (judgeLock = JudgeRunner.TryAcquireJudgeLock(JudgeRunner.JudgeLockPath, out var held)) is null)
        {
            Console.WriteLine(
                $"**Status:** busy: another judge holds {JudgeRunner.JudgeLockPath} ({held}). Nothing started."
            );
            Console.WriteLine(
                new JsonObject
                {
                    ["schema"] = Schema,
                    ["status"] = "busy",
                    ["exit"] = 0,
                }.ToJsonString()
            );
            return 0;
        }
        using var lockHandle = judgeLock;
        var client = config.CreateS2SClient();
        var actions = new List<JsonObject>();
        var runNeedsYou = new List<Note>();
        var tried = new HashSet<string>(StringComparer.Ordinal);
        var (started, failures, exit, status) = (0, 0, 0, "");
        string? cause = null;
        Scan scan;
        List<(Pick Pick, string? Wait)> plan;
        bool latched;
        while (true)
        {
            scan = await ScanAsync(targets, broken, client, settings, DateTimeOffset.UtcNow, ct);
            latched = stopFile is not null && File.Exists(stopFile);
            plan = Plan(scan, tried, latched, started, limit, settings, DateTimeOffset.UtcNow);
            if (!execute || status != "")
                break;
            if (plan.FirstOrDefault(entry => entry.Wait is null).Pick is not { } pick)
                break;
            tried.Add(pick.Pr.Id);
            if (pick.Kind == Kind.Start)
                started++;
            var (outcome, detail, after) = await RunPickAsync(pick, settings, options, config, client, ct);
            actions.Add(
                new JsonObject
                {
                    ["pr"] = pick.Pr.Label,
                    ["kind"] = pick.Kind.ToString(),
                    ["reason"] = pick.Reason,
                    ["outcome"] = outcome.ToString(),
                    ["detail"] = detail,
                    ["usd"] = after?["judgeUsage"]?["usd"]?.DeepClone(),
                }
            );
            Console.Error.WriteLine($"judge batch: {pick.Pr.Label}: {outcome}: {detail}");
            switch (outcome)
            {
                case Outcome.AdoChanged:
                    (exit, status, cause) = (
                        JudgeRunner.AdoChangedExitCode,
                        $"paused: {pick.Pr.Label} changed on ADO while it was judged; check it by hand",
                        "a PR changed on ADO while it was judged"
                    );
                    break;
                case Outcome.InFlight:
                    status = $"a judge is still running on {pick.Pr.Label}; it resumes next run";
                    break;
                case Outcome.Failed:
                    if (++failures >= 2)
                        (exit, status, cause) = (
                            GuardStopExitCode,
                            "paused: 2 judgments in a row failed",
                            "2 judgments in a row failed"
                        );
                    break;
                case Outcome.Refused:
                    runNeedsYou.Add(new(pick.Pr.Label, $"its judge could not be resumed {Details}"));
                    break;
                case Outcome.Done:
                case Outcome.NotCompleted:
                    failures = 0;
                    break;
                default:
                    throw new InvalidOperationException($"Unknown pick outcome {outcome}.");
            }
        }
        (exit, status, cause) = Conclude(scan, plan, settings, stopFile, latched, (exit, status, cause));
        Console.Write(
            Summary(scan, plan, actions, runNeedsYou, settings, execute, status, exit, DateTimeOffset.UtcNow, cause)
        );
        return exit;
    }

    /// <summary>
    ///     The run's exit, its status line and the short cause of a pause (the tick's alert title), from what the run
    ///     did (<paramref name="run" />) and what the last scan and plan show.
    /// </summary>
    internal static (int Exit, string Status, string? Cause) Conclude(
        Scan scan,
        List<(Pick Pick, string? Wait)> plan,
        Settings settings,
        string? stopFile,
        bool latched,
        (int Exit, string Status, string? Cause) run
    )
    {
        var (exit, status, cause) = run;
        if (status == "" && !latched && scan.Guard.Avg10 > settings.AvgUsdTarget)
            (exit, status, cause) = (
                GuardStopExitCode,
                $"paused: the average cost {Usd(scan.Guard.Avg10)} over the last {scan.Guard.AvgCount} judgments is above the {Usd(settings.AvgUsdTarget)} limit",
                "average cost too high"
            );
        // The stop file's first line says when and why (the tick writes it); the status says how to undo it.
        if (status == "" && latched)
            status =
                $"paused{StopReason(stopFile!)}. Judges already running still finish. To start new judges again, run: rm {stopFile}";
        List<string> parts = status == "" ? [] : [status];
        // A host that refuses the batch's sign-in is never "ok"; Needs you says what to check.
        if (scan.SignInRefused is { } code)
            parts.Add($"the host refused the batch's sign-in (HTTP {code}); see Needs you");
        if (scan.Skipped.Count > 0)
            parts.Add(
                parts.Count == 0
                    ? $"paused: {Plural(scan.Skipped.Count, "install")} unusable (see Needs you); judges already running still finish"
                    : $"new judges paused: {Plural(scan.Skipped.Count, "install")} unusable (see Needs you)"
            );
        // A preview counts each judge at its cap, so this is room left, not money spent. Never "ok" while the cap lets
        // no new judge start, whatever else holds the next one (a judge in flight hides the cap wait).
        var room = plan.Count(entry => entry.Pick.Kind == Kind.Start && entry.Wait is null);
        var capWait = plan.Any(entry => entry.Wait == "cap");
        var next = room == 0 ? plan.FirstOrDefault(entry => entry.Pick.Kind == Kind.Start).Pick : null;
        if (
            scan.Guard.Spent24h >= settings.MaxUsdPerDay
            || (next is not null && WithStart(scan, next, scan.Guard.Spent24h, settings) > settings.MaxUsdPerDay)
        )
            parts.Add($"{(parts.Count == 0 ? "waiting: " : "")}no room under the daily cap for another new judge");
        status = parts.Count == 0 ? "ok" : string.Join(" · ", parts);
        if (capWait && room > 0)
            status += $" · the daily cap leaves room for {Plural(room, "more new judge")}";
        if (plan.Any(entry => entry.Wait == "limit"))
            status += " · this run's --limit reached";
        return (exit, status, cause);
    }

    // ---- targets and selection

    /// <summary>
    ///     One target per line: <c>name&lt;TAB&gt;db&lt;TAB&gt;session&lt;TAB&gt;workspace</c>. A line
    ///     <c>name&lt;TAB&gt;reason</c> records an install that could not be listed (the tick writes these); like any
    ///     unreadable line it is a skipped target.
    /// </summary>
    internal static (List<Target> Targets, List<(string Name, string Reason)> Broken) ParseTargets(
        IEnumerable<string> lines
    )
    {
        var (targets, broken, number) = (new List<Target>(), new List<(string, string)>(), 0);
        foreach (var line in lines)
        {
            number++;
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#'))
                continue;
            var fields = line.Split('\t').Select(field => field.Trim()).ToArray();
            if (fields is [{ Length: > 0 } name, { Length: > 0 } reason])
                broken.Add((name, reason));
            else if (fields.Length != 4 || fields.Any(field => field.Length == 0))
                broken.Add(($"on targets line {number}", "expected name<TAB>db<TAB>session<TAB>workspace"));
            else if (targets.Any(target => target.Name == fields[0]))
                broken.Add((fields[0], $"targets line {number} repeats this name"));
            else
                targets.Add(new Target(fields[0], fields[1], fields[2], fields[3]));
        }
        return (targets, broken);
    }

    /// <summary>
    ///     The selection table (the README's "Selection" table). <c>inFlight</c>: the host says the
    ///     newest receipt's judge run is active.
    /// </summary>
    internal static Pick Classify(PrState pr, bool inFlight, DateTimeOffset now, Settings settings)
    {
        var latest = pr.Latest;
        if (latest is null)
        {
            if (pr.SkippedAt is { } skipped && now - skipped < Day)
                return new(pr, Kind.Skip, "skip-cooling");
            return pr.Lifecycle switch
            {
                null => new(pr, Kind.Skip, "not-in-snapshot"),
                "Merged"
                    when pr.LastChangedAt is { } changed && now - changed <= TimeSpan.FromDays(settings.MaxAgeDays) =>
                    new(pr, Kind.Start, "new"),
                "Merged" => new(pr, Kind.Skip, "too-old"),
                _ => new(pr, Kind.Skip, pr.Lifecycle.ToLowerInvariant()),
            };
        }
        var n = latest["n"]?.ToJsonString();
        var stage = latest["stage"]?.GetValue<string>() ?? "";
        if (JudgeRunner.ResumableStages.Contains(stage))
        {
            var ours =
                latest["identity"]?["promptTemplateSha256"]?.GetValue<string>() == settings.PromptSha256
                && latest["caps"]?["maxUsd"] is not null;
            // A live run the batch cannot resume (another prompt, no stored cap) is only "Running now".
            if (inFlight && !ours)
                return new(pr, Kind.Skip, "running");
            if (latest["identity"]?["promptTemplateSha256"]?.GetValue<string>() != settings.PromptSha256)
                return new(
                    pr,
                    Kind.NeedsYou,
                    $"judgment {n} stopped in {StageWords(stage)} under another prompt; resume it by hand with the original prompt"
                );
            if (latest["caps"]?["maxUsd"] is null)
                return new(
                    pr,
                    Kind.NeedsYou,
                    $"judgment {n} stopped in {StageWords(stage)} with no stored cap; resume it by hand"
                );
            // Only a judge the host has accepted skips the guards; before its send it is a new start.
            var accepted =
                latest["judgeThreadId"] is not null && (inFlight || stage is not ("thread-created" or "send-pending"));
            return new(
                pr,
                accepted ? Kind.Resume : Kind.Start,
                $"resume judgment {n} from {StageWords(stage)}",
                Stored: true,
                Live: inFlight
            );
        }
        if (JudgeRunner.NeedsPostCheck(latest))
            return ReCheckDue(latest, now)
                ? new(pr, Kind.PostCheck, $"post-check {stage} judgment {n}", Stored: true)
                : new(
                    pr,
                    Kind.NeedsYou,
                    stage == "failed"
                        ? $"failed judgment {n} still has unsettled sub-agents 24 h after it started"
                        : $"capped judgment {n} is still unsettled 24 h after its freeze"
                );
        if (stage != "failed-precondition")
            return new(pr, Kind.Skip, "judged");
        var preconditions = pr.Judgments.Count(j => j["stage"]?.GetValue<string>() == "failed-precondition");
        if (preconditions >= 2)
            return new(
                pr,
                Kind.NeedsYou,
                $"{preconditions} judgments could not start because the host could not create the judge conversation "
                    + $"{Details}; fix that, then judge run by hand"
            );
        return Time(latest["finishedAt"]) is { } finished && now - finished >= Day
            ? new(pr, Kind.Start, $"retry judgment {n}, which could not start")
            : new(pr, Kind.Skip, "precondition-cooling");
    }

    /// <summary>
    ///     Rolling 24 h spend (an unknown cost counts its cap), and the mean of the last 10 finished judgments that had
    ///     a thread at their known reading (off below 5).
    /// </summary>
    internal static GuardState Guards(IReadOnlyList<Spend> spends, DateTimeOffset now)
    {
        var threaded = spends.Where(spend => spend.Thread is not null).ToList();
        var last = LastFinished(threaded);
        return new(
            threaded.Where(spend => InDay(spend, now)).Sum(spend => spend.Charged),
            last.Count >= AvgMinimum ? Math.Round(last.Average(spend => spend.Usd), 2) : null,
            last.Count
        );
    }

    private static bool InDay(Spend spend, DateTimeOffset now) =>
        spend.FinishedAt is null || now - spend.FinishedAt < Day || now - spend.StartedAt < Day;

    private static List<Spend> LastFinished(IEnumerable<Spend> threaded) =>
        [
            .. threaded
                .Where(spend => spend.FinishedAt is not null)
                .OrderByDescending(spend => spend.FinishedAt)
                .Take(AvgWindow),
        ];

    internal static Spend SpendOf(string label, JsonObject receipt, decimal defaultCap)
    {
        var usage = receipt["judgeUsage"];
        var complete = JudgeRunner.IsComplete(usage?["completeness"]);
        var lowerBound =
            !complete
            || usage?["measured"]?.GetValue<bool>() != true
            || receipt["stage"]?.GetValue<string>() == "capped"
            || receipt["descendantsSettled"]?.GetValue<bool>() == false
            || receipt["finishedAt"] is null;
        return new(
            label,
            receipt["judgeThreadId"]?.GetValue<string>(),
            Time(receipt["startedAt"]),
            Time(receipt["finishedAt"]),
            usage?["usd"]?.GetValue<decimal>() ?? 0m,
            lowerBound,
            receipt["caps"]?["maxUsd"]?.GetValue<decimal>() ?? defaultCap,
            usage?["measured"]?.GetValue<bool>() != true || usage["usd"] is null,
            usage is not null
        );
    }

    /// <summary>
    ///     Round robin derived from receipts: the target whose latest judge start is oldest goes next (never judged
    ///     first, ties by name); within a target, stored resumes first (oldest start), then the newest change.
    /// </summary>
    internal static IEnumerable<Pick> TakingTurns(
        IEnumerable<Pick> picks,
        IReadOnlyDictionary<string, DateTimeOffset?> lastStart,
        DateTimeOffset now
    )
    {
        var queues = picks
            .GroupBy(pick => pick.Pr.Target.Name)
            .ToDictionary(
                group => group.Key,
                group => new Queue<Pick>(
                    group
                        .OrderByDescending(pick => pick.Stored)
                        .ThenBy(pick => pick.Stored ? Time(pick.Pr.Latest?["startedAt"]) : null)
                        .ThenByDescending(pick => pick.Pr.LastChangedAt)
                )
            );
        var turn = queues.Keys.ToDictionary(
            name => name,
            name => lastStart.GetValueOrDefault(name) ?? DateTimeOffset.MinValue
        );
        for (var taken = 1; queues.Count > 0; taken++)
        {
            var name = queues.Keys.OrderBy(key => turn[key]).ThenBy(key => key, StringComparer.Ordinal).First();
            yield return queues[name].Dequeue();
            if (queues[name].Count == 0)
                queues.Remove(name);
            turn[name] = now.AddTicks(taken);
        }
    }

    internal static List<(Pick Pick, string? Wait)> Plan(
        Scan scan,
        HashSet<string> tried,
        bool latched,
        int started,
        int limit,
        Settings settings,
        DateTimeOffset now
    )
    {
        var open = scan.Picks.Where(pick => !tried.Contains(pick.Pr.Id)).ToList();
        var plan = open.Where(pick => pick.Kind == Kind.Resume)
            .OrderBy(pick => Time(pick.Pr.Latest!["startedAt"]))
            .Concat(open.Where(pick => pick.Kind == Kind.PostCheck))
            .Select(pick => (pick, (string?)null))
            .ToList();
        var spent = scan.Guard.Spent24h;
        foreach (var pick in TakingTurns(open.Where(pick => pick.Kind == Kind.Start), scan.LastStart, now))
        {
            var wait =
                latched ? "latched"
                : scan.Skipped.Count > 0
                    ? $"waits: {(scan.Skipped.Count == 1 ? "install" : "installs")} {string.Join(", ", scan.Skipped.Select(skip => skip.Name))} unusable"
                : scan.InFlight.Count > 0 ? $"waits: judge in flight on {scan.InFlight[0]}"
                : scan.Guard.Avg10 > settings.AvgUsdTarget ? "average guard"
                : started >= limit ? "limit"
                : WithStart(scan, pick, spent, settings) > settings.MaxUsdPerDay ? "cap"
                : null;
            if (wait is null)
                (spent, started) = (WithStart(scan, pick, spent, settings), started + 1);
            plan.Add((pick, wait));
        }
        return plan;
    }

    /// <summary>
    ///     The day's spend once <paramref name="pick" /> starts: <paramref name="spent" /> plus its cap. A stored start
    ///     resumes a judgment whose unfinished spend <paramref name="spent" /> already holds; its cap bounds that
    ///     judgment as a whole, so that spend counts once, not again.
    /// </summary>
    internal static decimal WithStart(Scan scan, Pick pick, decimal spent, Settings settings)
    {
        var thread = pick.Stored ? pick.Pr.Latest?["judgeThreadId"]?.GetValue<string>() : null;
        var own = scan
            .Spends.Where(spend => thread is not null && spend.Thread == thread && spend.FinishedAt is null)
            .Sum(spend => spend.Charged);
        return spent - own + Math.Max(own, pick.MaxUsd(settings));
    }

    // ---- reads (read-only database opens, host GETs)

    internal static async Task<Scan> ScanAsync(
        List<Target> targets,
        List<(string Name, string Reason)> broken,
        LmStreamingS2SClient client,
        Settings settings,
        DateTimeOffset now,
        CancellationToken ct
    )
    {
        var scan = new Scan();
        scan.Skipped.AddRange(broken);
        foreach (var target in targets)
        {
            try
            {
                await ScanTargetAsync(scan, target, client, settings, now, ct);
            }
            catch (Exception failure) when (failure is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // One broken install must not stop resumes on the others. It may hide a judge in
                // flight, so it blocks new starts (Plan).
                scan.Skipped.Add((target.Name, PlainCause(failure)));
                Console.Error.WriteLine($"judge batch: target {target.Name} ({target.Db}) skipped: {failure.Message}");
            }
        }
        scan.NeedsYou.InsertRange(
            0,
            scan.Skipped.Select(skip => new Note($"install {skip.Name}", $"unusable because {skip.Reason}"))
        );
        // The receipt's reading is a lower bound while sub-agents may still spend; re-read those in a guard window.
        var windows = LastFinished(scan.Spends.Where(spend => spend.Thread is not null)).ToHashSet();
        var refusedReads = new HashSet<int>();
        for (var i = 0; i < scan.Spends.Count; i++)
        {
            var spend = scan.Spends[i];
            if (spend.Thread is null || !spend.LowerBound || !(InDay(spend, now) || windows.Contains(spend)))
                continue;
            JsonObject live;
            try
            {
                live = await JudgeRunner.ReadUsageAsync(client, spend.Thread, ct);
            }
            catch (Exception failure) when (failure is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Console.Error.WriteLine($"judge batch: {spend.Label}: live /usage read failed: {failure.Message}");
                scan.Spends[i] = spend with { Unknown = true };
                if (Refused(scan, failure))
                    refusedReads.Add(i);
                continue;
            }
            if (live["aggregate"] is null)
            {
                Console.Error.WriteLine($"judge batch: {spend.Label}: the host has no usage for {spend.Thread}");
                // With no reading of its own, the thread has spent nothing (a send that never landed). With one, the
                // host lost its record and the reading stands.
                if (!spend.Read)
                    scan.Spends[i] = spend with { Unknown = false };
                else if (!spend.Unknown)
                    scan.NeedsYou.Add(new(spend.Label, "cost may be higher than shown; the host has no cost record"));
                continue;
            }
            var measured = live["measured"]!.GetValue<bool>();
            scan.Spends[i] = spend with
            {
                Usd = Math.Max(spend.Usd, live["usd"]?.GetValue<decimal>() ?? 0m),
                LowerBound = !JudgeRunner.IsComplete(live["completeness"]),
                Unknown = !measured,
            };
            if (!measured)
                Console.Error.WriteLine($"judge batch: {spend.Label}: the host cannot price its model");
        }
        // Every cost the day cap charges at the cap says so, whatever made it unknown; a refused sign-in says
        // it once for all.
        scan.NeedsYou.AddRange(
            scan.Spends.Where(
                    (spend, i) =>
                        spend.Thread is not null && spend.Unknown && InDay(spend, now) && !refusedReads.Contains(i)
                )
                .Select(spend => new Note(
                    spend.Label,
                    $"cost not known; until it is, the daily cap counts its cost cap ({Usd(spend.Charged)})"
                ))
        );
        if (scan.SignInRefused is { } code)
            scan.NeedsYou.Insert(
                0,
                new(
                    "the host",
                    $"refused the batch's sign-in (HTTP {code}), so judges count as running and costs at their caps; "
                        + "check the batch's credentials (Nova's bin/env.sh loads them)"
                )
            );
        scan.Guard = Guards(scan.Spends, now);
        return scan;
    }

    private static async Task ScanTargetAsync(
        Scan scan,
        Target target,
        LmStreamingS2SClient client,
        Settings settings,
        DateTimeOffset now,
        CancellationToken ct
    )
    {
        using var connection = Open(target.Db, SqliteOpenMode.ReadOnly);
        await connection.OpenAsync(ct);
        // A shard lane reads only its own PRs (the last segment of a key or group is the PR id).
        bool Mine(string key) => settings.Shard?.Takes(key[(key.LastIndexOf('/') + 1)..]) ?? true;
        var judgments = (await RowsAsync(connection, "reviewd/judge-run/", ct))
            .GroupBy(row => row.Key[..(row.Key.LastIndexOf('/') + 1)], StringComparer.Ordinal)
            .Where(prefix => Mine(prefix.Key[..^1]))
            .ToDictionary(
                prefix => prefix.Key["reviewd/judge-run/".Length..^1],
                prefix => Judgments(prefix, prefix.Key),
                StringComparer.Ordinal
            );
        var snapshots = (await RowsAsync(connection, "reviewd/pr-snapshot/", ct)).ToDictionary(
            row => row.Key["reviewd/pr-snapshot/".Length..],
            row => Row(row.Key, row.Value),
            StringComparer.Ordinal
        );
        var skips = (await RowsAsync(connection, "reviewd/judge-skip/", ct))
            .Where(row => Mine(row.Key))
            .ToDictionary(
                row => row.Key["reviewd/judge-skip/".Length..],
                row => Time(Row(row.Key, row.Value)["at"]),
                StringComparer.Ordinal
            );

        var reviews = (await ReviewsAsync(connection, ct)).Where(review => Mine(review.PrId)).ToList();
        // A PR reads as its repo's short name (NovaClient !5764750); one install reviews several repos.
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var review in reviews)
            labels.TryAdd(
                $"{PrCommands.ParseRepo(review.Repository).NormalizedKey}/{review.PrId}",
                $"{PrCommands.ParseRepo(review.Repository).RepoName} !{review.PrId}"
            );
        string LabelOf(string group) =>
            labels.GetValueOrDefault(group) ?? $"{group.Split('/')[^2]} !{group.Split('/')[^1]}";

        var inFlight = new HashSet<string>(StringComparer.Ordinal);
        scan.LastStart[target.Name] = null;
        foreach (var (group, byN) in judgments)
        {
            var label = LabelOf(group);
            foreach (var receipt in byN.Values)
            {
                scan.Spends.Add(SpendOf($"{label} #{receipt["n"]?.ToJsonString()}", receipt, settings.MaxUsd));
                scan.Receipts.Add((label, receipt));
                if (Time(receipt["startedAt"]) is { } start && !(scan.LastStart[target.Name] >= start))
                    scan.LastStart[target.Name] = start;
            }
            var latest = byN.Values.Last();
            var stage = latest["stage"]?.GetValue<string>() ?? "";
            if (stage is "ado-changed" or "failed" or "failed-no-output" && now - Time(latest["finishedAt"]) < Week)
            {
                scan.NeedsYou.Add(
                    new(
                        label,
                        stage switch
                        {
                            // The plan re-checks it; the --rejudge advice waits until it has settled.
                            "failed" when ReCheckDue(latest, now) =>
                                "last judgment failed while its sub-agents still ran; re-check pending",
                            // Unsettled after 24 h: judge run repeats the settle and post-check before any --rejudge.
                            "failed" when JudgeRunner.NeedsPostCheck(latest) =>
                                $"last judgment failed and is not retried; see why in {Journal}, then judge run until its "
                                    + "sub-agents settle, then judge run --rejudge to judge it again",
                            "failed" => $"last judgment failed {NotRetried}",
                            "failed-no-output" => $"last judgment failed with no output {NotRetried}",
                            _ => "last judgment stopped because the PR changed on ADO while it was judged",
                        }
                    )
                );
                // The note points at the journal; the receipt's error text goes there.
                if (latest["error"]?.GetValue<string>() is { } why)
                    Console.Error.WriteLine($"judge batch: {label}: judgment {latest["n"]} {stage}: {why}");
            }
            if (latest["judgeThreadId"]?.GetValue<string>() is not { } thread)
                continue;
            if (
                !JudgeRunner.ResumableStages.Contains(stage)
                && SettleFrom(latest) is { } since
                && now - since < Day
                && latest["descendantsSettled"]?.GetValue<bool>() != true
            )
            {
                // A frozen judge whose sub-agents (or a pending spawn) still run is in flight, and so is a failed
                // one whose sub-agents had not settled.
                var frozen = latest["frozenAt"] is not null;
                var which = frozen ? "capped" : "failed";
                var (settling, unreadable) = await HostReadAsync(
                    () => JudgeRunner.StillRunningAsync(client, thread, frozen, ct),
                    ct
                );
                if (unreadable is not null)
                {
                    Console.Error.WriteLine(
                        $"judge batch: {label}: settle state of {thread} unreadable: {unreadable.Message}"
                    );
                    if (!Refused(scan, unreadable))
                        scan.NeedsYou.Add(
                            new(
                                label,
                                $"the host did not say whether its {which} judge has settled; treated as running"
                            )
                        );
                }
                if (settling is not null)
                {
                    Console.Error.WriteLine($"judge batch: {label}: {which} judge {thread} still settling: {settling}");
                    scan.InFlight.Add($"{label} ({which} judge still settling)");
                }
                continue;
            }
            if (!JudgeRunner.ResumableStages.Contains(stage))
                continue;
            var (active, error) = await HostReadAsync(() => RootRunAsync(client, thread, ct), ct);
            if (error is not null)
            {
                Console.Error.WriteLine($"judge batch: {label}: run state of {thread} unreadable: {error.Message}");
                if (!Refused(scan, error))
                    scan.NeedsYou.Add(
                        new(label, "the host did not say whether its judge is still running; treated as running")
                    );
            }
            if (active is null)
                continue;
            inFlight.Add(group);
            scan.InFlight.Add(label);
            if (now - Time(latest["startedAt"]) > LongInFlight)
            {
                Console.Error.WriteLine($"judge batch: {label}: judge {thread} running for more than 3 h");
                scan.NeedsYou.Add(new(label, "its judge has been running for more than 3 hours"));
            }
        }
        foreach (var (group, at) in skips)
        {
            if (now - at < Week)
                scan.NeedsYou.Add(new(LabelOf(group), "not completed when it was picked, so it was skipped"));
        }

        foreach (var (repository, prId, reviewStage, workspace, artifactCommit) in reviews)
        {
            if (reviewStage != "completed-verified")
            {
                Count(scan, "review-not-verified");
                continue;
            }
            if (workspace != target.Workspace)
            {
                Count(scan, "other-workspace");
                continue;
            }
            var repo = PrCommands.ParseRepo(repository);
            if (repo.Provider == "github")
            {
                Count(scan, "unsupported");
                continue;
            }
            // The judge refuses a review with no verified artifact commit; picking it would only fail every run.
            if (artifactCommit is not { Length: 40 })
            {
                Count(scan, "no-artifact-commit");
                continue;
            }
            var group = $"{repo.NormalizedKey}/{prId}";
            var observed = snapshots.GetValueOrDefault(repo.NormalizedKey)?[prId];
            var pr = new PrState(
                target,
                repository,
                repo.NormalizedKey,
                prId,
                judgments.TryGetValue(group, out var byN) ? [.. byN.Values] : [],
                observed?["Lifecycle"]?.GetValue<string>(),
                Time(observed?["LastChangedAt"]),
                skips.GetValueOrDefault(group)
            );
            var pick = Classify(pr, inFlight.Contains(group), now, settings);
            Count(
                scan,
                pick.Kind == Kind.Skip ? pick.Reason
                    : pick.Live ? "running"
                    : pick.Kind.ToString()
            );
            if (pick.Kind == Kind.NeedsYou)
            {
                scan.NeedsYou.Add(new(pr.Label, pick.Reason));
                // The note names the cause in words; the receipt's error text goes to the log.
                if (pr.Latest?["error"]?.GetValue<string>() is { } error)
                    Console.Error.WriteLine($"judge batch: {pr.Label}: judgment {pr.Latest["n"]}: {error}");
            }
            else if (pick.Kind != Kind.Skip)
                scan.Picks.Add(pick);
        }
    }

    // One rule for every host read about a judge thread: what it still runs, or null when quiet. A missing thread
    // (404) is quiet; any other failure counts as running and comes back as the error.
    private static async Task<(string? Running, Exception? Error)> HostReadAsync(
        Func<Task<string?>> read,
        CancellationToken ct
    )
    {
        try
        {
            return (await read(), null);
        }
        catch (HttpRequestException missing) when (missing.StatusCode == HttpStatusCode.NotFound)
        {
            return (null, null);
        }
        catch (Exception failure) when (failure is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return ("host state unreadable", failure);
        }
    }

    // A judge in a resumable stage runs until the host says its root is idle.
    private static async Task<string?> RootRunAsync(LmStreamingS2SClient client, string thread, CancellationToken ct) =>
        await client.GetRunStateAsync(thread, ct) is { IsInProgress: true } or { CurrentRunId: not null }
            ? "the root run is active"
            : null;

    // The host refused the batch's credentials. It refuses every read alike, so the scan names it once (the
    // status and one Needs-you line) instead of one line per judge.
    private static bool Refused(Scan scan, Exception failure)
    {
        if (
            failure
            is not HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } refused
        )
            return false;
        scan.SignInRefused ??= (int)refused.StatusCode.GetValueOrDefault();
        return true;
    }

    private static async Task<
        List<(string Repository, string PrId, string? Stage, string? Workspace, string? ArtifactCommit)>
    > ReviewsAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var query = connection.CreateCommand();
        query.CommandText =
            "SELECT json_extract(value,'$.repository'), json_extract(value,'$.prId'), json_extract(value,'$.stage'), "
            + "json_extract(value,'$.workspaceId'), json_extract(value,'$.artifactCommit.commit') "
            + "FROM cache WHERE substr(key,1,length(@prefix))=@prefix";
        query.Parameters.AddWithValue("@prefix", "reviewd/review-run/");
        var rows = new List<(string, string, string?, string?, string?)>();
        using var reader = await query.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1))
                continue;
            rows.Add(
                (
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)
                )
            );
        }
        return rows;
    }

    /// <summary>
    ///     Every KV row whose key starts with <paramref name="prefix" /> exactly, in key order. (LIKE would treat the
    ///     '_' common in project names as a wildcard.) The runner reads through it too.
    /// </summary>
    internal static async Task<List<(string Key, string Value)>> RowsAsync(
        SqliteConnection connection,
        string prefix,
        CancellationToken ct
    )
    {
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT key, value FROM cache WHERE substr(key,1,length(@prefix))=@prefix ORDER BY key";
        query.Parameters.AddWithValue("@prefix", prefix);
        var rows = new List<(string, string)>();
        using var reader = await query.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    /// <summary>
    ///     One PR's judge receipts by n, from rows under <c>reviewd/judge-run/{repo}/{pr}/</c>. A key whose n is not a
    ///     number, or a value that is not a JSON object, throws and names the key: the runner refuses that PR, the batch
    ///     skips that target.
    /// </summary>
    internal static SortedDictionary<int, JsonObject> Judgments(
        IEnumerable<(string Key, string Value)> rows,
        string prefix
    )
    {
        var judgments = new SortedDictionary<int, JsonObject>();
        foreach (var (key, value) in rows)
            judgments[
                int.TryParse(key[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                    ? n
                    : throw new DamagedRowException($"row {key} has no judgment number after {prefix}")
            ] = Row(key, value);
        return judgments;
    }

    // A row that does not parse names its key, so the skip message points at what to fix.
    private static JsonObject Row(string key, string value)
    {
        try
        {
            return JsonNode.Parse(value) as JsonObject
                ?? throw new DamagedRowException($"row {key} is not a JSON object");
        }
        catch (JsonException failure)
        {
            throw new DamagedRowException($"row {key} is not JSON: {failure.Message}", failure);
        }
    }

    /// <summary>A judge row that does not parse; the message names its key.</summary>
    internal sealed class DamagedRowException(string message, Exception? inner = null)
        : InvalidOperationException(message, inner);

    // One short cause a person can act on; the exception text goes to stderr.
    private static string PlainCause(Exception failure) =>
        failure switch
        {
            SqliteException { SqliteErrorCode: 14 } => "its database file is missing or cannot be opened",
            SqliteException => "its database could not be read",
            DamagedRowException => "a judge record in its database is damaged",
            _ => $"it could not be read {Details}",
        };

    private static SqliteConnection Open(string database, SqliteOpenMode mode) =>
        new(new SqliteConnectionStringBuilder { DataSource = database, Mode = mode }.ToString());

    // ---- one pick

    private static async Task<(Outcome Outcome, string Detail, JsonObject? After)> RunPickAsync(
        Pick pick,
        Settings settings,
        CliOptions options,
        ReviewdConfig config,
        LmStreamingS2SClient client,
        CancellationToken ct
    )
    {
        var pr = pick.Pr;
        var before = pr.Latest;
        int exit;
        var stdout = Console.Out;
        try
        {
            var run = CliOptions.Parse(RunArgs(pick, settings, options));
            Console.Error.WriteLine($"judge batch: {pr.Label}: {pick.Reason}");
            // judge run prints its receipt; stdout stays the batch summary, so the run's lines go to stderr.
            Console.SetOut(Console.Error);
            exit = await JudgeRunner.RunAsync(run, config, ct, leaseHeld: true);
        }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine($"judge batch: {pr.Label}: {failure.Message}");
            exit = 2;
        }
        finally
        {
            Console.SetOut(stdout);
        }
        if (exit == JudgeRunner.NotCompletedExitCode)
        {
            try
            {
                using var connection = Open(pr.Target.Db, SqliteOpenMode.ReadWrite);
                await connection.OpenAsync(ct);
                await new SqliteKvStore(connection).SetAsync(
                    $"reviewd/judge-skip/{pr.Key}/{pr.Pr}",
                    new JsonObject { ["reason"] = "not-completed", ["at"] = DateTimeOffset.UtcNow },
                    ct
                );
                return (Outcome.NotCompleted, "PR not completed; skipped for 24 h", null);
            }
            catch (SqliteException failure)
            {
                return (
                    Outcome.Failed,
                    $"PR not completed, and the skip marker was not written: {failure.Message}",
                    null
                );
            }
        }
        var after = await LatestAsync(pr, ct);
        var stage = after?["stage"]?.GetValue<string>();
        var changed =
            after is not null
            && (
                before is null
                || after["n"]?.ToJsonString() != before["n"]?.ToJsonString()
                || stage != before["stage"]?.GetValue<string>()
            );
        var detail = $"exit {exit}, judgment {after?["n"]?.ToJsonString() ?? "none"} {stage ?? ""}".TrimEnd();
        if (changed && stage == "ado-changed")
            return (Outcome.AdoChanged, detail, after);
        if (
            JudgeRunner.ResumableStages.Contains(stage ?? "")
            && after!["judgeThreadId"]?.GetValue<string>() is { } thread
            && (await HostReadAsync(() => RootRunAsync(client, thread, ct), ct)).Running is not null
        )
            return (Outcome.InFlight, detail, after);
        if (pick.Kind == Kind.PostCheck)
            return (
                Outcome.Done,
                after?["descendantsSettled"]?.GetValue<bool>() == true
                    ? $"{detail}, settled"
                    : $"{detail}, still unsettled",
                after
            );
        if (changed && (JudgeRunner.FinishedStages.Contains(stage ?? "") || stage == "capped"))
            return (Outcome.Done, detail, after);
        if (pick.Stored && !changed)
            return (Outcome.Refused, detail, after);
        return (Outcome.Failed, detail, after);
    }

    private static string[] RunArgs(Pick pick, Settings settings, CliOptions options)
    {
        var pr = pick.Pr;
        var identity = pick.Stored ? pr.Latest!["identity"] : null;
        string Stored(string name) =>
            identity?[name]?.GetValue<string>()
            ?? throw new InvalidOperationException($"the newest receipt stores no identity.{name}; resume it by hand.");
        List<string> args =
        [
            "judge",
            "run",
            "--repo",
            pr.Repository,
            "--pr",
            pr.Pr,
            "--db",
            pr.Target.Db,
            "--session",
            pr.Target.Session,
            "--workspace",
            pr.Target.Workspace,
            "--max-usd",
            pick.MaxUsd(settings).ToString(CultureInfo.InvariantCulture),
            "--execute",
        ];
        // A stored resume keeps its identity; a new judgment passes only what the operator set.
        if (pick.Stored)
            args.AddRange([
                "--provider",
                Stored("provider"),
                "--reasoning-effort",
                Stored("effort"),
                "--mode",
                Stored("mode"),
            ]);
        else
            foreach (var name in new[] { "provider", "reasoning-effort" })
                if (options.Get(name) is { } value)
                    args.AddRange([$"--{name}", value]);
        return [.. args];
    }

    private static async Task<JsonObject?> LatestAsync(PrState pr, CancellationToken ct)
    {
        using var connection = Open(pr.Target.Db, SqliteOpenMode.ReadOnly);
        await connection.OpenAsync(ct);
        var prefix = $"reviewd/judge-run/{pr.Key}/{pr.Pr}/";
        return Judgments(await RowsAsync(connection, prefix, ct), prefix).Values.LastOrDefault();
    }

    // ---- summary: markdown for a person (plain words; it is read on a phone), then one JSON line

    internal static string Summary(
        Scan scan,
        List<(Pick Pick, string? Wait)> plan,
        List<JsonObject> actions,
        List<Note> extraNeedsYou,
        Settings settings,
        bool execute,
        string status,
        int exit,
        DateTimeOffset now,
        string? cause = null
    )
    {
        var day = scan.Receipts.Where(r => now - Time(r.Receipt["finishedAt"]) < Day).ToList();
        var week = scan.Receipts.Where(r => now - Time(r.Receipt["finishedAt"]) < Week).ToList();
        var guard = scan.Guard;
        var needsYou = scan.NeedsYou.Concat(extraNeedsYou).Distinct().ToList();
        var read = scan.LastStart.Keys.Count(name => scan.Skipped.All(skip => skip.Name != name));
        var text = new StringBuilder();
        text.AppendLine(
            CultureInfo.InvariantCulture,
            $"# Judge batch {now:yyyy-MM-dd HH:mm}Z ({(execute ? "execute" : "preview")})"
        );
        text.AppendLine(CultureInfo.InvariantCulture, $"**Status:** {status}");
        text.AppendLine(
            CultureInfo.InvariantCulture,
            $"- Judgments in the last 24 h: {Stages(day)}. Missed issues: {Misses(day)}. Caught everything: {CaughtEverything(day)}."
        );
        text.AppendLine(CultureInfo.InvariantCulture, $"- Missed issues in 7 days: {Misses(week)}.");
        text.AppendLine(
            CultureInfo.InvariantCulture,
            $"- Spent in the last 24 h: {Usd(guard.Spent24h)} of the {Usd(settings.MaxUsdPerDay)} daily cap."
        );
        if (guard.Avg10 is null)
            text.AppendLine(
                CultureInfo.InvariantCulture,
                $"- Average per judgment: off until {AvgMinimum} have finished ({guard.AvgCount} so far); limit {Usd(settings.AvgUsdTarget)}."
            );
        else
            text.AppendLine(
                CultureInfo.InvariantCulture,
                $"- Average per judgment: {Usd(guard.Avg10)} over the last {guard.AvgCount}; limit {Usd(settings.AvgUsdTarget)}."
            );
        text.AppendLine(CultureInfo.InvariantCulture, $"- PRs: {PrCounts(scan.Counts)}.");
        text.AppendLine(
            CultureInfo.InvariantCulture,
            $"- Installs: {read} read{(scan.Skipped.Count > 0 ? $", {scan.Skipped.Count} unusable" : "")}. "
                + $"Running now: {(scan.InFlight.Count == 0 ? "none" : string.Join(", ", scan.InFlight))}."
        );
        // What needs a person comes before the table and the plan.
        text.AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"**Needs you:** {(needsYou.Count == 0 ? "none" : "")}");
        foreach (var line in NeedsYouLines(needsYou))
            text.AppendLine(CultureInfo.InvariantCulture, $"- {line}");
        if (day.Count > 0)
        {
            text.AppendLine().AppendLine("| PR | Result | $ | Misses |").AppendLine("|---|---|---|---|");
            // Newest first, so a cut table keeps the latest judgments. One with no judge thread never reached the
            // host, so it spent nothing.
            foreach (var (label, receipt) in day.OrderByDescending(r => Time(r.Receipt["finishedAt"])).Take(TableRows))
                text.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"| {label} #{receipt["n"]?.ToJsonString()} | {StageWords(receipt["stage"]?.GetValue<string>())} | {Usd(receipt["judgeUsage"]?["usd"]?.GetValue<decimal>() ?? (receipt["judgeThreadId"] is null ? 0m : null))} | {MissCount(receipt)} |"
                );
            if (day.Count > TableRows)
                text.AppendLine(CultureInfo.InvariantCulture, $"| … and {day.Count - TableRows} more | | | |");
        }
        if (actions.Count > 0)
        {
            // The JSON keeps each action's detail (exit code, stage); the person reads the outcome.
            text.AppendLine().AppendLine("**This run:**");
            foreach (var action in actions)
                text.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"- {action["pr"]}: {KindWord(action["kind"]?.GetValue<string>())}, {OutcomeWords(action["outcome"]?.GetValue<string>())}"
                );
        }
        if (!execute)
        {
            text.AppendLine().AppendLine("**Plan (preview; each new judge is counted at its cap):**");
            var rows = PlanLines(plan);
            foreach (var (line, _) in rows.Take(PlanRows))
                text.AppendLine(line);
            if (rows.Count > PlanRows)
                text.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"- … and {rows.Skip(PlanRows).Sum(row => row.Entries)} more"
                );
        }
        text.AppendLine(
            new JsonObject
            {
                ["schema"] = Schema,
                ["at"] = now,
                ["execute"] = execute,
                ["status"] = status,
                // The tick's alert title: a short cause when this run paused judging, else null.
                ["cause"] = cause,
                ["exit"] = exit,
                ["spent24h"] = guard.Spent24h,
                ["maxUsdPerDay"] = settings.MaxUsdPerDay,
                ["avg10"] = guard.Avg10,
                ["avgCount"] = guard.AvgCount,
                ["avgUsdTarget"] = settings.AvgUsdTarget,
                ["inFlight"] = new JsonArray([.. scan.InFlight.Select(label => (JsonNode)label)]),
                // The tick alerts once when this list becomes non-empty.
                ["unusable"] = new JsonArray([
                    .. scan.Skipped.Select(skip => new JsonObject { ["name"] = skip.Name, ["cause"] = skip.Reason }),
                ]),
                ["counts"] = new JsonObject([
                    .. scan.Counts.Select(c => KeyValuePair.Create(c.Key, (JsonNode?)c.Value)),
                ]),
                ["plan"] = new JsonArray([
                    .. plan.Select(entry => new JsonObject
                    {
                        ["pr"] = entry.Pick.Pr.Label,
                        ["kind"] = entry.Pick.Kind.ToString(),
                        ["wait"] = entry.Wait,
                    }),
                ]),
                ["actions"] = new JsonArray([.. actions.Select(action => action.DeepClone())]),
                ["needsYou"] = new JsonArray([.. needsYou.Select(note => (JsonNode)note.ToString())]),
            }.ToJsonString()
        );
        return text.ToString();
    }

    /// <summary>Notes with the same text read as one line with a count and the first few names.</summary>
    internal static IEnumerable<string> NeedsYouLines(IEnumerable<Note> notes) =>
        notes
            .GroupBy(note => note.What)
            .Select(group =>
                group.Count() == 1
                    ? group.Single().ToString()
                    : $"{group.Count()} × {group.Key}: {Names([.. group.Select(note => note.Who)])}"
            );

    private static string Names(List<string> names) =>
        names.Count <= NamesShown
            ? string.Join(", ", names)
            : $"{string.Join(", ", names.Take(NamesShown))} and {names.Count - NamesShown} more";

    // Consecutive picks of one kind with the same wait read as one line with a count.
    internal static List<(string Line, int Entries)> PlanLines(List<(Pick Pick, string? Wait)> plan)
    {
        var lines = new List<(string, int)>();
        for (var i = 0; i < plan.Count; )
        {
            var (pick, wait) = plan[i];
            var run = 1;
            while (
                wait is not null
                && i + run < plan.Count
                && plan[i + run].Pick.Kind == pick.Kind
                && plan[i + run].Wait == wait
            )
                run++;
            var kind = KindWord(pick.Kind.ToString());
            lines.Add(
                pick.Live ? ($"- {pick.Pr.Label}: running now; the batch re-attaches only if its process stops", 1)
                : run == 1 ? ($"- {pick.Pr.Label}: {kind} ({pick.Reason}); {Waiting(wait)}", 1)
                : ($"- {run} to {kind} (first {pick.Pr.Label}); {Waiting(wait)}", run)
            );
            i += run;
        }
        return lines;
    }

    // The plan's wait codes stay codes in the JSON; the markdown says them in words.
    private static string Waiting(string? wait) =>
        wait switch
        {
            null => "would run",
            "cap" => "no room under the daily cap",
            "limit" => "over this run's --limit",
            "latched" => "paused by the STOPPED file",
            "average guard" => "average cost above the limit",
            _ => wait,
        };

    // Receipt stages in plain words for the markdown; the JSON keeps the stage codes.
    internal static string StageWords(string? stage) =>
        stage switch
        {
            "completed" => "done",
            "completed-draft" => "done (draft only)",
            "capped" => "stopped at the cost cap",
            "failed" => "failed",
            "failed-no-output" => "failed with no output",
            "failed-precondition" => "could not start",
            "ado-changed" => "stopped because the PR changed on ADO",
            "inputs-written" => "the inputs step",
            "thread-created" => "the judge setup step",
            "send-pending" => "the sending step",
            "judge-running" => "the judging step",
            "post-check" => "the output check",
            "nudge-send-pending" or "nudge-running" => "the nudge step",
            _ => stage ?? "?",
        };

    private static string KindWord(string? kind) =>
        kind switch
        {
            "Start" => "start",
            "Resume" => "resume",
            "PostCheck" => "re-check",
            _ => kind ?? "?",
        };

    private static string OutcomeWords(string? outcome) =>
        outcome switch
        {
            nameof(Outcome.Done) => "done",
            nameof(Outcome.Failed) => $"failed {Details}",
            nameof(Outcome.InFlight) => "still running; it resumes next run",
            nameof(Outcome.AdoChanged) => "stopped because the PR changed on ADO",
            nameof(Outcome.NotCompleted) => "skipped because the PR is not completed",
            nameof(Outcome.Refused) => "not resumed (see Needs you)",
            _ => outcome ?? "?",
        };

    // The scan's per-PR counts in plain words, what a run acts on first.
    private static readonly (string Key, string Words)[] CountWords =
    [
        ("Start", "to start"),
        ("Resume", "to resume"),
        ("PostCheck", "to re-check"),
        ("NeedsYou", "waiting for you"),
        ("running", "running now"),
        ("judged", "already judged"),
        ("open", "still open"),
        ("not-in-snapshot", "not in the PR list yet"),
        ("too-old", "merged too long ago"),
        ("skip-cooling", "skipped today (not completed)"),
        ("precondition-cooling", "waiting a day to retry"),
        ("review-not-verified", "review not verified"),
        ("other-workspace", "in another workspace"),
        ("unsupported", "on GitHub (not supported)"),
        ("no-artifact-commit", "review has no verified artifact commit"),
    ];

    private static string PrCounts(SortedDictionary<string, int> counts) =>
        counts.Count == 0
            ? "none"
            : string.Join(
                ", ",
                CountWords
                    .Where(word => counts.ContainsKey(word.Key))
                    .Select(word => $"{counts[word.Key]} {word.Words}")
                    .Concat(
                        counts.Where(c => CountWords.All(word => word.Key != c.Key)).Select(c => $"{c.Value} {c.Key}")
                    )
            );

    private static string Stages(List<(string Label, JsonObject Receipt)> receipts) =>
        receipts.Count == 0
            ? "0"
            : $"{receipts.Count} ("
                + string.Join(
                    ", ",
                    receipts
                        .GroupBy(r => r.Receipt["stage"]?.GetValue<string>())
                        .Select(g => $"{g.Count()} {StageWords(g.Key)}")
                )
                + ")";

    private static string Misses(List<(string Label, JsonObject Receipt)> receipts)
    {
        var totals = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, receipt) in receipts)
        {
            if (receipt["judgment"]?["gapSummary"] is not JsonObject gaps)
                continue;
            foreach (var (category, count) in gaps)
            {
                if (count is JsonValue value && value.TryGetValue<int>(out var number) && number > 0)
                    totals[category] = totals.GetValueOrDefault(category) + number;
            }
        }
        return totals.Count == 0
            ? "none"
            : string.Join(
                ", ",
                GapWords
                    .Where(gap => totals.ContainsKey(gap.Key))
                    .Select(gap => $"{gap.Words} {totals[gap.Key]}")
                    .Concat(totals.Where(t => GapWords.All(gap => gap.Key != t.Key)).Select(t => $"{t.Key} {t.Value}"))
            );
    }

    // The judge's gap categories (Prompts/merged-judge.md) in plain words, in the prompt's order.
    private static readonly (string Key, string Words)[] GapWords =
    [
        ("knowledge", "missing system knowledge"),
        ("process-generic", "review method (any repo)"),
        ("process-repo", "review method (this repo)"),
        ("onboarding", "unknown repo convention"),
        ("access", "tool or source unavailable"),
        ("pipeline-filter", "found but dropped by our pipeline"),
        ("hallucination", "posted something with no basis"),
        ("human-decision", "needed a human decision"),
        ("no-lesson", "no lesson to draw"),
    ];

    // The judgment's "superset" (yes | no | UNKNOWN): did the review catch every valid issue people found?
    private static string CaughtEverything(List<(string Label, JsonObject Receipt)> receipts)
    {
        var values = receipts
            .Select(r => r.Receipt["judgment"]?["superset"]?.GetValue<string>()?.ToUpperInvariant())
            .OfType<string>()
            .ToList();
        return values.Count == 0
            ? "no results yet"
            : $"yes {values.Count(v => v == "YES")}, no {values.Count(v => v == "NO")}, unknown {values.Count(v => v is not ("YES" or "NO"))}";
    }

    private static int MissCount(JsonObject receipt) =>
        receipt["judgment"]?["issues"] is JsonArray issues
            ? issues.Count(issue => issue?["missStage"]?.GetValue<string>() is "found-not-posted" or "not-found")
            : 0;

    // " (<first line of the stop file>)", or "" when it is empty.
    private static string StopReason(string stopFile)
    {
        try
        {
            var reason = File.ReadLines(stopFile).FirstOrDefault()?.Trim();
            return string.IsNullOrEmpty(reason) ? "" : $" ({reason})";
        }
        catch (IOException failure)
        {
            Console.Error.WriteLine($"judge batch: cannot read {stopFile}: {failure.Message}");
            return " (unreadable)";
        }
    }

    private static string Usd(decimal? value) =>
        value is { } dollars ? "$" + dollars.ToString("0.00", CultureInfo.InvariantCulture) : "?";

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private static void Count(Scan scan, string name) => scan.Counts[name] = scan.Counts.GetValueOrDefault(name) + 1;

    /// <summary>
    ///     The next run re-checks this judgment (<see cref="Kind.PostCheck" />): its sub-agents or post-check are
    ///     unsettled, under 24 h since they could run on. After that it needs a person.
    /// </summary>
    private static bool ReCheckDue(JsonObject receipt, DateTimeOffset now) =>
        JudgeRunner.NeedsPostCheck(receipt) && SettleFrom(receipt) is { } since && now - since < Day;

    /// <summary>
    ///     Since when a judge's sub-agents may run on without its root: its freeze, or, for a failed judge whose
    ///     sub-agents had not settled, its start. Null when nothing can run on.
    /// </summary>
    private static DateTimeOffset? SettleFrom(JsonObject receipt) =>
        Time(receipt["frozenAt"])
        ?? (
            receipt["stage"]?.GetValue<string>() == "failed" && receipt["descendantsSettled"]?.GetValue<bool>() == false
                ? Time(receipt["startedAt"])
                : null
        );

    internal static DateTimeOffset? Time(JsonNode? node) =>
        node is JsonValue value
        && value.TryGetValue<string>(out var text)
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            ? time
        : node is JsonValue typed && typed.TryGetValue<DateTimeOffset>(out var stored) ? stored
        : null;
}
