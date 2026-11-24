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
                    + $"Once that reset has stopped, continue it: `{DiscardCommand(attempt, repo, pr, slot, connection.DataSource, workspace)}`."
            );
        }

        var (resume, fill) = ResumeCommand(attempt, repo, pr, slot, connection.DataSource, workspace);
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
            ["shell"] = "PowerShell",
        };
        if (fill.Count > 0)
        {
            result["fill"] = new JsonArray([.. fill.Select(step => JsonValue.Create(step))]);
        }
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

    /// <summary>
    /// The resume that finishes this attempt, and what the operator must fill in first (each placeholder it leaves in
    /// angle brackets, which PowerShell refuses to run). It repeats the attempt's own identity: its session, provider,
    /// mode, reasoning effort and reviewed head, and a shadow arm's shadow options, since a shadow database refuses a
    /// live run. Every value is quoted for PowerShell: Reviewd runs on Windows, from scheduled tasks.
    /// </summary>
    internal static (string Command, List<string> Fill) ResumeCommand(
        JsonObject attempt,
        RepoIdentity repo,
        string pr,
        int slot,
        string database,
        string workspace
    )
    {
        var fill = new List<string>();
        var provenance = attempt["provenance"] as JsonObject;
        var words = new List<string>
        {
            "reviewd review run --repo",
            Quote(SavedRepository(attempt, repo)),
            "--pr",
            Quote(pr),
            "--slot",
            slot.ToString(CultureInfo.InvariantCulture),
            "--db",
            Quote(database),
            "--session",
            Retained(attempt["sessionId"], "<session>"),
            "--workspace",
            Quote(workspace),
            "--provider",
            Retained(attempt["providerId"], "<provider>"),
            "--mode",
            // A shadow attempt saved its mode in its provenance before attempts saved it.
            Retained(attempt["mode"] ?? provenance?["mode"], "<mode>"),
        };
        if (words[^1] == "<mode>")
        {
            fill.Add(
                "<mode>: this attempt predates saved review modes. Use its run's --mode, else the configured "
                    + "CodeReviewDaemon:LmStreamingModeId (default workspace-agent); a resume in another mode is "
                    + "refused before any prompt is sent."
            );
        }
        if (attempt["reasoningEffort"] is { } effort)
        {
            words.AddRange(["--reasoning-effort", Retained(effort, "<effort>")]);
        }
        if (attempt["shadow"]?.GetValue<bool>() == true)
        {
            words.AddRange(["--shadow", "--post-session", Retained(attempt["postSessionId"], "<post session>")]);
            if (attempt["variant"] is { } variant)
            {
                words.AddRange(["--variant", Retained(variant, "<variant>")]);
            }
            if (provenance is null)
            {
                fill.Add(
                    "This shadow attempt saved no provenance: add its run's --manifest, --experiment and "
                        + "--task-prompt-file, if it had them."
                );
            }
            if (provenance?["manifestSha256"] is { } manifest)
            {
                words.AddRange([
                    "--manifest",
                    "<manifest file>",
                    "--experiment",
                    Retained(provenance["experimentId"], "<experiment>"),
                ]);
                fill.Add($"<manifest file>: the variant manifest this arm ran (SHA-256 {manifest}).");
            }
            if (
                provenance?["taskPromptSha256"]?.GetValue<string>() is { } prompt
                && prompt != VariantManifest.Sha256Hex(ShadowTaskPrompt.Default)
            )
            {
                words.AddRange(["--task-prompt-file", "<task prompt file>"]);
                fill.Add($"<task prompt file>: the task prompt this arm sent (SHA-256 {prompt}).");
            }
        }
        words.AddRange(["--head", Retained(attempt["headSha"], "<reviewed head>"), "--resume --execute"]);
        return (string.Join(' ', words), fill);

        static string Retained(JsonNode? value, string missing) =>
            value?.GetValue<string>() is { } retained ? Quote(retained) : missing;
    }

    /// <summary>
    /// The discard that continues a stopped reset of this slot (<c>reclaim-slot --discard-verified</c>), in the
    /// attempt's own repository spelling and session (a placeholder only when it saved none).
    /// </summary>
    internal static string DiscardCommand(
        JsonObject attempt,
        RepoIdentity repo,
        string pr,
        int slot,
        string database,
        string workspace
    ) =>
        $"reviewd review reclaim-slot --repo {Quote(SavedRepository(attempt, repo))} --pr {Quote(pr)} --slot {slot} "
        + $"--db {Quote(database)} --workspace {Quote(workspace)} --session "
        + (attempt["sessionId"]?.GetValue<string>() is { } session ? Quote(session) : "<session>")
        + " --discard-verified --execute";

    /// <summary>
    /// The repository as the attempt's run spelled it. Finish and reclaim match a repository case-insensitively, but
    /// a resume checks the attempt's exact spelling, so a printed command repeats the saved one.
    /// </summary>
    private static string SavedRepository(JsonObject attempt, RepoIdentity repo) =>
        attempt["repository"]?.GetValue<string>() ?? repo.DisplayName;

    /// <summary>
    /// A PowerShell single-quoted literal, so a value with spaces (an Azure DevOps project, a path) stays one argument
    /// and nothing in it is expanded. PowerShell also closes a single-quoted string at a typographic single quote, so
    /// those are doubled too.
    /// </summary>
    internal static string Quote(string value) =>
        "'"
        + string.Concat(
            value.Select(c => c is '\'' or '\u2018' or '\u2019' or '\u201A' or '\u201B' ? $"{c}{c}" : c.ToString())
        )
        + "'";
}
