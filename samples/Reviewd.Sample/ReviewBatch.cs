using System.Text.Json.Nodes;
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
            }
        );

    private static void EnsureNoDaemon() =>
        EnsureNoDaemon(() => Directory.EnumerateDirectories("/proc"), File.ReadAllBytes);

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
        var scope = new JsonObject
        {
            ["workspace"] = options.Required("workspace"),
            ["session"] = options.Required("session"),
            ["provider"] = options.Required("provider"),
            ["mode"] = options.Get("mode") ?? config.ModeId,
        };
        if (options.Get("reasoning-effort") is { } reasoningEffort)
            scope["reasoningEffort"] = reasoningEffort;
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
                    var slot = slots[index++ % slots.Length];
                    var attempt = await ReadAsync(database, AttemptKey(repo, pr));
                    if (attempt is not null && attempt["slot"]!.GetValue<int>() != slot)
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
                entry["reason"] = "Interrupted launch has no definitive non-admission evidence.";
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
            while (!scheduling.IsCancellationRequested)
            {
                var candidates = lane.Where(entry =>
                        entry["status"]!.GetValue<string>() is not ("verified" or "closed" or "stale" or "retired")
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
                            return;
                        }
                        entry = candidate;
                        attempt = retained;
                    }
                }
                entry ??=
                    candidates.FirstOrDefault(value => value["status"]!.GetValue<string>() is "held" or "closed-held")
                    ?? candidates.OrderBy(value => value["order"]!.GetValue<long>()).First();
                attempt ??= await ReadAsync(database, AttemptKey(entry));
                if (
                    entry["status"]!.GetValue<string>() is "held" or "closed-held"
                    || (attempt is not null && !Resumable(attempt))
                )
                {
                    await SaveAsync(() =>
                    {
                        entry["status"] = "held";
                        entry["reason"] ??= "Retained outcome requires reconciliation; slot remains owned.";
                    });
                    return;
                }
                var retryAt = entry["retryAt"]?.GetValue<long>() ?? 0;
                if (retryAt > now().ToUnixTimeSeconds())
                {
                    await delay(TimeSpan.FromSeconds(retryAt - now().ToUnixTimeSeconds()), scheduling.Token);
                    continue;
                }
                var launched = false;
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
                        if (attempt is not null)
                            return;
                        continue;
                    }
                    if (!cleanup && live["headSha"]!.GetValue<string>() != head)
                    {
                        await SaveAsync(() => entry["status"] = attempt is null ? "stale" : "held");
                        if (attempt is not null)
                            return;
                        continue;
                    }
                    if (
                        !cleanup
                        && (
                            live["isDraft"]!.GetValue<bool>()
                            || live["mergeSha"]?.GetValue<string>() is not { Length: 40 }
                            || (
                                attempt?["mergeSha"]?.GetValue<string>() is { } merge
                                && merge != live["mergeSha"]!.GetValue<string>()
                            )
                        )
                    )
                        throw new InvalidOperationException(
                            "PR draft or prepared merge is unavailable/changed; retry deferred."
                        );
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
                    if (attempt is not null && attempt["stage"]?.GetValue<string>() != "failed-released")
                        arguments.Add("--resume");
                    scheduling.Token.ThrowIfCancellationRequested();
                    await SaveAsync(() => entry["status"] = "launching");
                    if (scheduling.IsCancellationRequested)
                    {
                        await SaveAsync(() => entry["status"] = "queued");
                        return;
                    }
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
                }
                catch (OperationCanceledException) when (scheduling.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    var retained = await ReadAsync(database, AttemptKey(entry));
                    if (launched && (retained is null || !Resumable(retained)))
                    {
                        await SaveAsync(() =>
                        {
                            entry["status"] = "held";
                            entry["reason"] = ex.Message;
                        });
                        Console.Error.WriteLine(
                            $"Lane {repo}/{slot} held: {ex.Message}. No replay; other safe slots continue."
                        );
                        return;
                    }
                    await SaveAsync(() =>
                    {
                        var failures = entry["failures"]!.GetValue<int>() + 1;
                        entry["failures"] = failures;
                        entry["status"] = "retry";
                        entry["reason"] = ex.Message;
                        entry["retryAt"] = now()
                            .AddSeconds(Math.Min(300, 15 * Math.Pow(2, Math.Min(failures - 1, 5))))
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
