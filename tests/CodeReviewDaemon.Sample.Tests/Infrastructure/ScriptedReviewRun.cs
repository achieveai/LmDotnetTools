extern alias Reviewd;

using System.Net;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.Misc.Storage;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Persistence.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Reviewd::Reviewd.Sample;

namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>
///     One <c>review run --execute</c> of org/project/Nova #17 on slot 2, against a scripted host on which the review,
///     confirmation and verify all succeed. Each sandbox phase is answered by <see cref="OnPhase"/> when it returns a
///     result, otherwise with that phase's success. The database is a temp file the test can script with triggers.
/// </summary>
internal sealed class ScriptedReviewRun : IAsyncDisposable
{
    internal static readonly RepoIdentity Repo = PrCommands.ParseRepo("org/project/Nova");

    internal string Database { get; } = Path.Combine(Path.GetTempPath(), $"reviewd-scripted-run-{Guid.NewGuid():N}.db");

    internal string AttemptKey { get; } = $"reviewd/review-attempt/{Repo.NormalizedKey}/17";
    internal string RunKey { get; } = $"reviewd/review-run/{Repo.NormalizedKey}/17";
    internal string OwnerKey { get; } = SlotRelease.OwnerKey("workspace", Repo.NormalizedKey, 2);
    internal SqliteConnection Connection { get; }
    internal SqliteKvStore Store { get; }

    /// <summary>The run's own token: cancel it to cancel the run.</summary>
    internal CancellationTokenSource Cancellation { get; } = new();

    internal List<string> Phases { get; } = [];
    internal Func<string, JsonObject, string, CancellationToken, Task<JsonObject?>>? OnPhase { get; set; }

    private ScriptedReviewRun()
    {
        Connection = PrThreadBindings.Connection(Database);
        Store = new SqliteKvStore(Connection);
    }

    internal static async Task<ScriptedReviewRun> CreateAsync()
    {
        var run = new ScriptedReviewRun();
        await run.Connection.OpenAsync();
        await run.Store.SetAsync(
            $"reviewd/pr-snapshot/{Repo.NormalizedKey}",
            new JsonObject
            {
                ["17"] = new JsonObject
                {
                    ["Lifecycle"] = "Open",
                    ["IsDraft"] = false,
                    ["HeadSha"] = new string('a', 40),
                },
            }
        );
        return run;
    }

    internal async Task<JsonObject> ReadAsync(string key) => (await Store.GetAsync<JsonObject>(key))!;

    internal async Task ExecuteAsync(string sql)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync();
    }

    /// <summary>What a sweep's reset does under the run: the attempt becomes failed-released and the owner released.</summary>
    internal async Task ResetAsync(JsonObject args, CancellationToken token)
    {
        var attempt = await ReadAsync(AttemptKey);
        attempt["stage"] = "failed-released";
        await SlotRelease.ReleaseAsync(
            Connection,
            OwnerKey,
            args["claimToken"]!.GetValue<string>(),
            AttemptKey,
            attempt,
            token
        );
    }

    /// <summary>
    ///     A reset that lands inside the next owner-row renewal: right after it, the owner is released and the attempt
    ///     recorded as reset, as if a sweep had released the claim between the renewal and the save that follows it.
    /// </summary>
    internal Task ResetAfterNextRenewalAsync() =>
        ExecuteAsync(
            $"""
            CREATE TRIGGER reset_after_renewal AFTER UPDATE ON cache
            WHEN NEW.key = '{OwnerKey}' AND json_extract(NEW.value, '$.state') = 'owned'
            BEGIN
                UPDATE cache SET value = json_set(value, '$.state', 'released') WHERE key = '{OwnerKey}';
                UPDATE cache SET value = json_set(value, '$.stage', 'failed-released', '$.cleanupState', 'released')
                WHERE key = '{AttemptKey}';
            END;
            """
        );

    /// <summary>
    ///     Fails the next plain write of the attempt row at <paramref name="stage"/> once, as a transient database error
    ///     would. RAISE(FAIL) keeps the trigger's own delete, so only one write fails; a write inside a transaction that
    ///     rolls back would fail again.
    /// </summary>
    internal Task FailNextAttemptWriteAsync(string stage) =>
        ExecuteAsync(
            $"""
            CREATE TABLE fail_once(n INTEGER);
            INSERT INTO fail_once VALUES (1);
            CREATE TRIGGER fail_attempt_insert BEFORE INSERT ON cache
            WHEN NEW.key = '{AttemptKey}' AND json_extract(NEW.value, '$.stage') = '{stage}'
                AND EXISTS (SELECT 1 FROM fail_once)
            BEGIN DELETE FROM fail_once; SELECT RAISE(FAIL, 'transient attempt write failure'); END;
            CREATE TRIGGER fail_attempt_update BEFORE UPDATE ON cache
            WHEN NEW.key = '{AttemptKey}' AND json_extract(NEW.value, '$.stage') = '{stage}'
                AND EXISTS (SELECT 1 FROM fail_once)
            BEGIN DELETE FROM fail_once; SELECT RAISE(FAIL, 'transient attempt write failure'); END;
            """
        );

    internal async Task<int> RunAsync()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "/capabilities",
                """{"messageIdempotency":true,"spawnSuppression":true,"rootReasoningEffort":true,"conversationWorkingDirectory":true}"""
            )
            .On(
                request =>
                    request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/conversations",
                _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"threadId":"thread"}"""),
                }
            )
            .OnJson(HttpMethod.Get, "/api/conversations?", "[]")
            .OnJson(HttpMethod.Put, "/metadata", "{}")
            .OnJson(HttpMethod.Post, "/messages", """{"inputId":"input","idempotencyKeyHonored":true}""")
            .OnJson(HttpMethod.Get, "/status?", """{"status":"Completed","runId":"run","response":{"text":"done"}}""")
            .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""")
            .OnJson(HttpMethod.Get, "/subagents?", """{"schemaVersion":1,"nodes":[]}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        using var config = new ReviewdConfig(new ConfigurationBuilder().Build());
        var options = CliOptions.Parse([
            "review",
            "run",
            "--repo",
            Repo.DisplayName,
            "--pr",
            "17",
            "--slot",
            "2",
            "--db",
            Database,
            "--session",
            "session",
            "--workspace",
            "workspace",
            "--provider",
            "test",
            "--execute",
        ]);
        return await ReviewRunner.RunAsync(
            options,
            config,
            new LmStreamingS2SClient(http, null, null, null),
            SandboxAsync,
            Cancellation.Token,
            _ => Task.CompletedTask
        );
    }

    private async Task<JsonObject> SandboxAsync(
        string phase,
        JsonObject args,
        string operation,
        CancellationToken token
    )
    {
        Phases.Add(phase);
        if (OnPhase is not null && await OnPhase(phase, args, operation, token) is { } scripted)
        {
            return scripted;
        }
        return phase switch
        {
            "prepare" => new JsonObject
            {
                ["mergeSha"] = new string('b', 40),
                ["contextPath"] = "context",
                ["reviewDirectory"] = "reviews",
                ["baselineComments"] = new JsonArray(),
            },
            "commit" => Committed(),
            "cleanup" => new JsonObject
            {
                ["claimToken"] = args["claimToken"]!.DeepClone(),
                ["cleanBaselineVerified"] = true,
                ["evidencePreserved"] = true,
            },
            _ => new JsonObject { ["ok"] = true },
        };
    }

    internal static JsonObject Committed() => new() { ["status"] = "committed", ["commit"] = new string('c', 40) };

    public async ValueTask DisposeAsync()
    {
        Cancellation.Dispose();
        await Connection.DisposeAsync();
        ReviewdSqlitePools.Clear(Database);
        File.Delete(Database);
    }
}
