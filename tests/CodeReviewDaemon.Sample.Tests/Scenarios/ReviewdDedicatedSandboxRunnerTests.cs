extern alias Reviewd;

using System.Net;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.Misc.Storage;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Reviewd::Reviewd.Sample;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
///     The runner's side of dedicated sandboxes, on one PR whose earlier review left a conversation behind: the
///     continued thread moves to this review's PR folder before anything is sent, a move the host refuses stops the
///     review, a retained attempt from before dedicated sandboxes moves before its confirmation, and a verified review
///     never releases its sandbox afterwards. Each run records one ordered event list of host requests and sandbox phases.
///     The runner's stderr is read, so these tests swap the process-wide Console writers and run on their own.
/// </summary>
[Collection(ConsoleCapturingCollection.Name)]
public sealed class ReviewdDedicatedSandboxRunnerTests(ITestOutputHelper output) : IDisposable
{
    private const string Capabilities =
        """{"messageIdempotency":true,"spawnSuppression":true,"rootReasoningEffort":true,"conversationWorkingDirectory":true,"dedicatedSandbox":true}""";

    private const string ReviewDirectory = "PRs/nova-17";
    private const string Move = "POST /api/conversations/prior/sandbox/release " + ReviewDirectory;
    private const string FinalRelease = "POST /api/conversations/prior/sandbox/release null";
    private const string Send = "POST /api/conversations/prior/messages";

    /// <summary>Text a host could put in a refusal body; it must never reach an error message or the runner's log.</summary>
    private const string Sentinel = "UPSTREAM-BODY-SENTINEL-5d1c";

    private readonly string _database = Path.Combine(Path.GetTempPath(), $"reviewd-dedicated-{Guid.NewGuid():N}.db");
    private readonly RepoIdentity _repo = PrCommands.ParseRepo("org/project/Nova");

    /// <summary>What the run did, in order: host requests (<c>METHOD path</c>, a release with its directory) and sandbox phases.</summary>
    private readonly List<string> _events = [];

    private string _capabilities = Capabilities;
    private Func<HttpRequestMessage, string?, Task<HttpResponseMessage>> _release = (request, _) => Echo(request);
    private bool _failConfirmationInspect;

    private string AttemptKey => $"reviewd/review-attempt/{_repo.NormalizedKey}/17";
    private string ReceiptKey => $"reviewd/review-run/{_repo.NormalizedKey}/17";

    public void Dispose()
    {
        ReviewdSqlitePools.Clear(_database);
        File.Delete(_database);
        File.Delete(_database + ".review-lock");
    }

    [Fact]
    public async Task Runner_refuses_a_host_without_dedicated_sandboxes_before_it_claims_or_prepares()
    {
        // Such a host would run the review in the workspace's shared sandbox, which can write every slot, so the
        // review stops before it takes the slot, runs setup or creates a conversation.
        _capabilities =
            """{"messageIdempotency":true,"spawnSuppression":true,"rootReasoningEffort":true,"conversationWorkingDirectory":true}""";

        var run = await RunAsync();

        run.Error.Should()
            .BeOfType<ReviewHostContractException>()
            .Which.Message.Should()
            .Contain("dedicated sandboxes");
        _events.Should().NotContain(e => e.StartsWith("phase ", StringComparison.Ordinal), "nothing was prepared");
        _events.Should().OnlyContain(e => e.StartsWith("GET ", StringComparison.Ordinal), "nothing was written");
        (await StoreGetAsync(SlotRelease.OwnerKey("workspace", _repo.NormalizedKey, 2)))
            .Should()
            .BeNull("the slot was never claimed");
    }

    /// <summary>
    ///     A finished review leaves its sandbox paired with the conversation, to retire on the gateway: after verify,
    ///     commit and cleanup the run sends no final release, whose gateway DELETE would wipe the workspace's command
    ///     output for every other review on it (SBX#372). The move before the review still happens.
    /// </summary>
    [Fact]
    public async Task Runner_moves_a_continued_thread_before_its_review_and_never_releases_its_sandbox_after_it()
    {
        var run = await RunAsync();

        run.Exit.Should().Be(0);
        Steps()
            .Should()
            .Equal(
                [
                    "phase prepare",
                    Move,
                    "phase inspect",
                    Send,
                    "phase inspect",
                    Send,
                    "phase verify",
                    "phase commit",
                    "phase cleanup",
                ],
                "the move precedes the first send, and nothing releases the sandbox after the review"
            );
        _events.Should().NotContain(FinalRelease);
        (await StoreGetAsync(AttemptKey))!["sandboxDirectory"]!.GetValue<string>().Should().Be(ReviewDirectory);
        var attempt = (await StoreGetAsync(AttemptKey))!;
        attempt["stage"]!.GetValue<string>().Should().Be("completed-verified");
        attempt["cleanupState"]!.GetValue<string>().Should().Be("released", "the slot is still released");
        (await StoreGetAsync(SlotRelease.OwnerKey("workspace", _repo.NormalizedKey, 2)))!["state"]!
            .GetValue<string>()
            .Should()
            .Be("released");
    }

    /// <summary>A move the host does not complete stops the review before anything is sent, whatever the failure.</summary>
    [Theory]
    [InlineData("throws")]
    [InlineData("409")]
    [InlineData("503")]
    [InlineData("wrong-directory")]
    public async Task Runner_sends_nothing_when_the_move_of_a_continued_thread_fails(string failure)
    {
        _release = (request, directory) => directory is null ? Echo(request) : Refuse(failure);

        var run = await RunAsync();

        run.Error.Should().NotBeNull();
        run.Error!.Message.Should().NotContain(Sentinel);
        ExpectFailure(run.Error, failure, "PRs/nova-17");
        _events.Should().Contain(Move).And.NotContain(Send, "no prompt may reach a thread that was not moved");
        _events.Should().NotContain(FinalRelease);
        _events.Should().NotContain(e => e.StartsWith("phase verify", StringComparison.Ordinal));
        (await StoreGetAsync(AttemptKey))!["sandboxDirectory"].Should().BeNull("the move never happened");
    }

    /// <summary>
    ///     An attempt retained from before dedicated sandboxes has a sent review and no record of a move: its review ran
    ///     in the workspace's shared sandbox. Its resume moves the idle thread to its PR folder before the confirmation.
    ///     An attempt that recorded its move resumes in place. A move the host refuses stops the resume before the
    ///     confirmation and keeps the attempt for the next one.
    /// </summary>
    [Theory]
    [InlineData("pre-upgrade")]
    [InlineData("recorded")]
    [InlineData("pre-upgrade-503")]
    public async Task Runner_moves_a_retained_pre_upgrade_attempt_before_its_confirmation(string attempt)
    {
        _failConfirmationInspect = true;
        (await RunAsync())
            .Error.Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Be("gateway hiccup");
        var retained = (await StoreGetAsync(AttemptKey))!;
        retained["stage"]!.GetValue<string>().Should().Be("review-completed");
        if (attempt != "recorded")
        {
            retained.Remove("sandboxDirectory");
            await StoreSetAsync(AttemptKey, retained);
        }
        if (attempt == "pre-upgrade-503")
        {
            _release = (request, directory) => directory is null ? Echo(request) : Refuse("503");
        }
        _events.Clear();

        var run = await RunAsync("--resume");

        var host = Steps();
        if (attempt == "pre-upgrade-503")
        {
            ExpectFailure(run.Error, "503", ReviewDirectory);
            host.Should().Equal(["phase inspect-prepared", Move], "no confirmation reaches the shared sandbox");
            var kept = (await StoreGetAsync(AttemptKey))!;
            kept["stage"]!.GetValue<string>().Should().Be("review-completed", "the next resume tries the move again");
            kept["sandboxDirectory"].Should().BeNull();
            return;
        }
        run.Exit.Should().Be(0);
        var moved = attempt == "pre-upgrade";
        host.Should()
            .Equal([
                "phase inspect-prepared",
                .. moved ? new[] { Move } : [],
                "phase inspect",
                Send,
                "phase verify",
                "phase commit",
                "phase cleanup",
            ]);
        if (moved)
        {
            // The move waits for the thread to be idle, after the review turn and before the confirmation.
            var move = _events.IndexOf(Move);
            _events
                .FindLastIndex(move, e => e.EndsWith("/run-state", StringComparison.Ordinal))
                .Should()
                .BeGreaterThan(_events.IndexOf("phase inspect-prepared"));
        }
        (await StoreGetAsync(ReceiptKey))!["sandboxDirectory"]!.GetValue<string>().Should().Be(ReviewDirectory);
    }

    /// <summary>The events that matter for ordering: sandbox phases, prompts sent and sandbox releases.</summary>
    private List<string> Steps() =>
        [
            .. _events.Where(e =>
                e.StartsWith("phase ", StringComparison.Ordinal)
                || e == Send
                || e.StartsWith("POST /api/conversations/prior/sandbox/release", StringComparison.Ordinal)
            ),
        ];

    private static void ExpectFailure(Exception? error, string failure, string directory)
    {
        switch (failure)
        {
            case "throws":
                error.Should().BeOfType<HttpRequestException>();
                break;
            case "wrong-directory":
                error
                    .Should()
                    .BeOfType<ReviewHostContractException>()
                    .Which.Message.Should()
                    .Be($"The review host did not move 'prior' to '{directory}'.");
                break;
            default:
                error
                    .Should()
                    .BeOfType<ReviewHostContractException>()
                    .Which.Message.Should()
                    .Be(
                        $"The review host refused to release the sandbox of 'prior' ({failure}, "
                            + (failure == "409" ? "sandbox_release_while_running" : "sandbox_release_failed")
                            + ")."
                    );
                break;
        }
    }

    private static Task<HttpResponseMessage> Echo(HttpRequestMessage request) =>
        ReviewdRunnerTests.EchoSandboxReleaseAsync(request);

    private static Task<HttpResponseMessage> Refuse(string failure) =>
        failure switch
        {
            "throws" => throw new HttpRequestException("The connection to the review host was reset."),
            "409" => Task.FromResult(
                Reply(HttpStatusCode.Conflict, $$"""{"code":"sandbox_release_while_running","error":"{{Sentinel}}"}""")
            ),
            "503" => Task.FromResult(
                Reply(
                    HttpStatusCode.ServiceUnavailable,
                    $$"""{"code":"sandbox_release_failed","detail":"{{Sentinel}}"}"""
                )
            ),
            "wrong-directory" => Task.FromResult(
                Reply(HttpStatusCode.OK, """{"workingDirectoryRelPath":"PRs/nova-9"}""")
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, null),
        };

    private static HttpResponseMessage Reply(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json) };

    private static HttpResponseMessage Json(string json) => Reply(HttpStatusCode.OK, json);

    /// <summary>
    ///     Runs one review of PR 17 in slot 2 on a host whose conversation "prior" holds an earlier head's review, and
    ///     writes bounded diagnostics: the run's arguments, its events, its result and its stderr.
    /// </summary>
    private async Task<(int? Exit, Exception? Error, string Stderr)> RunAsync(params string[] extra)
    {
        var handler = new FakeHttpMessageHandler()
            .On(Record, _ => throw new InvalidOperationException("Record never matches."))
            .OnJson(HttpMethod.Get, "/capabilities", _capabilities)
            .OnAsync(
                ReviewdRunnerTests.IsSandboxRelease,
                async request =>
                {
                    var directory = JsonNode
                        .Parse(await request.Content!.ReadAsStringAsync())
                        ?["workingDirectoryRelPath"]?.GetValue<string>();
                    _events.Add($"POST {request.RequestUri!.AbsolutePath} {directory ?? "null"}");
                    return await _release(request, directory);
                }
            )
            .OnJson(
                HttpMethod.Get,
                "/api/conversations?",
                """[{"threadId":"prior","title":"Nova-17-99999999","workspace":"workspace","mode":"workspace-agent","provider":"test","lastUpdated":1,"clientToolsDisabled":true}]"""
            )
            .OnJson(
                HttpMethod.Get,
                "/prior/messages",
                ReviewdRunnerTests.Transcript(("User", ReviewdRunnerTests.ReviewPrompt('9')), ("Assistant", "Done."))
            )
            .OnJson(HttpMethod.Put, "/metadata", "{}")
            .OnJson(HttpMethod.Post, "/unfreeze", """{"threadId":"prior","frozen":false}""")
            .On(
                request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/messages"),
                _ => Json($$"""{"inputId":"input{{_events.Count(e => e == Send)}}","idempotencyKeyHonored":true}""")
            )
            .OnJson(HttpMethod.Get, "/status?", """{"status":"Completed","runId":"run","response":{"text":"done"}}""")
            .OnJson(HttpMethod.Get, "/run-state", """{"isInProgress":false,"currentRunId":null}""")
            .OnJson(HttpMethod.Get, "/subagents?", """{"schemaVersion":1,"nodes":[]}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new LmStreamingS2SClient(http, null, null, null);
        using var config = new ReviewdConfig(new ConfigurationBuilder().Build());
        string[] arguments =
        [
            "review",
            "run",
            "--repo",
            _repo.DisplayName,
            "--pr",
            "17",
            "--slot",
            "2",
            "--db",
            _database,
            "--session",
            "session",
            "--workspace",
            "workspace",
            "--provider",
            "test",
            "--execute",
            .. extra,
        ];
        await StoreSetAsync(
            $"reviewd/pr-snapshot/{_repo.NormalizedKey}",
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
        await PrThreadBindings.SetAsync(_database, _repo, "17", "prior", CancellationToken.None);
        var (stdout, stderr) = (Console.Out, Console.Error);
        using var error = new StringWriter();
        Console.SetOut(TextWriter.Null);
        Console.SetError(error);
        (int? Exit, Exception? Error, string Stderr) result;
        try
        {
            var exit = await ReviewRunner.RunAsync(
                CliOptions.Parse(arguments),
                config,
                client,
                SandboxAsync,
                CancellationToken.None,
                _ => Task.CompletedTask
            );
            result = (exit, null, error.ToString());
        }
        catch (Exception failure)
        {
            result = (null, failure, error.ToString());
        }
        finally
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
        }
        output.WriteLine(Bounded("args: " + string.Join(' ', extra)));
        foreach (var e in _events)
        {
            output.WriteLine(Bounded("event: " + e));
        }
        output.WriteLine(
            Bounded($"result: exit={result.Exit} error={result.Error?.GetType().Name}: {result.Error?.Message}")
        );
        output.WriteLine(Bounded("stderr: " + result.Stderr));
        return result;

        // A release is recorded by its route, with the directory it asked for.
        bool Record(HttpRequestMessage request)
        {
            if (!ReviewdRunnerTests.IsSandboxRelease(request))
            {
                _events.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            }
            return false;
        }
    }

    private Task<JsonObject> SandboxAsync(string phase, JsonObject args, string operation, CancellationToken ct)
    {
        _events.Add("phase " + phase);
        if (phase == "inspect" && _failConfirmationInspect && _events.Contains(Send))
        {
            _failConfirmationInspect = false;
            throw new InvalidOperationException("gateway hiccup");
        }
        var json = phase switch
        {
            "prepare" =>
                $$"""{"mergeSha":"{{new string(
                    'b',
                    40
                )}}","contextPath":"{{ReviewDirectory}}/setup-context.json","reviewDirectory":"{{ReviewDirectory}}","baselineComments":[]}""",
            "commit" => """{"status":"committed","commit":"cccccccccccccccccccccccccccccccccccccccc"}""",
            "verify" => """{"comments":[{"threadId":7,"commentId":1,"kind":"summary"}]}""",
            "cleanup" => new JsonObject
            {
                ["claimToken"] = args["claimToken"]!.DeepClone(),
                ["cleanBaselineVerified"] = true,
                ["evidencePreserved"] = true,
            }.ToJsonString(),
            _ => "{}",
        };
        return Task.FromResult(JsonNode.Parse(json)!.AsObject());
    }

    private async Task<JsonObject?> StoreGetAsync(string key)
    {
        using var connection = PrThreadBindings.Connection(_database);
        return await new SqliteKvStore(connection).GetAsync<JsonObject>(key);
    }

    private async Task StoreSetAsync(string key, JsonObject value)
    {
        using var connection = PrThreadBindings.Connection(_database);
        await new SqliteKvStore(connection).SetAsync(key, value);
    }

    private static string Bounded(string text) => text.Length <= 400 ? text : text[..400] + "...";
}
