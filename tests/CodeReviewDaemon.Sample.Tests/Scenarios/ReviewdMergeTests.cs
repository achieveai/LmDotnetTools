extern alias Reviewd;

using System.Net;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.Misc.Storage;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Reviewd::Reviewd.Sample;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

public sealed class ReviewdMergeTests
{
    [Fact]
    public async Task Wrong_merge_topology_with_conflicts_never_admits_a_resolver()
    {
        using var merge = new MergeFixture();
        merge.Verification = _ =>
        {
            var facts = MergeFixture.Facts(false, false);
            facts["headSha"] = new string('d', 40);
            facts["blockers"] = new JsonArray(new JsonObject { ["code"] = "MERGE_TOPOLOGY_CHANGED" });
            return facts;
        };
        try
        {
            (await merge.ExecuteAsync()).Should().Be(1);
        }
        catch (InvalidOperationException)
        {
            // Either an explicit rejection or a blocked result is safe; admission is not.
        }
        merge.Sends.Should().Be(0);
        merge.Provisions.Should().Be(0);
        merge.ReadReceipt()?["readyToPush"]?.GetValue<bool>().Should().NotBe(true);
    }

    [Fact]
    public async Task Retained_ready_merge_rechecks_live_PR_before_reporting_readiness()
    {
        using var merge = new MergeFixture();
        (await merge.ExecuteAsync()).Should().Be(0);
        merge.ReadReceipt()!["stage"]!.GetValue<string>().Should().Be("ready-to-push");
        merge.Live = () =>
        {
            var live = MergeFixture.Facts(true, true);
            live["headSha"] = new string('e', 40);
            live["blockers"] = new JsonArray(new JsonObject { ["code"] = "PR_CLOSED_OR_DRAFT" });
            return live;
        };
        var phaseCount = merge.Phases.Count;
        var execute = () => merge.ExecuteAsync();
        await execute.Should().ThrowAsync<InvalidOperationException>();
        merge.Phases.Skip(phaseCount).Should().Contain("inspect");
        merge.Sends.Should().Be(1);
        merge.Provisions.Should().Be(1);
        merge.ReadReceipt()!["readyToPush"]!.GetValue<bool>().Should().BeFalse();
        merge.ReadReceipt()!["stage"]!.GetValue<string>().Should().Be("blocked");
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 1)]
    [InlineData(true, true, 2)]
    public async Task Readiness_requires_passing_checks_and_exact_independent_tree_approval(
        bool checksPassed,
        bool conflict,
        int expectedSends
    )
    {
        using var merge = new MergeFixture();
        merge.Verification = read => MergeFixture.Facts(!(conflict && read == 1), checksPassed);
        (await merge.ExecuteAsync()).Should().Be(checksPassed ? 0 : 1);
        var receipt = merge.ReadReceipt()!;
        receipt["stage"]!.GetValue<string>().Should().Be(checksPassed ? "ready-to-push" : "blocked");
        receipt["readyToPush"]!.GetValue<bool>().Should().Be(checksPassed);
        merge.Sends.Should().Be(expectedSends);
        merge.Provisions.Should().Be(expectedSends);
        if (checksPassed)
        {
            receipt["reviewedTreeSha"]!.GetValue<string>().Should().Be(MergeFixture.Tree);
            receipt["resultDigest"]!
                .GetValue<string>()
                .Should()
                .Be(MergeRunner.ResultDigest(merge.Request, receipt["verification"]!.AsObject()));
            merge.Prompts.Last().Should().Contain("pinned merge tree " + MergeFixture.Tree);
        }
        merge.Phases.Should().NotContain("publish");
    }

    [Fact]
    public async Task Identified_input_survives_cancellation_and_resumes_with_GET_only()
    {
        using var merge = new MergeFixture { CancelStatus = true };
        var execute = () => merge.ExecuteAsync();
        await execute.Should().ThrowAsync<OperationCanceledException>();
        var receipt = merge.ReadReceipt()!;
        receipt["stage"]!.GetValue<string>().Should().Be("reviewer-running");
        receipt["reviewerInputId"]!.GetValue<string>().Should().Be("input1");
        var requests = merge.Handler.Requests.Count;
        merge.CancelStatus = false;
        (await merge.ExecuteAsync()).Should().Be(0);
        merge.Handler.Requests.Skip(requests).Should().OnlyContain(r => r.Method == HttpMethod.Get);
        merge.Sends.Should().Be(1);
        merge.Provisions.Should().Be(1);
        merge.ReadReceipt()!["stage"]!.GetValue<string>().Should().Be("ready-to-push");
    }

    [Fact]
    public async Task Ambiguous_send_persists_admission_and_never_replays()
    {
        using var merge = new MergeFixture { AmbiguousSend = true };
        var execute = () => merge.ExecuteAsync();
        await execute.Should().ThrowAsync<HttpRequestException>();
        var receipt = merge.ReadReceipt()!;
        receipt["stage"]!.GetValue<string>().Should().Be("reviewer-send-pending");
        receipt["reviewerSendKey"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        receipt["reviewerInputId"].Should().BeNull();
        var requests = merge.Handler.Requests.Count;
        await execute.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no automatic replay*");
        merge.Handler.Requests.Should().HaveCount(requests);
        merge.Sends.Should().Be(1);
        merge.Provisions.Should().Be(1);
    }

    [Fact]
    public async Task Another_retained_artifact_digest_holds_the_workspace_before_any_operation()
    {
        using var merge = new MergeFixture("artifact");
        using (var connection = PrThreadBindings.Connection(merge.Database))
            await new SqliteKvStore(connection).SetAsync(
                "reviewd/merge/other-digest",
                new JsonObject
                {
                    ["request"] = new JsonObject { ["kind"] = "artifact" },
                    ["scope"] = new JsonObject { ["workspace"] = "workspace" },
                    ["stage"] = "blocked",
                }
            );
        var execute = () => merge.ExecuteAsync();
        await execute.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Another retained artifact merge*");
        merge.Handler.Requests.Should().BeEmpty();
        merge.Phases.Should().BeEmpty();
    }

    private sealed class MergeFixture : IDisposable
    {
        public const string Tree = "cccccccccccccccccccccccccccccccccccccccc";
        public string Database { get; } = Path.Combine(Path.GetTempPath(), $"reviewd-merge-{Guid.NewGuid():N}.db");
        public JsonObject Request { get; }
        public FakeHttpMessageHandler Handler { get; } = new();
        public List<string> Phases { get; } = [];
        public List<string> Prompts { get; } = [];
        public int Sends { get; private set; }
        public int Provisions { get; private set; }
        public bool CancelStatus { get; set; }
        public bool AmbiguousSend { get; set; }
        public Func<int, JsonObject> Verification { get; set; } = _ => Facts(true, true);
        public Func<JsonObject> Live { get; set; } = () => Facts(true, true);
        private readonly HttpClient _http;
        private readonly LmStreamingS2SClient _client;
        private readonly CliOptions _options;
        private readonly List<string> _threads = [];
        private int _verifies;

        public MergeFixture(string kind = "source")
        {
            Request = new JsonObject
            {
                ["kind"] = kind,
                ["repoUrl"] = "https://example.invalid/repository",
                ["sourceRef"] = "refs/heads/source",
                ["targetRef"] = "refs/heads/target",
                ["sourceSha"] = new string('a', 40),
                ["targetSha"] = new string('b', 40),
                ["checks"] = new JsonArray(
                    new JsonObject { ["argv"] = new JsonArray("dotnet", "build"), ["directory"] = "." }
                ),
            };
            _options = CliOptions.Parse([
                "merge",
                "resolve",
                "--request",
                "unused.json",
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
            Handler.On(_ => true, Respond);
            _http = new HttpClient(Handler) { BaseAddress = new Uri("http://fixture.invalid/") };
            _client = new LmStreamingS2SClient(_http, null, null, null);
        }

        public Task<int> ExecuteAsync() =>
            MergeRunner.RunAsync(_options, Request, _client, Sandbox, CancellationToken.None, _ => Task.CompletedTask);

        public JsonObject? ReadReceipt()
        {
            using var connection = PrThreadBindings.Connection(Database);
            return new SqliteKvStore(connection)
                .GetAsync<JsonObject>("reviewd/merge/" + SlotRelease.Digest(Request.ToJsonString()))
                .GetAwaiter()
                .GetResult();
        }

        private Task<JsonObject> Sandbox(string phase, JsonObject args, string operation, CancellationToken ct)
        {
            Phases.Add(phase);
            return Task.FromResult(
                phase switch
                {
                    "prepare" or "inspect-artifact" => Facts(true, true),
                    "inspect" => Live(),
                    "verify" => Verification(++_verifies),
                    _ => throw new InvalidOperationException("Unexpected sandbox phase " + phase),
                }
            );
        }

        public static JsonObject Facts(bool resolved, bool checksPassed) =>
            new()
            {
                ["directory"] = "operator-scratch/fixture/repo",
                ["headSha"] = new string('a', 40),
                ["mergeSha"] = new string('b', 40),
                ["conflictPaths"] = resolved ? [] : new JsonArray("product.cs"),
                ["mergeResolved"] = resolved,
                ["checksPassed"] = checksPassed,
                ["treeSha"] = resolved ? Tree : null,
                ["checks"] = new JsonArray(
                    new JsonObject
                    {
                        ["argv"] = new JsonArray("dotnet", "build"),
                        ["exitCode"] = checksPassed ? JsonValue.Create(0) : null,
                        ["missingTool"] = !checksPassed,
                    }
                ),
                ["blockers"] = new JsonArray(),
            };

        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            string json;
            if (path.EndsWith("capabilities", StringComparison.Ordinal))
                json =
                    """{"messageIdempotency":true,"spawnSuppression":true,"rootReasoningEffort":true,"conversationWorkingDirectory":true}""";
            else if (path == "/api/conversations" && request.Method == HttpMethod.Post)
            {
                _threads.Add("thread" + ++Provisions);
                json = new JsonObject { ["threadId"] = _threads.Last() }.ToJsonString();
            }
            else if (path == "/api/conversations")
                json = new JsonArray(
                    _threads
                        .Select(thread =>
                            (JsonNode)
                                new JsonObject
                                {
                                    ["threadId"] = thread,
                                    ["workspace"] = "workspace",
                                    ["provider"] = "test",
                                    ["mode"] = "workspace-agent",
                                }
                        )
                        .ToArray()
                ).ToJsonString();
            else if (path.EndsWith("messages", StringComparison.Ordinal))
            {
                var body = JsonNode.Parse(Handler.Requests.Last().Body!)!;
                body["suppressSubAgentSpawning"]!.GetValue<bool>().Should().BeTrue();
                body["idempotencyKey"]!.GetValue<string>().Should().NotBeNullOrEmpty();
                Prompts.Add(body["text"]!.GetValue<string>());
                Sends++;
                if (AmbiguousSend)
                    throw new HttpRequestException("Simulated lost admission response");
                json = new JsonObject
                {
                    ["inputId"] = "input" + Sends,
                    ["spawningSuppressed"] = true,
                    ["idempotencyKeyHonored"] = true,
                }.ToJsonString();
            }
            else if (path.EndsWith("status", StringComparison.Ordinal))
            {
                if (CancelStatus)
                    throw new OperationCanceledException("Simulated cancellation after identified input");
                json = new JsonObject
                {
                    ["status"] = "Completed",
                    ["runId"] = "run",
                    ["response"] = new JsonObject { ["text"] = "merge-review: APPROVE " + Tree },
                }.ToJsonString();
            }
            else if (path.EndsWith("run-state", StringComparison.Ordinal))
                json = """{"isInProgress":false,"currentRunId":null}""";
            else if (path.EndsWith("subagents", StringComparison.Ordinal))
                json = """{"schemaVersion":1,"nodes":[]}""";
            else
                throw new InvalidOperationException("Unexpected HTTP route " + path);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }

        public void Dispose()
        {
            _http.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(Database);
        }
    }
}
