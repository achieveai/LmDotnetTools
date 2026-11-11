using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.Sandbox;
using Microsoft.Data.Sqlite;

namespace Reviewd.Sample;

internal static class ReviewBatch
{
    public static Task<int> RunAsync(CliOptions options, ReviewdConfig config, CancellationToken ct) =>
        AuthCommands.RunAsync(
            options,
            config,
            ct,
            async token =>
            {
                if (!options.Has("execute"))
                    throw new InvalidOperationException("review batch requires --execute; no state was changed.");
                // Startup sweep: reclaim any slot a prior (crashed) batch left owned — including legacy
                // owners claimed before leasing existed — so restarting the batch always clears jams.
                await SweepSlotsAsync(options, config, undatedExpired: true, token);
                // Periodic sweep on its own timer: a slot whose owner's lease lapsed stays held until the reaper
                // frees it, and the queue keeps it out of use until then.
                var reaper = SweepPeriodicallyAsync(
                    cancellation => SweepSlotsAsync(options, config, undatedExpired: false, cancellation),
                    (delay, cancellation) => Task.Delay(delay, cancellation),
                    token
                );
                // One batch per workspace and database. A batch that holds it is waited out, not raced.
                using (
                    var lease = await AcquireBatchLeaseAsync(
                        () =>
                            ReviewRunner.AcquireLease(options.Required("db"), "batch/" + options.Required("workspace")),
                        (delay, cancellation) => Task.Delay(delay, cancellation),
                        token
                    )
                )
                {
                    string? unreadable = null;
                    await ScheduleAsync(
                        options,
                        config,
                        async cancellation =>
                        {
                            var (text, error) = await TryReadSelectionAsync(
                                options.Required("selection"),
                                cancellation
                            );
                            if (text is null)
                            {
                                // Logged once per distinct error, not every poll, so a long outage stays readable.
                                if (error != unreadable)
                                    Console.Error.WriteLine($"Selection unreadable; retrying every 5 seconds: {error}");
                                unreadable = error;
                                return null;
                            }
                            if (unreadable is not null)
                                Console.Error.WriteLine("Selection readable again.");
                            unreadable = null;
                            return text;
                        },
                        async (text, cancellation) =>
                        {
                            _ = ValidateAgePolicy(options, JsonNode.Parse(text)!.AsObject());
                            await ClaimAsync(options.Required("db"), SlotRelease.Digest(text), text, cancellation);
                            _ = config.CreateS2SClient();
                        },
                        (repo, pr, slot, head, cancellation) =>
                            ReviewRunner.CheckEligibilityAsync(options, config, repo, pr, slot, head, cancellation),
                        run => ReviewRunner.RunAsync(run, config, CancellationToken.None),
                        EnsureNoDaemon,
                        (delay, cancellation) => Task.Delay(delay, cancellation),
                        () => DateTimeOffset.UtcNow,
                        untilIdle: false,
                        token
                    );
                }
                await reaper;
            }
        );

    // A lease held by another batch, or a lock file this user cannot open yet, is retried like an unreadable
    // selection: logged once per distinct error. Scheduling starts only once the lease is held.
    internal static async Task<FileStream> AcquireBatchLeaseAsync(
        Func<FileStream> acquire,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken ct
    )
    {
        string? leaseError = null;
        while (true)
        {
            try
            {
                return acquire();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (ex.Message != leaseError)
                    Console.Error.WriteLine($"Batch lease unavailable; retrying every 5 seconds: {ex.Message}");
                leaseError = ex.Message;
                await delay(Tick, ct);
            }
        }
    }

    // The discovery job replaces the selection file while the batch polls it, so a read can find it locked by the
    // replace or briefly missing. That is a miss to retry on the next poll, not a reason to stop: seen live, one
    // sharing violation ended scheduling for 7 hours while the process stayed up, so the watchdog never replaced it.
    internal static async Task<(string? Text, string? Error)> TryReadSelectionAsync(string path, CancellationToken ct)
    {
        try
        {
            return (await File.ReadAllTextAsync(path, ct), null);
        }
        catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && !ct.IsCancellationRequested)
        {
            return (null, ex.Message);
        }
    }

    internal static async Task SweepPeriodicallyAsync(
        Func<CancellationToken, Task> sweep,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken ct
    )
    {
        try
        {
            while (true)
            {
                await delay(TimeSpan.FromMinutes(5), ct);
                await sweep(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private static void EnsureNoDaemon()
    {
        if (OperatingSystem.IsWindows())
            EnsureNoWindowsDaemon(ProcessNames);
        else
            EnsureNoDaemon(() => Directory.EnumerateDirectories("/proc"), File.ReadAllBytes);
    }

    private static string[] ProcessNames()
    {
        var processes = System.Diagnostics.Process.GetProcesses();
        try
        {
            return [.. processes.Select(process => process.ProcessName)];
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    /// <summary>
    /// Windows offers no other process's command line without WMI, so this matches the daemon's apphost
    /// (CodeReviewDaemon.Sample.exe) by process name. A daemon hosted as <c>dotnet CodeReviewDaemon.Sample.dll</c>
    /// is not seen.
    /// </summary>
    internal static void EnsureNoWindowsDaemon(Func<IEnumerable<string>> processNames)
    {
        if (
            processNames()
                .Any(name => string.Equals(name, "CodeReviewDaemon.Sample", StringComparison.OrdinalIgnoreCase))
        )
            throw new InvalidOperationException("Competing daemon producer detected.");
    }

    /// <summary>
    /// Reclaims every slot in the batch's workspace whose lease has lapsed, using the live session to run
    /// each discard. Best-effort and self-contained: it opens its own short-lived read-write connection
    /// (with a busy timeout so it yields to in-flight reviews) and swallows any sweep-level error so a
    /// transient SQLite contention or host blip never stops the batch — the next sweep retries.
    /// <paramref name="undatedExpired"/> also reclaims legacy owners with no lease; used only at startup
    /// when no review is running yet.
    /// </summary>
    private static async Task SweepSlotsAsync(
        CliOptions options,
        ReviewdConfig config,
        bool undatedExpired,
        CancellationToken ct
    )
    {
        try
        {
            await using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = options.Required("db"),
                    Mode = SqliteOpenMode.ReadWrite,
                }.ToString()
            );
            await connection.OpenAsync(ct);
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout=5000;";
                _ = await pragma.ExecuteNonQueryAsync(ct);
            }
            var client = config.CreateS2SClient();
            using var sandboxClient = config.CreateSandboxClient(TimeSpan.FromMinutes(10));
            var session = options.Required("session");
            SlotReaper.SandboxExecutor sandbox = (phase, args, operation, token) =>
                ReviewRunner.ExecuteSandboxAsync(sandboxClient, session, phase, args, operation, token);
            var results = await SlotReaper.SweepAsync(
                connection,
                client,
                sandbox,
                options.Required("workspace"),
                DateTimeOffset.UtcNow,
                undatedExpired,
                execute: true,
                ct
            );
            var reclaimed = results.Where(result => result.Status == "reclaimed").ToList();
            if (reclaimed.Count > 0)
                Console.Error.WriteLine(
                    $"Reaper reclaimed {reclaimed.Count} lapsed-lease slot(s): "
                        + string.Join(", ", reclaimed.Select(result => $"{result.Repo}#{result.Pr}/slot{result.Slot}"))
                );
            // Every result here is an owner whose lease lapsed. A forced reclaim names what it overrode; a skip is a
            // slot still blocked (usually on active host agents) and is said on every sweep: a silent skip hid a jam 8 hours.
            foreach (var forced in reclaimed.Where(result => result.Detail is not null))
                Console.Error.WriteLine(
                    $"Reaper forced lapsed-lease slot {forced.Repo}#{forced.Pr}/slot{forced.Slot}: {forced.Detail}"
                );
            foreach (var skipped in results.Where(result => result.Status == "skipped"))
                Console.Error.WriteLine(
                    $"Reaper waiting on lapsed-lease slot {skipped.Repo}#{skipped.Pr}/slot{skipped.Slot}: {skipped.Detail}"
                );
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Console.Error.WriteLine($"Reaper sweep skipped: {ex.Message}.");
        }
    }

    internal static void EnsureNoDaemon(Func<IEnumerable<string>> enumerate, Func<string, byte[]> read)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Batch producer inspection currently requires Linux.");
        foreach (var directory in enumerate())
        {
            if (!int.TryParse(Path.GetFileName(directory), out _))
                continue;
            byte[] commandLine;
            try
            {
                commandLine = read(Path.Combine(directory, "cmdline"));
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (IOException ex) when (OperatingSystem.IsLinux() && ex.HResult == 3)
            {
                // Linux ESRCH: the process exited after its proc entry was opened.
                continue;
            }
            if (RunsDaemon(System.Text.Encoding.UTF8.GetString(commandLine).Split('\0')))
                throw new InvalidOperationException("Competing daemon producer detected.");
        }
    }

    // Value-taking options of `dotnet exec`, which come before the app's path.
    private static readonly HashSet<string> DotnetHostOptions =
    [
        "--additionalprobingpath",
        "--additional-deps",
        "--depsfile",
        "--fx-version",
        "--roll-forward",
        "--roll-forward-on-no-candidate-fx",
        "--runtimeconfig",
    ];

    /// <summary>
    /// True only for a process running the daemon: its apphost, or <c>dotnet [exec [options]]</c> whose first .dll is
    /// the daemon's. A tool that merely names the daemon's folder or file (grep, ls, git) once stopped every live batch.
    /// </summary>
    private static bool RunsDaemon(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
            return false;
        var program = Path.GetFileName(arguments[0]);
        if (program == "CodeReviewDaemon.Sample")
            return true;
        if (program != "dotnet")
            return false;
        for (var index = 1; index < arguments.Count; index++)
        {
            if (DotnetHostOptions.Contains(arguments[index]))
                index++;
            else if (arguments[index].EndsWith(".dll", StringComparison.Ordinal))
                return Path.GetFileName(arguments[index]) == "CodeReviewDaemon.Sample.dll";
        }
        return false;
    }

    internal static async Task<bool> ClaimAsync(string database, string digest, string selection, CancellationToken ct)
    {
        using var connection = Connection(database, SqliteOpenMode.ReadWrite);
        await connection.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO cache(key,value) VALUES(@key,@value)";
        command.Parameters.AddWithValue("@key", "reviewd/batch-request/" + digest);
        command.Parameters.AddWithValue("@value", selection);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>The saved queue of one workspace's batch: every picked PR, queued, running or settled.</summary>
    internal static string QueueKey(string workspace) => "reviewd/batch-queue/" + workspace;

    // How often a running batch rereads the selection and looks for a free slot.
    internal static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Runs the batch from one saved queue per workspace (<see cref="QueueKey"/>). Each pass merges the latest
    /// selection into the queue and starts queued PRs on the lowest free slots of their repo, so a picked PR never
    /// waits for unrelated reviews. A retained owner resumes only on its own slot, and a held slot blocks only itself.
    /// With <paramref name="untilIdle"/> it returns once nothing runs and nothing more can start; otherwise it runs
    /// until <paramref name="ct"/> is canceled. Either way it waits for the reviews it started before it returns.
    /// </summary>
    internal static async Task ScheduleAsync(
        CliOptions options,
        ReviewdConfig config,
        Func<CancellationToken, Task<string?>> readSelection,
        Func<string, CancellationToken, Task> prepare,
        Func<string, string, int, string, CancellationToken, Task<JsonObject>> eligibility,
        Func<CliOptions, Task<int>> run,
        Action checkProducer,
        Func<TimeSpan, CancellationToken, Task> delay,
        Func<DateTimeOffset> now,
        bool untilIdle,
        CancellationToken ct
    )
    {
        if (!options.Has("execute"))
            throw new InvalidOperationException("review batch requires --execute.");
        var database = options.Required("db");
        var workspace = options.Required("workspace");
        var queueKey = QueueKey(workspace);
        var maxAgeHours = MaxAgeHours(options);
        var scope = new JsonObject
        {
            ["workspace"] = workspace,
            ["session"] = options.Required("session"),
            ["provider"] = options.Required("provider"),
            ["mode"] = options.Get("mode") ?? config.ModeId,
        };
        if (options.Get("reasoning-effort") is { } reasoningEffort)
            scope["reasoningEffort"] = reasoningEffort;
        if (maxAgeHours is { } hours)
            scope["maxAgeHours"] = hours;
        JsonObject? state = null;
        using var writes = new SemaphoreSlim(1, 1);
        // Steps started here, by repo and slot. Only the loop below adds or removes them.
        var running = new Dictionary<(string Repo, int Slot), (JsonObject Entry, string Pr, Task Step)>();
        var launchHolds = new ConcurrentDictionary<(string Repo, int Slot), int>();
        var stopped = new ConcurrentDictionary<(string Repo, int Slot), bool>();
        var sessions = new List<CancellationTokenSource>();
        var watchers = new List<Task>();
        // Admission runs under this source. A competing producer cancels it; the next selection starts another.
        CancellationTokenSource? admitting = null;
        string? merged = null;
        string? blocked = null;
        string? admissionError = null;
        string? mergeError = null;
        // The selection this batch last reported idle; reset once anything runs.
        string? reportedIdle = null;
        try
        {
            do
            {
                foreach (
                    var done in running.Where(pair => pair.Value.Step.IsCompleted).Select(pair => pair.Key).ToList()
                )
                    running.Remove(done);
                if (admitting is { IsCancellationRequested: true })
                {
                    if (untilIdle || ct.IsCancellationRequested)
                        break;
                    // Suspended: admitted reviews finish, and admission resumes with the next selection.
                    blocked = merged;
                    admitting = null;
                }
                string? text;
                try
                {
                    text = await readSelection(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                var digest = text is null ? null : SlotRelease.Digest(text);
                var queueCurrent = true;
                if (text is not null && digest != blocked)
                {
                    try
                    {
                        if (digest != merged)
                            await prepare(text, ct);
                        await MergeAsync(text, digest!);
                        merged = digest;
                        blocked = null;
                        mergeError = null;
                        if (admitting is null)
                        {
                            checkProducer();
                            admitting = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            sessions.Add(admitting);
                            watchers.Add(WatchAsync(admitting));
                        }
                    }
                    catch (Exception ex) when (!untilIdle && !ct.IsCancellationRequested && digest == merged)
                    {
                        // The selection was already accepted; only updating its queue failed. Retried next tick,
                        // with no admission until the queue is current again.
                        if (ex.Message != mergeError)
                            Console.Error.WriteLine(
                                $"Queue {queueKey} not updated; no admission until it is, retrying every 5 seconds: {ex.Message}"
                            );
                        mergeError = ex.Message;
                        queueCurrent = false;
                    }
                    catch (Exception ex) when (!untilIdle && !ct.IsCancellationRequested)
                    {
                        // Parked until a new selection arrives, as a round was. Admitted reviews keep running.
                        Console.Error.WriteLine(
                            $"Batch parked: {ex.Message}. Auth remains running; admitted reviews continue; waiting for a new selection."
                        );
                        blocked = digest;
                    }
                }
                if (admitting is { IsCancellationRequested: false } && blocked is null && queueCurrent)
                {
                    try
                    {
                        await AdmitAsync(admitting);
                        admissionError = null;
                    }
                    catch (Exception ex) when (!untilIdle && !ct.IsCancellationRequested)
                    {
                        if (ex.Message != admissionError)
                            Console.Error.WriteLine($"Admission skipped; retrying every 5 seconds: {ex.Message}");
                        admissionError = ex.Message;
                        queueCurrent = false;
                    }
                }
                if (running.Count > 0)
                    reportedIdle = null;
                else if (
                    !untilIdle
                    && admitting is { IsCancellationRequested: false }
                    && blocked is null
                    && queueCurrent
                    && reportedIdle != merged
                    && NextRetry() is null
                )
                {
                    // Nothing runs and nothing more can start until the selection changes. Keep-alive picks the next
                    // selection only once the queue says so for the selection it wrote.
                    try
                    {
                        if (state!["idle"]?.GetValue<string>() != merged)
                            await SaveAsync(() => state!["idle"] = merged);
                        reportedIdle = merged;
                        Console.WriteLine(
                            "Batch scheduling finished: nothing left to start for this selection; polling for the next one. OAuth callback remains running."
                        );
                    }
                    catch (Exception) when (!ct.IsCancellationRequested)
                    {
                        // SaveAsync suspended admission and said why.
                    }
                }
                if (untilIdle)
                {
                    if (admitting is not { IsCancellationRequested: false })
                        break;
                    if (running.Count > 0)
                    {
                        await Task.WhenAny(running.Values.Select(value => value.Step));
                        continue;
                    }
                    if (NextRetry() is not { } retryAt)
                        break;
                    try
                    {
                        await delay(TimeSpan.FromSeconds(retryAt - now().ToUnixTimeSeconds()), admitting.Token);
                    }
                    catch (OperationCanceledException) when (admitting.IsCancellationRequested) { }
                    continue;
                }
                // A free slot is taken on the next tick, or as soon as a running step ends.
                using var wake = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var tick = delay(Tick, wake.Token);
                await Task.WhenAny([tick, .. running.Values.Select(value => value.Step)]);
                await wake.CancelAsync();
                try
                {
                    await tick;
                }
                catch (OperationCanceledException) when (wake.IsCancellationRequested) { }
            } while (!ct.IsCancellationRequested);
        }
        finally
        {
            // SIGTERM and suspension stop admission only; reviews already started finish first.
            await Task.WhenAll(running.Values.Select(value => value.Step));
            foreach (var source in sessions)
                await source.CancelAsync();
            await Task.WhenAll(watchers);
            foreach (var source in sessions)
                source.Dispose();
        }
        Console.WriteLine("Batch scheduling finished. OAuth callback remains running.");

        async Task WatchAsync(CancellationTokenSource source)
        {
            try
            {
                while (true)
                {
                    await delay(Tick, source.Token);
                    checkProducer();
                }
            }
            catch (OperationCanceledException) when (source.IsCancellationRequested) { }
            catch (Exception ex)
            {
                await source.CancelAsync();
                Console.Error.WriteLine($"Scheduling suspended: {ex.Message}. Admitted work and auth retained.");
            }
        }

        long? NextRetry()
        {
            var nowSeconds = now().ToUnixTimeSeconds();
            long? next = null;
            foreach (var entry in state?["entries"]?.AsArray() ?? [])
                if (
                    entry!["status"]?.GetValue<string>() == "retry"
                    && entry["retryAt"]?.GetValue<long>() is { } at
                    && at > nowSeconds
                    && (next is null || at < next)
                )
                    next = at;
            return next;
        }

        // Merges a selection into the queue. A new selection starts every selected PR over (fresh checks and retries),
        // as each new round did, except a step running here, which keeps its record. A PR that left the selection
        // leaves the queue unless it runs here or its retained owner still holds its slot.
        async Task MergeAsync(string text, string digest)
        {
            var selection = JsonNode.Parse(text)!.AsObject();
            _ = ValidateAgePolicy(options, selection);
            var lanes = new JsonObject();
            var rows = new List<(string Repo, string Pr, string Head)>();
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (repo, repoRows) in selection["repositories"]!.AsObject())
            {
                _ = PrCommands.ParseRepo(repo);
                var slots = selection["lanes"]![repo]!.AsArray().Select(value => value!.GetValue<int>()).ToArray();
                if (
                    slots.Length == 0
                    || slots.Distinct().Count() != slots.Length
                    || slots.Any(slot => slot is < 0 or > 5)
                )
                    throw new InvalidOperationException("Invalid batch lanes.");
                lanes[repo] = new JsonArray([.. slots.Order().Select(slot => (JsonNode)JsonValue.Create(slot))]);
                foreach (var row in repoRows!.AsArray())
                {
                    var pr = row!["prId"]!.GetValue<string>();
                    var head = row["headSha"]!.GetValue<string>();
                    if (
                        row["repository"]?.GetValue<string>() != repo
                        || !int.TryParse(pr, out var id)
                        || id <= 0
                        || !identities.Add(repo + "/" + pr)
                        || head.Length != 40
                        || !head.All(Uri.IsHexDigit)
                    )
                        throw new InvalidOperationException("Invalid or duplicate selected PR.");
                    rows.Add((repo, pr, head));
                }
            }
            var selected = rows.Select(row => Identity(row.Repo, row.Pr, row.Head)).ToHashSet(StringComparer.Ordinal);
            var selectedPrs = rows.Select(row => row.Repo + "/" + row.Pr).ToHashSet(StringComparer.Ordinal);
            await writes.WaitAsync();
            try
            {
                var startup = state is null;
                var queue = state ?? await LoadQueueAsync(digest);
                var same = queue["selection"]?.GetValue<string>() == digest;
                if (same && !JsonNode.DeepEquals(queue["scope"], scope))
                    throw new InvalidOperationException(
                        "Persisted batch execution scope changed; no admission allowed."
                    );
                var entries = queue["entries"]!.AsArray().Select(entry => entry!.AsObject()).ToList();
                if (same && !startup && entries.All(entry => selected.Contains(Identity(entry))))
                    return;
                var busy = running.Values.Select(value => value.Entry).ToHashSet(ReferenceEqualityComparer.Instance);
                var next = new List<JsonObject>();
                var classify = new List<JsonObject>();
                if (same)
                {
                    next.AddRange(entries);
                    if (startup)
                        classify.AddRange(entries);
                }
                else
                {
                    foreach (var (repo, pr, head) in rows)
                    {
                        var entry = entries.FirstOrDefault(value =>
                            busy.Contains(value) && Identity(value) == Identity(repo, pr, head)
                        );
                        if (entry is null)
                        {
                            entry = new JsonObject
                            {
                                ["repo"] = repo,
                                ["pr"] = pr,
                                ["head"] = head,
                                ["status"] = "queued",
                                ["failures"] = 0,
                                ["order"] = (long)next.Count,
                            };
                            classify.Add(entry);
                        }
                        next.Add(entry);
                    }
                    next.AddRange(entries.Where(entry => !selected.Contains(Identity(entry))));
                }
                // An unselected entry stays while it runs here or its retained attempt still owns a slot: any retained
                // attempt of a PR that left the selection, but for a PR selected at a new head only the attempt of
                // this entry's head, which must finish on that head before the new head starts.
                foreach (var entry in next.ToList())
                {
                    if (selected.Contains(Identity(entry)) || busy.Contains(entry))
                        continue;
                    if (
                        await ReadAsync(database, AttemptKey(entry)) is { } retained
                        && !Reusable(retained)
                        && (
                            !selectedPrs.Contains(
                                entry["repo"]!.GetValue<string>() + "/" + entry["pr"]!.GetValue<string>()
                            )
                            || retained["headSha"]?.GetValue<string>() == entry["head"]!.GetValue<string>()
                        )
                    )
                        continue;
                    next.Remove(entry);
                }
                // A kept entry of a repo that left the selection keeps that repo's lanes, so it can still finish.
                foreach (var repo in next.Select(entry => entry["repo"]!.GetValue<string>()).Distinct().ToList())
                {
                    if (lanes.ContainsKey(repo))
                        continue;
                    var kept = queue["lanes"]?[repo] is JsonArray previous
                        ? previous.Select(value => value!.GetValue<int>())
                        : next.Where(entry => entry["repo"]!.GetValue<string>() == repo && entry["slot"] is not null)
                            .Select(entry => entry["slot"]!.GetValue<int>());
                    lanes[repo] = new JsonArray([
                        .. kept.Distinct().Order().Select(slot => (JsonNode)JsonValue.Create(slot)),
                    ]);
                }
                foreach (var entry in classify)
                    await ClassifyAsync(entry);
                queue["scope"] = scope.DeepClone();
                queue["selection"] = digest;
                queue["lanes"] = lanes;
                var array = queue["entries"]!.AsArray();
                array.Clear();
                foreach (var entry in next)
                    array.Add(entry);
                using (var connection = Connection(database, SqliteOpenMode.ReadWrite))
                {
                    await connection.OpenAsync();
                    await WriteAsync(connection, null, queue);
                }
                state = queue;
                if (!same)
                {
                    launchHolds.Clear();
                    stopped.Clear();
                }
            }
            finally
            {
                writes.Release();
            }
        }

        async Task<JsonObject> LoadQueueAsync(string digest)
        {
            if (await ReadRawAsync(database, queueKey) is { } raw)
            {
                // Every selection reloads this row, so a new selection cannot repair it: an operator must.
                string problem;
                try
                {
                    problem = JsonNode.Parse(raw) switch
                    {
                        JsonObject saved
                            when saved["schemaVersion"] is JsonValue version
                                && version.TryGetValue<int>(out var number)
                                && number == 2
                                && saved["entries"] is JsonArray
                                && saved["lanes"] is JsonObject => "",
                        JsonObject saved
                            when saved["schemaVersion"] is JsonValue version
                                && version.TryGetValue<int>(out var number)
                                && number == 2 => "its entries or lanes are missing",
                        JsonObject => "its schemaVersion is unknown or not a number",
                        _ => "it is not a JSON object",
                    };
                }
                catch (JsonException ex)
                {
                    problem = "it is not valid JSON (" + ex.Message + ")";
                }
                if (problem.Length == 0)
                    return JsonNode.Parse(raw)!.AsObject();
                throw new InvalidOperationException(
                    $"Batch queue {queueKey} is unusable: {problem}. A new selection alone cannot repair it; repair the row or move it aside. No admission until then."
                );
            }
            // Before the queue, each selection ran as one round under reviewd/batch-state/<digest>, and a restart
            // resumed only the round of the current selection. That round becomes the queue; older rounds stay as
            // they are, already abandoned.
            if (
                await ReadAsync(database, "reviewd/batch-state/" + digest) is JsonObject round
                && round["schemaVersion"]?.GetValue<int>() == 1
                && round["scope"]?["workspace"]?.GetValue<string>() == workspace
            )
                return new JsonObject
                {
                    ["schemaVersion"] = 2,
                    ["scope"] = round["scope"]!.DeepClone(),
                    ["selection"] = digest,
                    ["lanes"] = new JsonObject(),
                    ["entries"] = round["entries"]!.DeepClone(),
                };
            return new JsonObject
            {
                ["schemaVersion"] = 2,
                ["scope"] = scope.DeepClone(),
                ["selection"] = null,
                ["lanes"] = new JsonObject(),
                ["entries"] = new JsonArray(),
            };
        }

        async Task ClassifyAsync(JsonObject entry)
        {
            var receipt = await ReadAsync(database, ReceiptKey(entry));
            var attempt = await ReadAsync(database, AttemptKey(entry));
            if (attempt is not null)
            {
                var raw = (await ReadRawAsync(database, AttemptKey(entry)))!;
                var release = await ReadAsync(database, SlotRelease.Key(raw));
                if (
                    release is not null
                    && SlotRelease.IsResetRetirement(release, raw, options.Required("session"))
                    && SlotRelease.Allows(release, raw, "different-pr", options.Required("session"))
                )
                {
                    entry["status"] = "retired";
                    NormalizeSlot(entry, attempt);
                    return;
                }
            }
            if (entry["status"]?.GetValue<string>() == "retired")
                throw new InvalidOperationException(
                    "Retired batch owner lacks an exact reset certificate for this session."
                );
            if (Verified(receipt, entry) && Verified(attempt, entry) && Reusable(attempt!))
                entry["status"] = "verified";
            else if (entry["status"]?.GetValue<string>() == "launching" && attempt is null)
            {
                entry["status"] = "held";
                entry["reason"] = InterruptedLaunch;
            }
            NormalizeSlot(entry, attempt);
        }

        // Starts work on every free slot: a retained owner on its own slot, else the next queued PR on the lowest
        // free slot. A held slot, or one claimed outside this queue, is skipped without delaying the others.
        async Task AdmitAsync(CancellationTokenSource source)
        {
            var nowSeconds = now().ToUnixTimeSeconds();
            var busy = running.Values.Select(value => value.Entry).ToHashSet(ReferenceEqualityComparer.Instance);
            var all = state!["entries"]!.AsArray().Select(entry => entry!.AsObject()).ToList();
            var entries = all.Where(entry => !busy.Contains(entry)).ToList();
            foreach (var (repo, laneSlots) in state["lanes"]!.AsObject())
            {
                var slots = laneSlots!.AsArray().Select(value => value!.GetValue<int>()).ToArray();
                var held = running.Keys.Where(key => key.Repo == repo).Select(key => key.Slot).ToHashSet();
                var runningPrs = running
                    .Where(pair => pair.Key.Repo == repo)
                    .Select(pair => pair.Value.Pr)
                    .ToHashSet(StringComparer.Ordinal);
                var pinned = new Dictionary<int, JsonObject>();
                var fresh = new List<JsonObject>();
                foreach (var entry in entries.Where(value => value["repo"]!.GetValue<string>() == repo))
                {
                    var status = entry["status"]!.GetValue<string>();
                    if (status is "verified" or "closed" or "stale" or "retired" or "age-excluded" or "quarantined")
                        continue;
                    var attempt = await ReadAsync(database, AttemptKey(entry));
                    var owned = attempt is not null && !Reusable(attempt) ? attempt["slot"]?.GetValue<int>() : null;
                    if (status is "held" or "closed-held")
                    {
                        // Held work keeps out of the slot it may still own, and only that slot.
                        if (
                            entry["reason"]?.GetValue<string>() == InterruptedLaunch
                            && entry["slot"]?.GetValue<int>() is { } launchSlot
                        )
                            held.Add(launchSlot);
                        else if (owned is { } heldSlot)
                            held.Add(heldSlot);
                        continue;
                    }
                    // Two heads of one PR never run at once, and a new head waits while an earlier head in this queue
                    // still owns the PR's attempt: that head resumes, finishes and cleans up first.
                    if (runningPrs.Contains(entry["pr"]!.GetValue<string>()))
                        continue;
                    if (
                        attempt is not null
                        && !Reusable(attempt)
                        && attempt["headSha"]?.GetValue<string>() is { } ownerHead
                        && ownerHead != entry["head"]!.GetValue<string>()
                        && all.Any(other =>
                            Identity(other) == Identity(repo, entry["pr"]!.GetValue<string>(), ownerHead)
                        )
                    )
                        continue;
                    if (owned is not { } ownSlot)
                    {
                        fresh.Add(entry);
                        continue;
                    }
                    if (!slots.Contains(ownSlot) || pinned.ContainsKey(ownSlot))
                    {
                        var reason = slots.Contains(ownSlot)
                            ? "Multiple retained owners in one slot."
                            : "Retained owner's slot is not one of this repo's lanes; reconcile before admission.";
                        await SaveAsync(() =>
                        {
                            entry["status"] = "held";
                            entry["reason"] = reason;
                            NormalizeSlot(entry, attempt);
                        });
                        held.Add(ownSlot);
                        continue;
                    }
                    pinned[ownSlot] = entry;
                }
                fresh.Sort(
                    (left, right) => left["order"]!.GetValue<long>().CompareTo(right["order"]!.GetValue<long>())
                );
                foreach (var slot in slots)
                {
                    if (held.Contains(slot) || stopped.ContainsKey((repo, slot)))
                        continue;
                    JsonObject? next;
                    if (pinned.TryGetValue(slot, out var owner))
                        // A retained owner's slot waits for it, even through its backoff.
                        next = Due(owner) ? owner : null;
                    else
                    {
                        // A slot claimed outside this queue is free only to the PR that holds it.
                        var claim = await ReadAsync(
                            database,
                            SlotRelease.OwnerKey(workspace, PrCommands.ParseRepo(repo).NormalizedKey, slot)
                        );
                        var holder =
                            claim is not null && claim["state"]?.GetValue<string>() != "released"
                                ? claim["prId"]?.GetValue<string>() ?? ""
                                : null;
                        next = fresh.FirstOrDefault(entry =>
                            Due(entry) && (holder is null || entry["pr"]!.GetValue<string>() == holder)
                        );
                    }
                    if (next is null)
                        continue;
                    var pr = next["pr"]!.GetValue<string>();
                    fresh.RemoveAll(entry => entry["pr"]!.GetValue<string>() == pr);
                    running[(repo, slot)] = (next, pr, GuardStepAsync(repo, slot, next, source));
                }
            }

            bool Due(JsonObject entry) => (entry["retryAt"]?.GetValue<long>() ?? 0) <= nowSeconds;
        }

        async Task GuardStepAsync(string repo, int slot, JsonObject entry, CancellationTokenSource source)
        {
            try
            {
                await StepAsync(repo, slot, entry, source);
            }
            catch (OperationCanceledException) when (source.IsCancellationRequested) { }
            catch (Exception ex)
            {
                stopped[(repo, slot)] = true;
                Console.Error.WriteLine(
                    source.IsCancellationRequested
                        ? $"Lane {repo}/{slot} held: {ex.Message}. Admission is suspended; nothing new starts."
                        : $"Lane {repo}/{slot} held: {ex.Message}. Other safe slots continue."
                );
            }
            try
            {
                var attempt = await ReadAsync(database, AttemptKey(entry));
                await SaveAsync(() => NormalizeSlot(entry, attempt));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Lane {repo}/{slot}: queue slot not recorded: {ex.Message}.");
            }
        }

        // One PR on one slot: the pre-launch checks, the launch and its outcome.
        async Task StepAsync(string repo, int slot, JsonObject entry, CancellationTokenSource source)
        {
            var token = source.Token;
            JsonNode? attempt = await ReadAsync(database, AttemptKey(entry));
            // A head that failed the same way three times is skipped until the PR is pushed again. Only a free
            // slot is left this way: an owned attempt still goes through the resume and held rules below.
            if (
                (attempt is null || Reusable(attempt))
                && IsQuarantined(await ReadAsync(database, FailureKey(entry)), entry["head"]!.GetValue<string>())
            )
            {
                await SaveAsync(() =>
                {
                    entry["status"] = "quarantined";
                    entry["reason"] = "The same failure three times on this head; skipped until a new push.";
                });
                return;
            }
            // AttemptKey is per repo/pr, not per head. A completed-verified attempt whose slot
            // was already released belongs to an EARLIER head of this PR; once the PR advances,
            // its new head is a fresh review, not a stuck retry. Drop the stale attempt so the
            // new head is admitted below instead of parking it as an unresumable outcome.
            if (
                attempt is not null
                && attempt["stage"]?.GetValue<string>() == "completed-verified"
                && Reusable(attempt)
                && attempt["reviewedHeadSha"]?.GetValue<string>() != entry["head"]!.GetValue<string>()
            )
                attempt = null;
            // Likewise an attempt whose slot was released mid-review (reaper or reclaim-slot): its stage
            // stays, but once the PR advances it can never publish, so the new head is admitted fresh.
            if (
                attempt is not null
                && PrSelection.IsReleasedForEarlierHead(
                    JsonSerializer.SerializeToElement(attempt),
                    entry["head"]!.GetValue<string>()
                )
            )
                attempt = null;
            if (attempt is not null && !Resumable(attempt))
            {
                await SaveAsync(() =>
                {
                    entry["status"] = "held";
                    entry["reason"] ??= "Retained outcome requires reconciliation; slot remains owned.";
                });
                return;
            }
            var launched = false;
            var launchedFresh = false;
            string? attemptBeforeLaunch = null;
            try
            {
                try
                {
                    checkProducer();
                }
                catch
                {
                    await source.CancelAsync();
                    throw;
                }
                var pr = entry["pr"]!.GetValue<string>();
                var head = entry["head"]!.GetValue<string>();
                var live = await eligibility(repo, pr, slot, head, token);
                if (
                    live["prId"]?.GetValue<string>() != pr
                    || live["status"]?.GetValue<string>() is not ("active" or "completed" or "abandoned")
                    || live["isDraft"] is not JsonValue
                    || live["headSha"]?.GetValue<string>() is not { Length: 40 }
                )
                    throw new InvalidOperationException("Live PR eligibility contract is incomplete.");
                var cleanup = attempt?["stage"]?.GetValue<string>() == "completed-verified" && !Reusable(attempt);
                if (!cleanup && live["status"]!.GetValue<string>() != "active")
                {
                    await SaveAsync(() => entry["status"] = attempt is null ? "closed" : "closed-held");
                    return;
                }
                // An owner whose review turn is over finishes on the head and merge it reviewed, whatever moved
                // since; a new head is then picked for a fresh review.
                var reviewFinished = attempt is not null && !Reusable(attempt) && ReviewRunner.ReviewFinished(attempt);
                if (!cleanup && !reviewFinished && live["headSha"]!.GetValue<string>() != head)
                {
                    await SaveAsync(() => entry["status"] = attempt is null ? "stale" : "held");
                    return;
                }
                var freshAdmission =
                    attempt is null || (attempt["stage"]?.GetValue<string>() == "failed-released" && Reusable(attempt));
                // Only a resumed owner still reviewing must keep its prepared merge. A fresh admission prepares a
                // new one, so a released failure's old merge must not defer it: the target branch moves on, and
                // the PR was retried until held. A finished owner needs no live merge at all.
                if (
                    !cleanup
                    && (
                        live["isDraft"]!.GetValue<bool>()
                        || (
                            !reviewFinished
                            && (
                                live["mergeSha"]?.GetValue<string>() is not { Length: 40 }
                                || (
                                    !freshAdmission
                                    && attempt?["mergeSha"]?.GetValue<string>() is { } merge
                                    && merge != live["mergeSha"]!.GetValue<string>()
                                )
                            )
                        )
                    )
                )
                    throw new InvalidOperationException(
                        "PR draft or prepared merge is unavailable/changed; retry deferred."
                    );
                if (
                    freshAdmission
                    && maxAgeHours is { } limit
                    && !PrSelection.IsWithinAge(
                        live["createdAt"],
                        (await ReadAsync(database, "reviewd/pr-snapshot/" + PrCommands.ParseRepo(repo).NormalizedKey))?[
                            pr
                        ],
                        live["headSha"]?.GetValue<string>(),
                        limit,
                        now(),
                        attempt
                    )
                )
                {
                    await SaveAsync(() =>
                    {
                        entry["status"] = "age-excluded";
                        entry["reason"] =
                            "PR age (creation or last push) is outside the configured limit or unavailable; no admission performed.";
                        entry["ageCheckedAt"] = now();
                    });
                    return;
                }
                var arguments = new List<string>
                {
                    "review",
                    "run",
                    "--repo",
                    repo,
                    "--pr",
                    pr,
                    "--slot",
                    slot.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "--db",
                    database,
                    "--session",
                    options.Required("session"),
                    "--workspace",
                    workspace,
                    "--provider",
                    options.Required("provider"),
                    "--mode",
                    options.Get("mode") ?? config.ModeId,
                    "--head",
                    head,
                    "--execute",
                };
                if (options.Get("reasoning-effort") is { } effort)
                    arguments.AddRange(["--reasoning-effort", effort]);
                if (maxAgeHours is { } maximumAge)
                    arguments.AddRange([
                        "--max-age-hours",
                        maximumAge.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ]);
                if (attempt is not null && attempt["stage"]?.GetValue<string>() != "failed-released")
                    arguments.Add("--resume");
                token.ThrowIfCancellationRequested();
                if (!await ClaimSlotAsync(repo, slot, entry))
                {
                    Console.Error.WriteLine(
                        $"PR {repo}/{pr} not started: slot {slot} was claimed elsewhere first. It waits for a free slot."
                    );
                    return;
                }
                if (token.IsCancellationRequested)
                {
                    await SaveAsync(() => entry["status"] = "queued");
                    return;
                }
                attemptBeforeLaunch = (await ReadAsync(database, AttemptKey(entry)))?.ToJsonString();
                launchedFresh = freshAdmission;
                launched = true;
                await run(CliOptions.Parse([.. arguments]));
                var receipt = await ReadAsync(database, ReceiptKey(entry));
                var finished = await ReadAsync(database, AttemptKey(entry));
                if (!Verified(receipt, entry) || !Verified(finished, entry) || !Reusable(finished!))
                    throw new InvalidOperationException("Batch receipt does not match the selected revision.");
                await SaveAsync(() =>
                {
                    entry["status"] = "verified";
                    entry.Remove("retryAt");
                });
                await ForgetFailuresAsync(entry);
                launchHolds[(repo, slot)] = 0;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var retained = await ReadAsync(database, AttemptKey(entry));
                // The claim writes the attempt row, so a host timeout or connection failure that left the row
                // as it was before launch failed before the claim: it left nothing behind and says nothing
                // about the PR. Retry it with backoff. Holding it parked nine PRs whose conversation lookup
                // timed out. The row may predate this launch (an earlier head's released review). An earlier
                // conversation still running (or a busy hierarchy read) defers the review the same way.
                var transientBeforeClaim =
                    retained?.ToJsonString() == attemptBeforeLaunch
                    && ex
                        is HttpRequestException
                            or TimeoutException
                            or TaskCanceledException
                            or ReviewRunner.ReviewDeferredException;
                // A gateway at its operation limit says nothing about the PR: a restart resumes every owner at
                // once and briefly exceeds it. Back off without spending the PR's retries, or admitted reviews
                // are held and their slots wait for the reaper.
                // A plain 503 while a lost commit awaits its replay is that commit's response lost again, not a
                // refusal at the limit, so it counts toward the lane's retries. Each resume renews the lease, so
                // backing it off without a bound kept the slot forever, out of the reaper's and the alarm's sight.
                var capacity =
                    ex is SandboxException { Kind: SandboxErrorKind.Unavailable, StatusCode: 503 }
                    && (
                        ReviewRunner.RefusedAtCapacity(ex)
                        || retained?[ReviewRunner.CommitReplay]?.GetValue<string>() is not { Length: > 0 }
                    );
                // A fresh admission that fails says something about the PR, so it is counted. One that released
                // its slot quarantines now; one still owning its slot is held below, and once the reaper frees the
                // slot the pre-launch check skips it, so a quarantine never strands an owned slot (seen live: a
                // prepare that timed out three times, each time holding its slot until the reaper freed it).
                // A resume is not counted: it repeats one admission, which only a fresh admission can replace
                // (seen live: a lost input re-verified five times). Neither is a held slot, which says nothing
                // about the PR.
                var failedTimes =
                    launchedFresh
                    && !transientBeforeClaim
                    && !capacity
                    && ex is not (ReviewRunner.ReviewNotAdmittedException or ReviewRunner.SlotUnavailableException)
                        ? await RecordFailureAsync(entry, ex.Message)
                        : 0;
                if (failedTimes >= QuarantineAfter && retained is not null && Reusable(retained))
                {
                    await SaveAsync(() =>
                    {
                        entry["status"] = "quarantined";
                        entry["reason"] = ex.Message;
                    });
                    Console.Error.WriteLine(
                        $"PR {repo}/{entry["pr"]} quarantined after {QuarantineAfter} identical failures on head {entry["head"]}: {ex.Message}. Skipped until a new push."
                    );
                    HoldSlotAfterRepeatedFailures();
                    return;
                }
                if (
                    (launched && !transientBeforeClaim && (retained is null || !Resumable(retained)))
                    || (!capacity && entry["failures"]!.GetValue<int>() + 1 >= MaxLaneRetries)
                )
                {
                    await SaveAsync(() =>
                    {
                        entry["status"] = "held";
                        entry["reason"] = ex.Message;
                    });
                    Console.Error.WriteLine(
                        $"PR {repo}/{entry["pr"]} held on lane {slot}: {ex.Message}. Skipped; the lane continues once its slot is free."
                    );
                    if (
                        launched
                        && ex is not (ReviewRunner.ReviewNotAdmittedException or ReviewRunner.ReviewDeferredException)
                    )
                        HoldSlotAfterRepeatedFailures();
                    return;
                }
                await SaveAsync(() =>
                {
                    var failures = entry["failures"]!.GetValue<int>() + (capacity ? 0 : 1);
                    var backoff = capacity ? (entry["capacityRetries"]?.GetValue<int>() ?? 0) + 1 : failures;
                    entry["failures"] = failures;
                    if (capacity)
                        entry["capacityRetries"] = backoff;
                    entry["status"] = "retry";
                    entry["reason"] = ex.Message;
                    entry["retryAt"] = now()
                        .AddSeconds(Math.Min(300, 15 * Math.Pow(2, Math.Min(backoff - 1, 5))))
                        .ToUnixTimeSeconds();
                    entry["order"] = state!["entries"]!.AsArray().Max(value => value!["order"]!.GetValue<long>()) + 1;
                });
                Console.Error.WriteLine(
                    $"PR {repo}/{entry["pr"]} requeued: {ex.Message}. Existing ownership retained."
                );
            }

            // Several launches failing in a row on one slot points at the slot, not the PRs: stop using it until the
            // next selection rather than burn the queue on it.
            void HoldSlotAfterRepeatedFailures()
            {
                var holds = launchHolds.AddOrUpdate((repo, slot), 1, (_, count) => count + 1);
                if (holds < MaxLaneRetries)
                    return;
                stopped[(repo, slot)] = true;
                Console.Error.WriteLine($"Lane {repo}/{slot} held: {holds} launches failed in a row.");
            }
        }

        // Claims the PR and its slot in one transaction: the slot must not be owned by another PR, and the queue
        // records the launch before the runner starts.
        async Task<bool> ClaimSlotAsync(string repo, int slot, JsonObject entry)
        {
            await writes.WaitAsync();
            try
            {
                using var connection = Connection(database, SqliteOpenMode.ReadWrite);
                await connection.OpenAsync();
                using var transaction = connection.BeginTransaction();
                using (var query = connection.CreateCommand())
                {
                    query.Transaction = transaction;
                    query.CommandText = "SELECT value FROM cache WHERE key=@key";
                    query.Parameters.AddWithValue(
                        "@key",
                        SlotRelease.OwnerKey(workspace, PrCommands.ParseRepo(repo).NormalizedKey, slot)
                    );
                    if (
                        await query.ExecuteScalarAsync() is string raw
                        && JsonNode.Parse(raw) is { } owner
                        && owner["state"]?.GetValue<string>() != "released"
                        && owner["prId"]?.GetValue<string>() != entry["pr"]!.GetValue<string>()
                    )
                        return false;
                }
                entry["status"] = "launching";
                entry["slot"] = slot;
                state!.Remove("idle");
                await WriteAsync(connection, transaction, state);
                await transaction.CommitAsync();
                return true;
            }
            catch (Exception ex)
            {
                Suspend(ex);
                throw;
            }
            finally
            {
                writes.Release();
            }
        }

        // Counts consecutive identical failures of one head; a different failure or a new head starts the count over.
        // Repeats closer together than QuarantineSpacing count once, so a short outage that fails every retry of one
        // selection is not mistaken for a PR that fails every selection.
        async Task<int> RecordFailureAsync(JsonObject entry, string message)
        {
            await writes.WaitAsync();
            try
            {
                var key = FailureKey(entry);
                var head = entry["head"]!.GetValue<string>();
                var signature = FailureSignature(message);
                var previous = await ReadAsync(database, key);
                var same =
                    previous?["head"]?.GetValue<string>() == head
                    && previous["signature"]?.GetValue<string>() == signature;
                if (same && previous!["at"]?.GetValue<DateTimeOffset>() is { } at && now() - at < QuarantineSpacing)
                    return previous["count"]!.GetValue<int>();
                var count = same ? previous!["count"]!.GetValue<int>() + 1 : 1;
                using var connection = Connection(database, SqliteOpenMode.ReadWrite);
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText =
                    "INSERT INTO cache(key,value) VALUES(@key,@value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
                command.Parameters.AddWithValue("@key", key);
                command.Parameters.AddWithValue(
                    "@value",
                    new JsonObject
                    {
                        ["head"] = head,
                        ["signature"] = signature,
                        ["count"] = count,
                        ["reason"] = message,
                        ["at"] = now(),
                    }.ToJsonString()
                );
                await command.ExecuteNonQueryAsync();
                return count;
            }
            finally
            {
                writes.Release();
            }
        }

        // A verified review settles the PR, so its failure history (and any quarantine of an older head) goes.
        async Task ForgetFailuresAsync(JsonObject entry)
        {
            await writes.WaitAsync();
            try
            {
                using var connection = Connection(database, SqliteOpenMode.ReadWrite);
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM cache WHERE key=@key";
                command.Parameters.AddWithValue("@key", FailureKey(entry));
                await command.ExecuteNonQueryAsync();
            }
            finally
            {
                writes.Release();
            }
        }

        async Task SaveAsync(Action update)
        {
            await writes.WaitAsync();
            try
            {
                update();
                using var connection = Connection(database, SqliteOpenMode.ReadWrite);
                await connection.OpenAsync();
                await WriteAsync(connection, null, state!);
            }
            catch (Exception ex)
            {
                Suspend(ex);
                throw;
            }
            finally
            {
                writes.Release();
            }
        }

        // The queue no longer matches what runs, so nothing new may start: admitted reviews finish, and admission
        // resumes with the next selection.
        void Suspend(Exception ex)
        {
            if (admitting is not { IsCancellationRequested: false } source)
                return;
            Console.Error.WriteLine(
                $"Scheduling suspended: queue {queueKey} not saved: {ex.Message}. Admitted reviews finish; nothing new starts until the selection changes."
            );
            source.Cancel();
        }

        async Task WriteAsync(SqliteConnection connection, SqliteTransaction? transaction, JsonObject queue)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO cache(key,value) VALUES(@key,@value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            command.Parameters.AddWithValue("@key", queueKey);
            command.Parameters.AddWithValue("@value", queue.ToJsonString());
            await command.ExecuteNonQueryAsync();
        }
    }

    private static string Identity(string repo, string pr, string head) => repo + "/" + pr + "@" + head;

    private static string Identity(JsonObject entry) =>
        Identity(entry["repo"]!.GetValue<string>(), entry["pr"]!.GetValue<string>(), entry["head"]!.GetValue<string>());

    // An entry names a slot only while it may own one: launching there, its retained attempt owns it, or a launch
    // there was interrupted. Any other entry takes the lowest free slot when it starts.
    private static void NormalizeSlot(JsonObject entry, JsonNode? attempt)
    {
        if (attempt is not null && !Reusable(attempt) && attempt["slot"]?.GetValue<int>() is { } owned)
            entry["slot"] = owned;
        else if (
            entry["status"]?.GetValue<string>() != "launching"
            && !(
                entry["status"]?.GetValue<string>() == "held"
                && entry["reason"]?.GetValue<string>() == InterruptedLaunch
            )
        )
            entry.Remove("slot");
    }

    private static int? MaxAgeHours(CliOptions options) =>
        options.Get("max-age-hours") is { } age
            ? int.Parse(age, System.Globalization.CultureInfo.InvariantCulture)
            : null;

    private static int? ValidateAgePolicy(CliOptions options, JsonObject selection)
    {
        var maxAgeHours = MaxAgeHours(options);
        if (selection.ContainsKey("maxAgeHours") || maxAgeHours is not null)
        {
            if (
                selection["maxAgeHours"] is not JsonValue ageValue
                || !ageValue.TryGetValue<int>(out var manifestAge)
                || manifestAge <= 0
                || manifestAge != maxAgeHours
            )
                throw new InvalidOperationException(
                    "Manifest creation-age policy must exactly match --max-age-hours; use a new immutable selection."
                );
        }
        return maxAgeHours;
    }

    // A PR that keeps failing is skipped (held) instead of looping on one lane forever.
    private const int MaxLaneRetries = 5;

    // The same failure on the same head three times will not clear by retrying: skip the PR until it is pushed again
    // (a new head) rather than spend a lane on it every selection (seen live: 88 tries on one PR).
    internal const int QuarantineAfter = 3;

    // Longer than one selection's retries (about 8 minutes of backoff), shorter than the keep-alive's next pick.
    internal static readonly TimeSpan QuarantineSpacing = TimeSpan.FromMinutes(20);

    internal static string FailureKey(string repo, string pr) =>
        "reviewd/failure/" + PrCommands.ParseRepo(repo).NormalizedKey + "/" + pr;

    private static string FailureKey(JsonObject entry) =>
        FailureKey(entry["repo"]!.GetValue<string>(), entry["pr"]!.GetValue<string>());

    // Operation ids, claim tokens, SHAs and times differ between tries of one failure; the remaining text names it.
    // The sandbox output tail is cut at a fixed length, so its first lines shift between tries: keep the headline and
    // the tail's last line, which names the error.
    internal static string FailureSignature(string message)
    {
        var parts = message.Split(SandboxTail, 2);
        var text = parts.Length == 2 ? parts[0] + SandboxTail + parts[1].TrimEnd().Split('\n')[^1] : message;
        return Regex.Replace(Regex.Replace(text, "[0-9a-fA-F]{8,}", "#"), "[0-9]+", "N");
    }

    private const string SandboxTail = "\n--- sandbox output tail ---\n";

    internal static bool IsQuarantined(JsonNode? failure, string? head) =>
        head is not null
        && failure?["head"]?.GetValue<string>() == head
        && failure["count"]?.GetValue<int>() >= QuarantineAfter;

    private const string InterruptedLaunch = "Interrupted launch has no definitive non-admission evidence.";

    private static bool Reusable(JsonNode attempt) =>
        attempt["cleanupState"]?.GetValue<string>() == "released"
        && attempt["claimToken"]?.GetValue<string>() is { Length: > 0 };

    private static bool Resumable(JsonNode attempt) =>
        (attempt["stage"]?.GetValue<string>() == "failed-released" && Reusable(attempt))
        || (
            attempt["stage"]?.GetValue<string>() == "completed-verified"
            && attempt["claimToken"]?.GetValue<string>() is { Length: > 0 }
            && !Reusable(attempt)
        )
        || (
            attempt["stage"]?.GetValue<string>()
                is "review-running"
                    or "review-completed"
                    or "confirmation-running"
                    or "confirmation-completed"
                    or "verify-pending"
            && attempt["reviewInputId"]?.GetValue<string>() is { Length: > 0 }
            && (
                attempt["confirmationIdempotencyKey"] is null
                || attempt["confirmationInputId"]?.GetValue<string>() is { Length: > 0 }
            )
            && (
                attempt["stage"]!.GetValue<string>().StartsWith("review-", StringComparison.Ordinal)
                || attempt["confirmationInputId"]?.GetValue<string>() is { Length: > 0 }
            )
        );

    private static bool Verified(JsonNode? receipt, JsonObject entry) =>
        receipt?["stage"]?.GetValue<string>() == "completed-verified"
        && receipt["reviewedHeadSha"]?.GetValue<string>() == entry["head"]!.GetValue<string>()
        && receipt["artifactCommit"]?["status"]?.GetValue<string>() is "committed" or "unchanged"
        && receipt["artifactCommit"]?["commit"]?.GetValue<string>() is { Length: 40 };

    private static string AttemptKey(string repo, string pr) =>
        "reviewd/review-attempt/" + PrCommands.ParseRepo(repo).NormalizedKey + "/" + pr;

    private static string AttemptKey(JsonObject entry) =>
        AttemptKey(entry["repo"]!.GetValue<string>(), entry["pr"]!.GetValue<string>());

    private static string ReceiptKey(JsonObject entry) =>
        "reviewd/review-run/"
        + PrCommands.ParseRepo(entry["repo"]!.GetValue<string>()).NormalizedKey
        + "/"
        + entry["pr"]!.GetValue<string>();

    private static SqliteConnection Connection(string database, SqliteOpenMode mode) =>
        new(new SqliteConnectionStringBuilder { DataSource = database, Mode = mode }.ToString());

    private static async Task<JsonNode?> ReadAsync(string database, string key) =>
        await ReadRawAsync(database, key) is { } raw ? JsonNode.Parse(raw) : null;

    private static async Task<string?> ReadRawAsync(string database, string key)
    {
        using var connection = Connection(database, SqliteOpenMode.ReadOnly);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM cache WHERE key=@key";
        command.Parameters.AddWithValue("@key", key);
        return await command.ExecuteScalarAsync() as string;
    }
}
