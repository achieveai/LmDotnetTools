extern alias Reviewd;

using System.Net;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.Misc.Storage;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Scenarios;
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

    /// <summary>
    ///     The <see cref="Phases"/> entry for a sandbox release the run sent to the host, in order with the phases: the
    ///     thread named in its route and the directory in its body ("none" when it asks for no move).
    /// </summary>
    internal static string SandboxRelease(string thread, string? directory) =>
        $"sandbox-release {thread} {directory ?? "none"}";

    /// <summary>The host lists the run's conversation, bound to its workspace, provider and mode, as it does for a resume.</summary>
    internal bool HostListsThread { get; set; }
    internal Func<string, JsonObject, string, CancellationToken, Task<JsonObject?>>? OnPhase { get; set; }

    // SQL run once, synchronously, when the run logs a matching line to stderr (see ExecuteWhenLogged).
    private readonly List<(Func<string, bool> Match, string Sql)> _onLogged = [];

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
    ///     Fails every write of the attempt row at <paramref name="stage"/>, as a transient database error would, until
    ///     <see cref="EndAttemptWriteFailure"/> runs. The failure must outlast the write's own transaction: a trigger that
    ///     counted its failures in the database would have the count rolled back with it.
    /// </summary>
    internal Task FailAttemptWritesAsync(string stage) =>
        ExecuteAsync(
            $"""
            CREATE TRIGGER fail_attempt_insert BEFORE INSERT ON cache
            WHEN NEW.key = '{AttemptKey}' AND json_extract(NEW.value, '$.stage') = '{stage}'
            BEGIN SELECT RAISE(ABORT, 'transient attempt write failure'); END;
            CREATE TRIGGER fail_attempt_update BEFORE UPDATE ON cache
            WHEN NEW.key = '{AttemptKey}' AND json_extract(NEW.value, '$.stage') = '{stage}'
            BEGIN SELECT RAISE(ABORT, 'transient attempt write failure'); END;
            """
        );

    /// <summary>Ends <see cref="FailAttemptWritesAsync"/>: the next write of the attempt row succeeds.</summary>
    internal const string EndAttemptWriteFailure =
        "DROP TRIGGER fail_attempt_insert; DROP TRIGGER fail_attempt_update;";

    /// <summary>A sweep's reset, as SQL: the owner released and unfenced, the attempt recorded as a reset admission.</summary>
    internal string ResetSql =>
        $"""
            UPDATE cache SET value = json_remove(json_set(value, '$.state', 'released'), '$.{SlotRelease.ResetFence}')
            WHERE key = '{OwnerKey}';
            UPDATE cache SET value = json_set(value, '$.stage', 'failed-released', '$.cleanupState', 'released',
                '$.resetCount', 1)
            WHERE key = '{AttemptKey}';
            """;

    /// <summary>
    ///     Runs <paramref name="sql"/> once, on the test's own connection, when the run writes a stderr line that
    ///     <paramref name="match"/> accepts: the only point between two of the run's own writes that a test can reach,
    ///     such as a failed save and the failure path's retained save that follows its log line. The run holds no
    ///     transaction while it logs, so the SQL commits before the run's next write. Tests that use it share
    ///     the console-capturing test collection, since stderr is process-wide.
    /// </summary>
    internal void ExecuteWhenLogged(Func<string, bool> match, string sql) => _onLogged.Add((match, sql));

    /// <summary>
    ///     One run. <paramref name="resume"/> passes <c>--resume</c>, as a new process resuming the attempt would;
    ///     <paramref name="cancellation"/> replaces <see cref="Cancellation"/>, as that new process's own token.
    /// </summary>
    internal async Task<int> RunAsync(bool resume = false, CancellationToken? cancellation = null)
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "/capabilities",
                """{"messageIdempotency":true,"spawnSuppression":true,"rootReasoningEffort":true,"conversationWorkingDirectory":true,"dedicatedSandbox":true}"""
            )
            .On(
                request =>
                    request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/conversations",
                _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"threadId":"thread","dedicatedSandboxAccepted":true}"""),
                }
            )
            .On(
                request =>
                    request.Method == HttpMethod.Get
                    && request.RequestUri!.PathAndQuery.StartsWith("/api/conversations?", StringComparison.Ordinal),
                _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        HostListsThread
                            ? """[{"threadId":"thread","title":"Nova-17","workspace":"workspace","mode":"workspace-agent","provider":"test","lastUpdated":1,"clientToolsDisabled":true}]"""
                            : "[]"
                    ),
                }
            )
            .OnAsync(
                ReviewdRunnerTests.IsSandboxRelease,
                async request =>
                {
                    var segments = request.RequestUri!.AbsolutePath.Split('/');
                    var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync());
                    var directory = body?["workingDirectoryRelPath"]?.GetValue<string>();
                    Phases.Add(SandboxRelease(Uri.UnescapeDataString(segments[^3]), directory));
                    return await ReviewdRunnerTests.EchoSandboxReleaseAsync(request);
                }
            )
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
            .. resume ? new[] { "--resume" } : [],
            "--execute",
        ]);
        var stderr = Console.Error;
        if (_onLogged.Count > 0)
        {
            Console.SetError(new LoggedHook(stderr, OnLogged));
        }
        try
        {
            return await ReviewRunner.RunAsync(
                options,
                config,
                new LmStreamingS2SClient(http, null, null, null),
                SandboxAsync,
                cancellation ?? Cancellation.Token,
                _ => Task.CompletedTask
            );
        }
        finally
        {
            Console.SetError(stderr);
        }
    }

    private void OnLogged(string line)
    {
        string? sql;
        lock (_onLogged)
        {
            var index = _onLogged.FindIndex(hook => hook.Match(line));
            if (index < 0)
            {
                return;
            }
            sql = _onLogged[index].Sql;
            _onLogged.RemoveAt(index);
        }
        // Synchronous on purpose: the run is inside Console.Error.WriteLine, and its next write must see this one.
        using var command = Connection.CreateCommand();
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }

    /// <summary>Passes every stderr write through, and reports each whole line written.</summary>
    private sealed class LoggedHook(TextWriter inner, Action<string> onLine) : TextWriter
    {
        public override System.Text.Encoding Encoding => inner.Encoding;

        public override void Write(char value) => inner.Write(value);

        public override void Write(string? value) => inner.Write(value);

        public override void WriteLine(string? value)
        {
            inner.WriteLine(value);
            onLine(value ?? "");
        }
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
