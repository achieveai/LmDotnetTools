using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Reviewd.Sample;

internal static class SlotRelease
{
    internal static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal static string Key(string attempt) => "reviewd/slot-release/" + Digest(attempt);

    internal static string OwnerKey(string workspace, string repository, int slot) =>
        "reviewd/slot-owner/" + Digest(workspace + "/" + repository + "/" + slot);

    /// <summary>
    /// How long a fresh slot claim is leased before the reaper may reclaim it. A running review
    /// extends this each phase via <see cref="HeartbeatAsync"/>, so only a dead/stalled review lets
    /// the lease lapse. Generously longer than the gap between a review's phases.
    /// </summary>
    // A live review heartbeats this lease on every progress checkpoint (ReviewRunner.SaveAsync), so the
    // lease records when the review was last active. 30 min bounds how long a dead/abandoned slot can stay
    // owned before the reaper reclaims it; a still-running review is protected regardless by the reaper's
    // run-state pre-check, so this window can be short without risking a live review.
    internal static readonly TimeSpan DefaultLeaseWindow = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Key prefix of every slot-owner row, so the reaper can enumerate owners it must inspect.
    /// </summary>
    internal const string OwnerKeyPrefix = "reviewd/slot-owner/";

    /// <summary>
    ///     Claims the slot owner row and saves <paramref name="owner"/>, stamped with the claim token and
    ///     <c>cleanupState</c>, as the attempt at <paramref name="attemptKey"/> in the same transaction. A crash
    ///     can therefore never leave a durable claim whose attempt reconcile-slot cannot find.
    /// </summary>
    internal static async Task<string> ClaimAsync(
        SqliteConnection connection,
        string key,
        JsonObject owner,
        string? retainedToken,
        string attemptKey,
        CancellationToken ct,
        DateTimeOffset? nowUtc = null
    )
    {
        using var transaction = connection.BeginTransaction();
        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT value FROM cache WHERE key=@key";
        read.Parameters.AddWithValue("@key", key);
        var current = await read.ExecuteScalarAsync(ct) as string;
        var existing = current is null ? null : JsonNode.Parse(current);
        using var write = connection.CreateCommand();
        write.Transaction = transaction;
        write.CommandText =
            "INSERT INTO cache(key,value) VALUES(@key,@value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        write.Parameters.AddWithValue("@key", key);
        write.Parameters.AddWithValue("@value", "");
        string token;
        if (existing is not null && existing["state"]?.GetValue<string>() != "released")
        {
            if (
                retainedToken is null
                || existing["claimToken"]?.GetValue<string>() != retainedToken
                || existing["prId"]?.GetValue<string>() != owner["prId"]?.GetValue<string>()
                || existing["sessionId"]?.GetValue<string>() != owner["sessionId"]?.GetValue<string>()
            )
                throw new ReviewRunner.SlotUnavailableException(
                    "Selected slot has a durable owner; no admission or reset allowed.",
                    existing
                );
            token = retainedToken;
        }
        else
        {
            if (retainedToken is not null)
                throw new InvalidOperationException(
                    "Retained claim is no longer current; no cleanup or replay allowed."
                );
            token = Guid.NewGuid().ToString("N");
            var claim = owner.DeepClone().AsObject();
            claim["claimToken"] = token;
            claim["state"] = "owned";
            // Lease the claim. The reaper reclaims an owned slot only once this lapses, so a dead or
            // SIGKILL'd review never blocks the slot forever; a live review refreshes it each phase.
            claim["leaseExpiresAt"] = (nowUtc ?? DateTimeOffset.UtcNow) + DefaultLeaseWindow;
            write.Parameters["@value"].Value = claim.ToJsonString();
            await write.ExecuteNonQueryAsync(ct);
        }
        owner["claimToken"] = token;
        owner["cleanupState"] ??= "owned";
        write.Parameters["@key"].Value = attemptKey;
        write.Parameters["@value"].Value = owner.ToJsonString();
        await write.ExecuteNonQueryAsync(ct);
        transaction.Commit();
        return token;
    }

    /// <summary>
    /// True when an owner row is still held (<c>state=="owned"</c>) and its lease has lapsed at
    /// <paramref name="now"/>. An owner with no <c>leaseExpiresAt</c> (claimed before leasing
    /// existed) is reported expired only when <paramref name="undatedExpired"/> is set, so a reaper
    /// can opt in to reclaiming legacy jams while a routine sweep ignores them.
    /// </summary>
    internal static bool IsLeaseExpired(JsonObject owner, DateTimeOffset now, bool undatedExpired = false)
    {
        if (owner["state"]?.GetValue<string>() != "owned")
            return false;
        if (owner["leaseExpiresAt"]?.GetValue<DateTimeOffset>() is { } expiry)
            return expiry <= now;
        return undatedExpired;
    }

    /// <summary>
    /// Extends an owned slot's lease to <paramref name="nowUtc"/> + <see cref="DefaultLeaseWindow"/>,
    /// but only while the row is still owned on the exact claim token (a CAS). Returns false without
    /// throwing when the row was released or reclaimed out from under the caller, so a review's
    /// per-phase heartbeat never turns a lost race into a failure.
    /// </summary>
    internal static async Task<bool> HeartbeatAsync(
        SqliteConnection connection,
        string key,
        string token,
        CancellationToken ct,
        DateTimeOffset? nowUtc = null
    )
    {
        using var transaction = connection.BeginTransaction();
        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT value FROM cache WHERE key=@key";
        read.Parameters.AddWithValue("@key", key);
        var raw = await read.ExecuteScalarAsync(ct) as string;
        var claim = raw is null ? null : JsonNode.Parse(raw)!.AsObject();
        if (claim?["claimToken"]?.GetValue<string>() != token || claim["state"]?.GetValue<string>() != "owned")
            return false;
        claim["leaseExpiresAt"] = (nowUtc ?? DateTimeOffset.UtcNow) + DefaultLeaseWindow;
        using var write = connection.CreateCommand();
        write.Transaction = transaction;
        write.CommandText = "UPDATE cache SET value=@value WHERE key=@key AND value=@old";
        write.Parameters.AddWithValue("@key", key);
        write.Parameters.AddWithValue("@old", raw!);
        write.Parameters.AddWithValue("@value", claim.ToJsonString());
        var updated = await write.ExecuteNonQueryAsync(ct) == 1;
        transaction.Commit();
        return updated;
    }

    internal static async Task AssertCurrentAsync(
        SqliteConnection connection,
        string key,
        string token,
        CancellationToken ct
    )
    {
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT value FROM cache WHERE key=@key";
        read.Parameters.AddWithValue("@key", key);
        var current = await read.ExecuteScalarAsync(ct) as string;
        var claim = current is null ? null : JsonNode.Parse(current);
        if (claim?["claimToken"]?.GetValue<string>() != token || claim["state"]?.GetValue<string>() != "owned")
            throw new InvalidOperationException("Slot claim changed; no cleanup or release allowed.");
    }

    internal static async Task ReleaseAsync(
        SqliteConnection connection,
        string key,
        string token,
        string attemptKey,
        JsonObject attempt,
        CancellationToken ct,
        IReadOnlyList<string>? adoptedHistory = null
    )
    {
        using var transaction = connection.BeginTransaction();
        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT value FROM cache WHERE key=@key";
        read.Parameters.AddWithValue("@key", key);
        var raw = await read.ExecuteScalarAsync(ct) as string;
        var claim = raw is null ? null : JsonNode.Parse(raw)!.AsObject();
        if (claim?["claimToken"]?.GetValue<string>() != token || claim["state"]?.GetValue<string>() != "owned")
            throw new InvalidOperationException("Slot claim changed; exact-token release refused.");
        claim["state"] = "released";
        attempt["cleanupState"] = "released";
        using var write = connection.CreateCommand();
        write.Transaction = transaction;
        write.CommandText = "UPDATE cache SET value=@value WHERE key=@key AND value=@old";
        write.Parameters.AddWithValue("@key", key);
        write.Parameters.AddWithValue("@old", raw!);
        write.Parameters.AddWithValue("@value", claim.ToJsonString());
        if (await write.ExecuteNonQueryAsync(ct) != 1)
            throw new InvalidOperationException("Slot claim changed during release.");
        using var save = connection.CreateCommand();
        save.Transaction = transaction;
        save.CommandText =
            "INSERT INTO cache(key,value) VALUES(@key,@value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        save.Parameters.AddWithValue("@key", attemptKey);
        save.Parameters.AddWithValue("@value", attempt.ToJsonString());
        await save.ExecuteNonQueryAsync(ct);
        foreach (var historical in adoptedHistory ?? [])
        {
            save.Parameters["@key"].Value = "reviewd/legacy-cleaned/" + Digest(historical);
            save.Parameters["@value"].Value = new JsonObject
            {
                ["cleanupState"] = "released",
                ["claimToken"] = token,
                ["ownerPrId"] = attempt["prId"]?.DeepClone(),
                ["attemptDigest"] = Digest(historical),
            }.ToJsonString();
            await save.ExecuteNonQueryAsync(ct);
        }
        transaction.Commit();
    }

    internal static bool Allows(JsonNode proof, string attempt, string newPr, string newSession)
    {
        var owner = JsonNode.Parse(attempt)!;
        return proof["schemaVersion"]?.GetValue<int>() == 1
            && proof["attemptDigest"]?.GetValue<string>() == Digest(attempt)
            && proof["repository"]?.GetValue<string>() == owner["repository"]?.GetValue<string>()
            && proof["prId"]?.GetValue<string>() == owner["prId"]?.GetValue<string>()
            && proof["workspaceId"]?.GetValue<string>() == owner["workspaceId"]?.GetValue<string>()
            && proof["slot"]?.GetValue<int>() == owner["slot"]?.GetValue<int>()
            && proof["sessionId"]?.GetValue<string>() == owner["sessionId"]?.GetValue<string>()
            && owner["prId"]?.GetValue<string>() != newPr
            && owner["sessionId"]?.GetValue<string>() is { Length: > 0 } previous
            && (
                IsResetRetirement(proof, attempt, newSession)
                || (
                    proof["classification"]?.GetValue<string>() == "preparation-settled-native-idle"
                    && previous == newSession
                    && IsNeverAdmitted(owner)
                    && proof["noAdmission"]?.GetValue<bool>() == true
                    && proof["nativeIdleReceiptPath"]?.GetValue<string>() is { Length: > 0 }
                    && proof["claimToken"]?.GetValue<string>() == owner["claimToken"]?.GetValue<string>()
                )
                || (
                    previous != newSession
                    && proof["classification"]?.GetValue<string>()
                        is "failed-before-review-admission"
                            or "preparation-settled-before-review"
                    && proof["noAdmission"]?.GetValue<bool>() == true
                )
            )
            && proof["threadQuiescent"]?.GetValue<bool>() == true
            && proof["nativeQuiescent"]?.GetValue<bool>() == true
            && proof["evidence"] is JsonArray { Count: > 0 };
    }

    internal static bool IsResetRetirement(JsonNode proof, string attempt, string session) =>
        proof["classification"]?.GetValue<string>() == "operator-retired-after-reset"
        && proof["recoverySessionId"]?.GetValue<string>() is { Length: > 0 } recovery
        && recovery == session
        && JsonNode.Parse(attempt)!["stage"]?.GetValue<string>() != "completed-verified"
        && proof["writersRetired"]?.GetValue<bool>() == true
        && proof["producersStopped"]?.GetValue<bool>() == true
        && proof["resetReceiptPath"]?.GetValue<string>() is { Length: > 0 }
        && proof["archiveManifestPath"]?.GetValue<string>() is { Length: > 0 };

    private static bool IsNeverAdmitted(JsonNode attempt) =>
        attempt["stage"]?.GetValue<string>() == "prepare-pending"
        && attempt["claimToken"]?.GetValue<string>() is { Length: > 0 }
        && new[]
        {
            "threadId",
            "reviewInputId",
            "confirmationInputId",
            "reviewIdempotencyKey",
            "confirmationIdempotencyKey",
            "initialSend",
            "confirmationSend",
        }.All(field => attempt[field] is null);

    private static async Task ValidateNativeIdleAsync(JsonNode proof, JsonNode owner, CancellationToken ct)
    {
        var path = proof["nativeIdleReceiptPath"]!.GetValue<string>();
        var evidence = proof["evidence"]!.AsArray().SingleOrDefault(item => item?["path"]?.GetValue<string>() == path);
        var bytes = await File.ReadAllBytesAsync(path, ct);
        if (evidence is null || Convert.ToHexString(SHA256.HashData(bytes)) != evidence["sha256"]?.GetValue<string>())
            throw new InvalidOperationException("Native idle receipt must be hash-bound evidence.");
        var receipt = JsonNode.Parse(bytes)!;
        if (
            !IsNeverAdmitted(owner)
            || receipt["schemaVersion"]?.GetValue<int>() != 1
            || receipt["attemptDigest"]?.GetValue<string>() != proof["attemptDigest"]!.GetValue<string>()
            || new[] { "workspaceId", "sessionId", "repository", "prId", "claimToken" }.Any(field =>
                receipt[field]?.GetValue<string>() != owner[field]?.GetValue<string>()
            )
            || receipt["slot"]?.GetValue<int>() != owner["slot"]?.GetValue<int>()
            || receipt["nativeOwnerState"]?.GetValue<string>() is not ("released" or "absent")
            || receipt["preparedRunAbsent"]?.GetValue<bool>() != true
            || receipt["poolRunAbsent"]?.GetValue<bool>() != true
            || receipt["writersQuiescent"]?.GetValue<bool>() != true
            || receipt["producersStopped"]?.GetValue<bool>() != true
            || receipt["cleanBaselineVerified"]?.GetValue<bool>() != true
            || receipt["baselineSha"]?.GetValue<string>() is not { Length: 40 } baseline
            || !baseline.All(Uri.IsHexDigit)
            || receipt["sourceHeadSha"]?.GetValue<string>() != baseline
            || receipt["checkedAt"]?.GetValue<DateTimeOffset>() is not { } checkedAt
            || checkedAt > DateTimeOffset.UtcNow
            || DateTimeOffset.UtcNow - checkedAt > TimeSpan.FromMinutes(5)
        )
            throw new InvalidOperationException("Native idle evidence does not match this never-admitted attempt.");
        foreach (var field in new[] { "prepareOperationId", "cleanupOperationId" })
        {
            var id = owner[field]?.GetValue<string>();
            if (field == "prepareOperationId" && string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException("Preparation operation is unidentified.");
            if (id is null)
                continue;
            var operation = receipt["operations"]
                ?.AsArray()
                .SingleOrDefault(item => item?["operationId"]?.GetValue<string>() == id);
            if (
                operation?["status"]?.GetValue<string>() is not ("succeeded" or "failed")
                || operation["exitCode"] is null
            )
                throw new InvalidOperationException(
                    "Preparation and cleanup operations must be settled before release."
                );
        }
    }

    private static async Task ValidateResetAsync(JsonNode proof, JsonNode owner, CancellationToken ct)
    {
        async Task<JsonNode> ReadReceiptAsync(string field)
        {
            var path = proof[field]!.GetValue<string>();
            var evidence = proof["evidence"]!
                .AsArray()
                .SingleOrDefault(item => item?["path"]?.GetValue<string>() == path);
            var bytes = await File.ReadAllBytesAsync(path, ct);
            if (
                evidence is null
                || Convert.ToHexString(SHA256.HashData(bytes)) != evidence["sha256"]?.GetValue<string>()
            )
                throw new InvalidOperationException("Reset and archive receipts must be hash-bound evidence.");
            return JsonNode.Parse(bytes)!;
        }
        var reset = await ReadReceiptAsync("resetReceiptPath");
        var archive = await ReadReceiptAsync("archiveManifestPath");
        if (
            reset["attemptDigest"]?.GetValue<string>() != proof["attemptDigest"]!.GetValue<string>()
            || archive["attemptDigest"]?.GetValue<string>() != proof["attemptDigest"]!.GetValue<string>()
            || reset["workspaceId"]?.GetValue<string>() != owner["workspaceId"]!.GetValue<string>()
            || reset["sessionId"]?.GetValue<string>() != proof["recoverySessionId"]!.GetValue<string>()
            || reset["repository"]?.GetValue<string>() != owner["repository"]!.GetValue<string>()
            || reset["slot"]?.GetValue<int>() != owner["slot"]!.GetValue<int>()
            || reset["operationId"]?.GetValue<string>() is not { Length: > 0 }
            || reset["status"]?.GetValue<string>() != "succeeded"
            || reset["exitCode"]?.GetValue<int>() != 0
            || reset["dryRun"]?.GetValue<bool>() != false
            || reset["cleanBaselineVerified"]?.GetValue<bool>() != true
            || archive["files"] is not JsonArray { Count: > 0 }
            || archive["verified"]?.GetValue<bool>() != true
        )
            throw new InvalidOperationException("Reset/archive evidence does not match the retired attempt.");
        var archivedAt = archive["finishedAt"]!.GetValue<DateTimeOffset>();
        var retiredAt = reset["writersRetiredAt"]!.GetValue<DateTimeOffset>();
        var startedAt = reset["startedAt"]!.GetValue<DateTimeOffset>();
        var finishedAt = reset["finishedAt"]!.GetValue<DateTimeOffset>();
        if (
            archivedAt > startedAt
            || retiredAt > startedAt
            || startedAt > finishedAt
            || finishedAt > DateTimeOffset.UtcNow
        )
            throw new InvalidOperationException("Recovery evidence ordering is invalid.");
    }

    internal static async Task<int> RunAsync(CliOptions options, CancellationToken ct)
    {
        var database = options.Required("db");
        var repo = PrCommands.ParseRepo(options.Required("repo"));
        var pr = options.Required("pr");
        var proof =
            JsonNode.Parse(await File.ReadAllTextAsync(options.Required("evidence"), ct))
            ?? throw new InvalidOperationException("Reconciliation evidence is missing.");
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = options.Has("execute") ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
            }.ToString()
        );
        await connection.OpenAsync(ct);
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT value FROM cache WHERE key=@key";
        query.Parameters.AddWithValue("@key", $"reviewd/review-attempt/{repo.NormalizedKey}/{pr}");
        var attempt =
            await query.ExecuteScalarAsync(ct) as string
            ?? throw new InvalidOperationException("No retained attempt exists.");
        var owner = JsonNode.Parse(attempt)!;
        var classification = proof["classification"]?.GetValue<string>();
        var retired = classification == "operator-retired-after-reset";
        var nativeIdle = classification == "preparation-settled-native-idle";
        var recoverySession =
            retired ? proof["recoverySessionId"]?.GetValue<string>() ?? ""
            : nativeIdle ? owner["sessionId"]?.GetValue<string>() ?? ""
            : "different-session";
        if (
            !Allows(proof, attempt, "different-pr", recoverySession)
            || proof["operatorReason"]?.GetValue<string>() is not { Length: > 0 }
            || (!retired && (owner["reviewInputId"] is not null || owner["confirmationInputId"] is not null))
        )
            throw new InvalidOperationException("Evidence does not certify this attempt for release.");
        using var prLease = options.Has("execute")
            ? ReviewRunner.AcquireLease(database, "pr/" + repo.NormalizedKey + "/" + pr)
            : null;
        using var slotLease = options.Has("execute")
            ? ReviewRunner.AcquireLease(
                database,
                "slot/"
                    + owner["workspaceId"]!.GetValue<string>()
                    + "/"
                    + repo.NormalizedKey
                    + "/"
                    + owner["slot"]!.GetValue<int>()
            )
            : null;
        if (classification == "failed-before-review-admission")
        {
            if (
                owner["stage"]?.GetValue<string>() != "review-send-pending"
                || owner["threadId"] is null
                || owner["reviewIdempotencyKey"] is null
                || proof["failedBeforeAdmission"]?.GetValue<bool>() != true
            )
                throw new InvalidOperationException("Send failure evidence does not match the retained attempt.");
        }
        // "selected" is the stage the claim commits with: nothing has been dispatched yet, so it
        // settles exactly like an interrupted prepare.
        else if (
            !retired
            && (
                owner["stage"]?.GetValue<string>() is not ("prepare-pending" or "selected")
                || owner["threadId"] is not null
                || owner["reviewIdempotencyKey"] is not null
                || proof["preparationSettled"]?.GetValue<bool>() != true
            )
        )
            throw new InvalidOperationException("Preparation evidence does not match the retained attempt.");
        foreach (var item in proof["evidence"]!.AsArray())
        {
            var path = item!["path"]!.GetValue<string>();
            if (
                Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path, ct)))
                != item["sha256"]!.GetValue<string>()
            )
                throw new InvalidOperationException("Evidence content changed.");
        }
        if (retired)
            await ValidateResetAsync(proof, owner, ct);
        if (nativeIdle)
            await ValidateNativeIdleAsync(proof, owner, ct);
        if (!options.Has("execute"))
        {
            Console.WriteLine(proof.ToJsonString());
            return 0;
        }
        using var transaction = connection.BeginTransaction();
        query.Transaction = transaction;
        if ((await query.ExecuteScalarAsync(ct) as string) != attempt)
            throw new InvalidOperationException("Attempt changed during reconciliation.");
        var ownerReleased = await ReleaseCertifiedOwnerAsync(
            connection,
            transaction,
            OwnerKey(owner["workspaceId"]!.GetValue<string>(), repo.NormalizedKey, owner["slot"]!.GetValue<int>()),
            owner["claimToken"]?.GetValue<string>(),
            ct
        );
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO cache(key,value) VALUES(@key,@value)";
        insert.Parameters.AddWithValue("@key", Key(attempt));
        insert.Parameters.AddWithValue("@value", proof.ToJsonString());
        await insert.ExecuteNonQueryAsync(ct);
        transaction.Commit();
        Console.WriteLine(
            ownerReleased
                ? "Slot release recorded and the certified owner claim released; original attempt retained. No replay or reset performed."
                : "Slot release recorded; no owner claim was held. Original attempt retained. No replay or reset performed."
        );
        return 0;
    }

    /// <summary>
    ///     Releases the slot owner row only when it is still held by the certified attempt's exact claim.
    ///     Unlike <see cref="ReleaseAsync"/> it leaves the attempt untouched, so the retained attempt keeps
    ///     its interrupted state for inspection. A row held under any other claim belongs to a newer owner
    ///     and refuses the whole reconciliation.
    /// </summary>
    /// <returns>True when a held claim was released; false when no claim was held.</returns>
    private static async Task<bool> ReleaseCertifiedOwnerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string ownerKey,
        string? attemptToken,
        CancellationToken ct
    )
    {
        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT value FROM cache WHERE key=@key";
        read.Parameters.AddWithValue("@key", ownerKey);
        var raw = await read.ExecuteScalarAsync(ct) as string;
        var claim = raw is null ? null : JsonNode.Parse(raw)?.AsObject();
        if (claim is null || claim["state"]?.GetValue<string>() == "released")
            return false;
        if (attemptToken is not { Length: > 0 } || claim["claimToken"]?.GetValue<string>() != attemptToken)
            throw new InvalidOperationException(
                "Slot is owned by a different claim than the certified attempt; reconciliation refused."
            );
        claim["state"] = "released";
        using var write = connection.CreateCommand();
        write.Transaction = transaction;
        write.CommandText = "UPDATE cache SET value=@value WHERE key=@key AND value=@old";
        write.Parameters.AddWithValue("@key", ownerKey);
        write.Parameters.AddWithValue("@old", raw);
        write.Parameters.AddWithValue("@value", claim.ToJsonString());
        if (await write.ExecuteNonQueryAsync(ct) != 1)
            throw new InvalidOperationException("Slot claim changed during reconciliation.");
        return true;
    }
}
