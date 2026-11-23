using System.Globalization;
using System.Text.Json.Nodes;
using CodeReviewDaemon.Sample.Persistence.Models;
using Microsoft.Data.Sqlite;

namespace Reviewd.Sample;

/// <summary>
/// <c>review finish-commit</c>: finishes a held review whose artifact commit landed but whose run stopped before it
/// wrote its receipt (stage <c>commit-completed</c>, slot still owned by that run's claim). It sends the attempt back to
/// <c>verify-pending</c> under that claim, so the <c>review run --resume</c> it prints re-verifies the publication,
/// commits again and writes the receipt itself. The batch does not: it keeps the PR's entry held until that receipt
/// exists. It writes no success record and runs nothing remote.
/// </summary>
internal static class FinishCommitCommand
{
    public static async Task<int> RunAsync(CliOptions options, CancellationToken ct)
    {
        var repo = PrCommands.ParseRepo(options.Required("repo"));
        var pr = options.Required("pr");
        var slot = int.Parse(options.Required("slot"), CultureInfo.InvariantCulture);
        var database = options.Required("db");
        var workspace = options.Required("workspace");
        var execute = options.Has("execute");
        // A run still alive on this slot holds the slot lease; the stage is never changed under it.
        FileStream? lease = null;
        if (execute)
        {
            try
            {
                lease = ReviewRunner.AcquireLease(database, ReviewRunner.SlotLeaseIdentity(workspace, repo, slot));
            }
            catch (IOException)
            {
                throw new InvalidOperationException(
                    $"A review run still holds slot {slot}; stop it before finishing its commit."
                );
            }
        }
        using (lease)
        {
            await using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = database,
                    Mode = execute ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
                }.ToString()
            );
            await connection.OpenAsync(ct);
            var result = await FinishAsync(connection, repo, pr, slot, workspace, execute, ct);
            Console.WriteLine(result.ToJsonString());
            return 0;
        }
    }

    /// <summary>
    /// Checks the attempt and its slot owner, and with <paramref name="execute"/> sets the attempt to verify-pending,
    /// in one transaction. Throws with the operator's next step when the review cannot be finished this way.
    /// </summary>
    internal static async Task<JsonObject> FinishAsync(
        SqliteConnection connection,
        RepoIdentity repo,
        string pr,
        int slot,
        string workspace,
        bool execute,
        CancellationToken ct
    )
    {
        var attemptKey = $"reviewd/review-attempt/{repo.NormalizedKey}/{pr}";
        var ownerKey = SlotRelease.OwnerKey(workspace, repo.NormalizedKey, slot);
        using var transaction = connection.BeginTransaction();
        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT value FROM cache WHERE key=@key";
        read.Parameters.AddWithValue("@key", attemptKey);
        var raw = await read.ExecuteScalarAsync(ct) as string;
        var attempt =
            (raw is null ? null : JsonNode.Parse(raw)?.AsObject())
            ?? throw new InvalidOperationException(
                $"No review attempt for {repo.DisplayName} #{pr}; nothing to finish."
            );
        if (attempt["slot"]?.GetValue<int>() != slot || attempt["workspaceId"]?.GetValue<string>() != workspace)
        {
            throw new InvalidOperationException("The attempt's slot or workspace does not match the request.");
        }

        if (attempt["cleanupState"]?.GetValue<string>() == "released")
        {
            throw new InvalidOperationException(
                "The attempt's slot was already released (discarded or cleaned), so its prepared review is gone and "
                    + "the commit cannot be finished. A new push gets a fresh review."
            );
        }

        var stage = attempt["stage"]?.GetValue<string>();
        if (stage != "commit-completed")
        {
            throw new InvalidOperationException(
                $"Only a commit-completed attempt can be finished; this one is at {stage ?? "no stage"}."
            );
        }

        if (!ReviewRunner.CommitLanded(attempt))
        {
            throw new InvalidOperationException("The attempt has no landed artifact commit; nothing to finish.");
        }

        read.Parameters["@key"].Value = ownerKey;
        var owner = await read.ExecuteScalarAsync(ct) is string ownerRaw ? JsonNode.Parse(ownerRaw) : null;
        if (
            owner?["state"]?.GetValue<string>() != "owned"
            || owner["claimToken"]?.GetValue<string>() is not { Length: > 0 } ownerToken
            || ownerToken != attempt["claimToken"]?.GetValue<string>()
        )
        {
            throw new InvalidOperationException(
                $"Slot {slot} is not owned by this attempt's claim; refusing to change another owner's slot."
            );
        }

        if (owner[SlotRelease.ResetFence] is not null)
        {
            throw new InvalidOperationException(
                $"A reset fenced slot {slot}, so it may be partly discarded and the review cannot be finished there. "
                    + "Once that reset has stopped, continue it: "
                    + $"`reviewd review reclaim-slot --repo {repo.DisplayName} --pr {pr} --slot {slot} --db <db> "
                    + $"--workspace {workspace} --session <session> --discard-verified --execute`."
            );
        }

        // The resume must repeat the attempt's own identity: its session, provider, reasoning effort and reviewed head.
        var effort = attempt["reasoningEffort"]?.GetValue<string>() is { } retained
            ? $" --reasoning-effort {retained}"
            : "";
        var resume =
            $"reviewd review run --repo {repo.DisplayName} --pr {pr} --slot {slot} --db \"{connection.DataSource}\" "
            + $"--session {Retained("sessionId", "<session>")} --workspace {workspace} "
            + $"--provider {Retained("providerId", "<provider>")}{effort} "
            + $"--head {Retained("headSha", "<reviewed head>")} "
            + "--resume --execute";

        string Retained(string field, string missing) => attempt[field]?.GetValue<string>() ?? missing;

        var result = new JsonObject
        {
            ["repo"] = repo.DisplayName,
            ["pr"] = pr,
            ["slot"] = slot,
            ["attemptKey"] = attemptKey,
            ["from"] = stage,
            ["to"] = "verify-pending",
            ["status"] = execute ? "finish-ready" : "preview",
            ["next"] = resume,
        };
        if (!execute)
        {
            return result;
        }

        attempt["stage"] = "verify-pending";
        using var write = connection.CreateCommand();
        write.Transaction = transaction;
        write.CommandText = "UPDATE cache SET value=@value WHERE key=@key AND value=@old";
        write.Parameters.AddWithValue("@key", attemptKey);
        write.Parameters.AddWithValue("@old", raw);
        write.Parameters.AddWithValue("@value", attempt.ToJsonString());
        if (await write.ExecuteNonQueryAsync(ct) != 1)
        {
            throw new InvalidOperationException("The attempt changed while it was checked; run the command again.");
        }
        transaction.Commit();
        return result;
    }
}
