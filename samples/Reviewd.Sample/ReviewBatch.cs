using System.Text.Json.Nodes;
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
                string? previous = null;
                // Startup sweep: reclaim any slot a prior (crashed) batch left owned — including legacy
                // owners claimed before leasing existed — so restarting the batch always clears jams.
                await SweepSlotsAsync(options, config, undatedExpired: true, token);
                // Periodic sweep on its own timer: a queue can drain for hours, and a lane waiting on a
                // lapsed owner only resumes once the reaper frees that slot. Sweeping between queues
                // alone left such lanes waiting until they gave up.
                var reaper = SweepPeriodicallyAsync(
                    cancellation => SweepSlotsAsync(options, config, undatedExpired: false, cancellation),
                    (delay, cancellation) => Task.Delay(delay, cancellation),
                    token
                );
                while (!token.IsCancellationRequested)
                {
                    var text = await File.ReadAllTextAsync(options.Required("selection"), token);
                    var digest = SlotRelease.Digest(text);
                    if (!options.Has("execute"))
                        throw new InvalidOperationException("review batch requires --execute; no state was changed.");
                    if (digest != previous)
                    {
                        previous = digest;
                        try
                        {
                            using var lease = ReviewRunner.AcquireLease(
                                options.Required("db"),
                                "batch/" + options.Required("workspace")
                            );
                            _ = ValidateAgePolicy(options, JsonNode.Parse(text)!.AsObject());
                            await ClaimAsync(options.Required("db"), digest, text, token);
                            _ = config.CreateS2SClient();
                            await ExecuteAsync(
                                options,
                                config,
                                text,
                                (repo, pr, slot, head, cancellation) =>
                                    ReviewRunner.CheckEligibilityAsync(
                                        options,
                                        config,
                                        repo,
                                        pr,
                                        slot,
                                        head,
                                        cancellation
                                    ),
                                run => ReviewRunner.RunAsync(run, config, CancellationToken.None),
                                EnsureNoDaemon,
                                (delay, cancellation) => Task.Delay(delay, cancellation),
                                () => DateTimeOffset.UtcNow,
                                token
                            );
                        }
                        catch (Exception ex) when (!token.IsCancellationRequested)
                        {
                            Console.Error.WriteLine(
                                $"Batch parked: {ex.Message}. Auth remains running; retained work was not replayed."
                            );
                        }
                    }
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                }
                await reaper;
            }
        );

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

    private static void EnsureNoDaemon() =>
        EnsureNoDaemon(() => Directory.EnumerateDirectories("/proc"), File.ReadAllBytes);

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
            var arguments = System.Text.Encoding.UTF8.GetString(commandLine).Split('\0');
            if (
                arguments.Any(argument =>
                    Path.GetFileName(argument) is "CodeReviewDaemon.Sample" or "CodeReviewDaemon.Sample.dll"
                )
            )
                throw new InvalidOperationException("Competing daemon producer detected.");
        }
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

    internal static async Task ExecuteAsync(
        CliOptions options,
        ReviewdConfig config,
        string text,
        Func<string, string, int, string, CancellationToken, Task<JsonObject>> eligibility,
        Func<CliOptions, Task<int>> run,
        Action checkProducer,
        Func<TimeSpan, CancellationToken, Task> delay,
        Func<DateTimeOffset> now,
        CancellationToken ct
    )
    {
        if (!options.Has("execute"))
            throw new InvalidOperationException("review batch requires --execute.");
        var database = options.Required("db");
        var stateKey = "reviewd/batch-state/" + SlotRelease.Digest(text);
        var selection = JsonNode.Parse(text)!.AsObject();
        var maxAgeHours = ValidateAgePolicy(options, selection);
        var scope = new JsonObject
        {
            ["workspace"] = options.Required("workspace"),
            ["session"] = options.Required("session"),
            ["provider"] = options.Required("provider"),
            ["mode"] = options.Get("mode") ?? config.ModeId,
        };
        if (options.Get("reasoning-effort") is { } reasoningEffort)
            scope["reasoningEffort"] = reasoningEffort;
        if (maxAgeHours is { } hours)
            scope["maxAgeHours"] = hours;
        var state = await ReadAsync(database, stateKey) as JsonObject;
        if (state is null)
        {
            var entries = new JsonArray();
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (repo, rows) in selection["repositories"]!.AsObject())
            {
                _ = PrCommands.ParseRepo(repo);
                var slots = selection["lanes"]![repo]!.AsArray().Select(value => value!.GetValue<int>()).ToArray();
                if (
                    slots.Length == 0
                    || slots.Distinct().Count() != slots.Length
                    || slots.Any(slot => slot is < 0 or > 5)
                )
                    throw new InvalidOperationException("Invalid batch lanes.");
                var index = 0;
                foreach (var row in rows!.AsArray())
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
                    // A PR this db already ran keeps its earlier lane, so one long-lived db can feed many
                    // selections; only an owner retained on a lane outside this selection is a conflict.
                    var attempt = await ReadAsync(database, AttemptKey(repo, pr));
                    var next = slots[index++ % slots.Length];
                    var slot =
                        attempt?["slot"]?.GetValue<int>() is { } earlier && slots.Contains(earlier) ? earlier : next;
                    if (attempt is not null && attempt["slot"]!.GetValue<int>() != slot && !Reusable(attempt))
                        throw new InvalidOperationException(
                            "Legacy batch slot assignment conflicts with retained ownership; reconcile before admission."
                        );
                    entries.Add(
                        new JsonObject
                        {
                            ["repo"] = repo,
                            ["pr"] = pr,
                            ["head"] = head,
                            ["slot"] = slot,
                            ["status"] = "queued",
                            ["failures"] = 0,
                            ["order"] = (long)entries.Count,
                        }
                    );
                }
            }
            state = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["scope"] = scope,
                ["entries"] = entries,
            };
        }
        else if (state["schemaVersion"]?.GetValue<int>() != 1 || !JsonNode.DeepEquals(state["scope"], scope))
            throw new InvalidOperationException("Persisted batch execution scope changed; no admission allowed.");
        using var writes = new SemaphoreSlim(1, 1);
        using var scheduling = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var watching = new CancellationTokenSource();
        var entriesToRun = state["entries"]!.AsArray().Select(entry => entry!.AsObject()).ToArray();
        foreach (var entry in entriesToRun)
        {
            var receipt = await ReadAsync(database, ReceiptKey(entry));
            var attempt = await ReadAsync(database, AttemptKey(entry));
            if (attempt is not null)
            {
                using var releaseConnection = Connection(database, SqliteOpenMode.ReadOnly);
                await releaseConnection.OpenAsync(ct);
                using var releaseQuery = releaseConnection.CreateCommand();
                releaseQuery.CommandText = "SELECT value FROM cache WHERE key=@key";
                releaseQuery.Parameters.AddWithValue("@key", AttemptKey(entry));
                var raw = (string)(await releaseQuery.ExecuteScalarAsync(ct))!;
                var release = await ReadAsync(database, SlotRelease.Key(raw));
                if (
                    release is not null
                    && SlotRelease.IsResetRetirement(release, raw, options.Required("session"))
                    && SlotRelease.Allows(release, raw, "different-pr", options.Required("session"))
                )
                {
                    entry["status"] = "retired";
                    continue;
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
        }
        await SaveAsync(() => { });
        checkProducer();
        var watcher = WatchAsync();
        try
        {
            await Task.WhenAll(
                entriesToRun
                    .GroupBy(entry => (Repo: entry["repo"]!.GetValue<string>(), Slot: entry["slot"]!.GetValue<int>()))
                    .Select(lane => GuardLaneAsync(lane.Key.Repo, lane.Key.Slot, [.. lane]))
            );
        }
        finally
        {
            watching.Cancel();
            await watcher;
        }
        Console.WriteLine("Batch scheduling finished. OAuth callback remains running.");

        async Task WatchAsync()
        {
            try
            {
                while (true)
                {
                    await delay(TimeSpan.FromSeconds(5), watching.Token);
                    checkProducer();
                }
            }
            catch (OperationCanceledException) when (watching.IsCancellationRequested) { }
            catch (Exception ex)
            {
                scheduling.Cancel();
                Console.Error.WriteLine($"Scheduling suspended: {ex.Message}. Admitted work and auth retained.");
            }
        }

        async Task GuardLaneAsync(string repo, int slot, JsonObject[] lane)
        {
            try
            {
                await LaneAsync(repo, slot, lane);
            }
            catch (OperationCanceledException) when (scheduling.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Lane {repo}/{slot} held: {ex.Message}. Other safe slots continue.");
            }
        }

        async Task LaneAsync(string repo, int slot, JsonObject[] lane)
        {
            DateTimeOffset? heldSince = null;
            var launchHolds = 0;
            while (!scheduling.IsCancellationRequested)
            {
                // A held PR is skipped, never a lane stop. While its retained owner still holds the slot,
                // wait for the reaper to reclaim it (its lease lapses once nothing runs), then move on.
                // A slot the reaper cannot free within the wait bound stays held for an operator.
                var slotHeld = false;
                foreach (
                    var held in lane.Where(value => value["status"]!.GetValue<string>() is "held" or "closed-held")
                )
                {
                    if (
                        held["reason"]?.GetValue<string>() == InterruptedLaunch
                        || (await ReadAsync(database, AttemptKey(held)) is { } retainedHeld && !Reusable(retainedHeld))
                    )
                    {
                        slotHeld = true;
                        break;
                    }
                }
                // A slot still owned by a PR outside this lane (left by an earlier batch) refuses every
                // launch, so the lane would burn its queue in seconds. Wait for the reaper instead.
                if (
                    !slotHeld
                    && await ReadAsync(
                        database,
                        SlotRelease.OwnerKey(
                            options.Required("workspace"),
                            PrCommands.ParseRepo(repo).NormalizedKey,
                            slot
                        )
                    )
                        is { } owner
                    && owner["state"]?.GetValue<string>() != "released"
                    && !lane.Any(value => value["pr"]!.GetValue<string>() == owner["prId"]?.GetValue<string>())
                )
                    slotHeld = true;
                if (slotHeld)
                {
                    heldSince ??= now();
                    if (now() - heldSince >= HeldSlotWait)
                    {
                        Console.Error.WriteLine(
                            $"Lane {repo}/{slot} held: its retained owner was not reclaimed. Other safe slots continue."
                        );
                        return;
                    }
                    await delay(TimeSpan.FromMinutes(1), scheduling.Token);
                    continue;
                }
                heldSince = null;
                var candidates = lane.Where(entry =>
                        entry["status"]!.GetValue<string>()
                            is not (
                                "verified"
                                or "closed"
                                or "stale"
                                or "retired"
                                or "age-excluded"
                                or "held"
                                or "closed-held"
                            )
                    )
                    .ToArray();
                if (candidates.Length == 0)
                    return;
                JsonObject? entry = null;
                JsonNode? attempt = null;
                foreach (var candidate in candidates)
                {
                    var retained = await ReadAsync(database, AttemptKey(candidate));
                    if (retained is not null && !Reusable(retained))
                    {
                        if (entry is not null)
                        {
                            await SaveAsync(() =>
                            {
                                candidate["status"] = "held";
                                candidate["reason"] = "Multiple retained owners in one slot.";
                            });
                            break;
                        }
                        entry = candidate;
                        attempt = retained;
                    }
                }
                if (candidates.Any(value => value["status"]!.GetValue<string>() == "held"))
                    continue;
                entry ??= candidates.OrderBy(value => value["order"]!.GetValue<long>()).First();
                attempt ??= await ReadAsync(database, AttemptKey(entry));
                // AttemptKey is per repo/pr, not per head. A completed-verified attempt whose slot
                // was already released belongs to an EARLIER head of this PR; once the PR advances,
                // its new head is a fresh review, not a stuck retry. Drop the stale attempt so the
                // lane admits the new head below instead of parking it as an unresumable outcome.
                if (
                    attempt is not null
                    && attempt["stage"]?.GetValue<string>() == "completed-verified"
                    && Reusable(attempt)
                    && attempt["reviewedHeadSha"]?.GetValue<string>() != entry["head"]!.GetValue<string>()
                )
                    attempt = null;
                if (attempt is not null && !Resumable(attempt))
                {
                    await SaveAsync(() =>
                    {
                        entry["status"] = "held";
                        entry["reason"] ??= "Retained outcome requires reconciliation; slot remains owned.";
                    });
                    continue;
                }
                var retryAt = entry["retryAt"]?.GetValue<long>() ?? 0;
                if (retryAt > now().ToUnixTimeSeconds())
                {
                    await delay(TimeSpan.FromSeconds(retryAt - now().ToUnixTimeSeconds()), scheduling.Token);
                    continue;
                }
                var launched = false;
                string? attemptBeforeLaunch = null;
                try
                {
                    try
                    {
                        checkProducer();
                    }
                    catch
                    {
                        scheduling.Cancel();
                        throw;
                    }
                    var pr = entry["pr"]!.GetValue<string>();
                    var head = entry["head"]!.GetValue<string>();
                    var live = await eligibility(repo, pr, slot, head, scheduling.Token);
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
                        continue;
                    }
                    if (!cleanup && live["headSha"]!.GetValue<string>() != head)
                    {
                        await SaveAsync(() => entry["status"] = attempt is null ? "stale" : "held");
                        continue;
                    }
                    var freshAdmission =
                        attempt is null
                        || (attempt["stage"]?.GetValue<string>() == "failed-released" && Reusable(attempt));
                    // Only a resumed owner must keep its prepared merge. A fresh admission prepares a new one, so
                    // a released failure's old merge must not defer it: the target branch moves on, and the PR was
                    // retried until held.
                    if (
                        !cleanup
                        && (
                            live["isDraft"]!.GetValue<bool>()
                            || live["mergeSha"]?.GetValue<string>() is not { Length: 40 }
                            || (
                                !freshAdmission
                                && attempt?["mergeSha"]?.GetValue<string>() is { } merge
                                && merge != live["mergeSha"]!.GetValue<string>()
                            )
                        )
                    )
                        throw new InvalidOperationException(
                            "PR draft or prepared merge is unavailable/changed; retry deferred."
                        );
                    if (
                        freshAdmission
                        && maxAgeHours is { } limit
                        && !PrSelection.IsWithinCreationAge(live["createdAt"], limit, now())
                    )
                    {
                        await SaveAsync(() =>
                        {
                            entry["status"] = "age-excluded";
                            entry["reason"] =
                                "PR creation age is outside the configured limit or unavailable; no admission performed.";
                            entry["ageCheckedAt"] = now();
                        });
                        continue;
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
                        options.Required("workspace"),
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
                    scheduling.Token.ThrowIfCancellationRequested();
                    await SaveAsync(() => entry["status"] = "launching");
                    if (scheduling.IsCancellationRequested)
                    {
                        await SaveAsync(() => entry["status"] = "queued");
                        return;
                    }
                    attemptBeforeLaunch = (await ReadAsync(database, AttemptKey(entry)))?.ToJsonString();
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
                    launchHolds = 0;
                }
                catch (OperationCanceledException) when (scheduling.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    var retained = await ReadAsync(database, AttemptKey(entry));
                    // The claim writes the attempt row, so a host timeout or connection failure that left the row
                    // as it was before launch failed before the claim: it left nothing behind and says nothing
                    // about the PR. Retry it with backoff. Holding it parked nine PRs whose conversation lookup
                    // timed out. The row may predate this launch (an earlier head's released review).
                    var transientBeforeClaim =
                        retained?.ToJsonString() == attemptBeforeLaunch
                        && ex is HttpRequestException or TimeoutException or TaskCanceledException;
                    // A gateway at its operation limit says nothing about the PR: a restart resumes every owner at
                    // once and briefly exceeds it. Back off without spending the PR's retries, or admitted reviews
                    // are held and their slots wait for the reaper.
                    var capacity = ex is SandboxException { Kind: SandboxErrorKind.Unavailable, StatusCode: 503 };
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
                        // Several launches failing in a row points at the slot, not the PRs: stop the lane
                        // rather than burn the rest of its queue.
                        if (
                            launched
                            && ex is not ReviewRunner.ReviewNotAdmittedException
                            && ++launchHolds >= MaxLaneRetries
                        )
                        {
                            Console.Error.WriteLine(
                                $"Lane {repo}/{slot} held: {launchHolds} launches failed in a row."
                            );
                            return;
                        }
                        continue;
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
                        entry["order"] = entriesToRun.Max(value => value["order"]!.GetValue<long>()) + 1;
                    });
                    Console.Error.WriteLine(
                        $"PR {repo}/{entry["pr"]} requeued: {ex.Message}. Existing ownership retained."
                    );
                }
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
                using var command = connection.CreateCommand();
                command.CommandText =
                    "INSERT INTO cache(key,value) VALUES(@key,@value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
                command.Parameters.AddWithValue("@key", stateKey);
                command.Parameters.AddWithValue("@value", state.ToJsonString());
                await command.ExecuteNonQueryAsync();
            }
            catch
            {
                scheduling.Cancel();
                throw;
            }
            finally
            {
                writes.Release();
            }
        }
    }

    private static int? ValidateAgePolicy(CliOptions options, JsonObject selection)
    {
        var maxAgeHours = options.Get("max-age-hours") is { } age
            ? int.Parse(age, System.Globalization.CultureInfo.InvariantCulture)
            : (int?)null;
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

    private const string InterruptedLaunch = "Interrupted launch has no definitive non-admission evidence.";

    // Long enough for a lapsed lease plus a few 5-minute reaper sweeps.
    private static readonly TimeSpan HeldSlotWait = TimeSpan.FromHours(2);

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

    private static async Task<JsonNode?> ReadAsync(string database, string key)
    {
        using var connection = Connection(database, SqliteOpenMode.ReadOnly);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM cache WHERE key=@key";
        command.Parameters.AddWithValue("@key", key);
        return await command.ExecuteScalarAsync() is string raw ? JsonNode.Parse(raw) : null;
    }
}
