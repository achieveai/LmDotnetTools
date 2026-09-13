using System.Net;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Persistence;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Tests.Workspace;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

public sealed class ReviewWorkflowInvokerTests
{
    [Fact]
    public async Task Legacy_exportable_invocation_record_requires_recovery_without_replay()
    {
        using var fixture = new Fixture();
        var invocation = AgentInvocation("legacy-private");
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(invocation.InvocationId))
        );
        System.IO.Directory.CreateDirectory(Path.Combine(fixture.Directory, "artifacts"));
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Directory, "artifacts", "invocation-" + hash + ".json"),
            "{\"private\":\"token-sentinel@example.com\"}"
        );
        var result = await fixture.Create().InvokeAsync(invocation);
        result.Status.Should().Be(WorkflowInvocationStatus.Unknown);
        result.Error.Should().NotContain("token-sentinel");
        fixture.Handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"Id\":\"one\"},{\"Id\":\"one\"}]")]
    [InlineData("[{\"Id\":\"one\"},{\"Id\":\"other\"}]")]
    public void Grading_rejects_missing_duplicate_and_unknown_finding_ids(string assessments)
    {
        var input = JsonNode.Parse("""{"Review":{"Findings":[{"Id":"one"},{"Id":"two"}]}}""")!;
        var output = new JsonObject { ["Assessments"] = JsonNode.Parse(assessments) };
        var validate = () => ReviewWorkflowInvoker.ValidateAssessmentIds(input, output);
        validate.Should().Throw<InvalidOperationException>();
        output["Assessments"] = JsonNode.Parse("""[{"Id":"two"},{"Id":"one"}]""");
        ReviewWorkflowInvoker.ValidateAssessmentIds(input, output);
    }

    [Fact]
    public async Task Independent_catalog_timeout_stays_retryable_and_does_not_persist_private_diagnostics()
    {
        using var fixture = new Fixture();
        fixture.GatewaySkills.Failure = new OperationCanceledException("secret-sentinel@example.com");
        var result = await fixture.Create().InvokeAsync(AgentInvocation("timeout"));
        result.Status.Should().Be(WorkflowInvocationStatus.Unknown);
        result.Error.Should().NotContain("secret-sentinel");
        fixture.Handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Lost_publication_response_is_adopted_from_provider_evidence_after_restart_without_posting_again()
    {
        using var fixture = new Fixture();
        await fixture.PrepareAsync();
        var publisher = new LostResponsePublisher();
        fixture.Publisher = publisher;
        var run = fixture.Workspace.Run;
        var repo = fixture.Workspace.Store.GetRepo(run.RepoId)!;
        var request = new PostReviewRequest(
            run.Id,
            new IdempotencyKeyComponents(
                "github",
                repo.OrgOrOwner,
                repo.Project,
                repo.RepoStableId ?? repo.NormalizedKey,
                run.PrId,
                "workflow-publish-summary",
                "workflow-publication",
                "action",
                run.HeadSha,
                run.VariantId
            ),
            new ReviewCommentTarget(repo, run.PrId),
            "The complete intended review.",
            true,
            true,
            () => true
        );
        var poster = new ReviewPoster(publisher, fixture.Workspace.Store, NullLogger<ReviewPoster>.Instance);
        await poster.Invoking(value => value.PostReviewAsync(request, default)).Should().ThrowAsync<IOException>();
        fixture.Handler.OnJson(
            HttpMethod.Post,
            "/messages",
            "{\"inputId\":\"input-1\",\"idempotencyKeyHonored\":true}"
        );
        var invocation = AgentInvocation("lost-publication");
        (await fixture.Create().InvokeAsync(invocation)).Status.Should().Be(WorkflowInvocationStatus.Unknown);
        fixture.Scopes.ActiveCount.Should().Be(1);
        fixture.Workspace.Store.GetOutboxForRun(run.Id).Single().Status.Should().Be(OutboxStatus.Sending);
        publisher.ProofVisible = true;
        var resumed = await fixture.Create().ReconcileAsync(invocation);
        resumed.Status.Should().Be(WorkflowInvocationStatus.Completed, resumed.Error);
        fixture.Scopes.ActiveCount.Should().Be(0);
        fixture.Workspace.Store.GetOutboxForRun(run.Id).Single().Status.Should().Be(OutboxStatus.Posted);
        publisher.PostCount.Should().Be(1);
        fixture
            .Handler.Requests.Count(request =>
                request.Method == HttpMethod.Post
                && request.Uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal)
            )
            .Should()
            .Be(1);
    }

    [Fact]
    public async Task Session_is_durable_before_send_and_reused_after_composite_recreation()
    {
        using var fixture = new Fixture();
        await fixture.PrepareAsync();
        fixture.Handler.On(
            request =>
                request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal),
            _ =>
            {
                fixture
                    .Workspace.Store.GetArtifacts(fixture.Workspace.Run.Id)
                    .Should()
                    .Contain(a =>
                        a.ArtifactKind.StartsWith("workflow-session:", StringComparison.Ordinal)
                        && a.Payload.Contains("thread-parent", StringComparison.Ordinal)
                    );
                fixture
                    .Workspace.Store.ListDeepLinkConversationsMintedBefore(DateTimeOffset.UtcNow.AddSeconds(1))
                    .Should()
                    .ContainSingle(row =>
                        row.ThreadId == "thread-parent" && row.Title!.Contains("task", StringComparison.Ordinal)
                    );
                return new(HttpStatusCode.Accepted)
                {
                    Content = new StringContent("{\"inputId\":\"input-1\",\"idempotencyKeyHonored\":true}"),
                };
            }
        );
        var first = await fixture.Create().InvokeAsync(AgentInvocation("one"));
        first.Status.Should().Be(WorkflowInvocationStatus.Completed, first.Error);
        var second = await fixture.Create().InvokeAsync(AgentInvocation("two"));
        second.Status.Should().Be(WorkflowInvocationStatus.Completed);
        fixture
            .Handler.Requests.Count(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/conversations")
            .Should()
            .Be(1);
        fixture.ScopeCreations.Should().Be(2);
        fixture.Scopes.ActiveCount.Should().Be(0);
    }

    [Fact]
    public async Task Reconciliation_without_session_mapping_never_provisions_or_sends()
    {
        using var fixture = new Fixture();
        var result = await fixture.Create().ReconcileAsync(AgentInvocation("one"));
        result.Status.Should().Be(WorkflowInvocationStatus.Unknown, result.Error);
        fixture.Handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Script_result_artifact_reconciles_without_replaying_the_script()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Directory, "once.py"),
            "import json,pathlib,sys\ne=json.load(sys.stdin)\np=pathlib.Path('calls.txt')\np.write_text(p.read_text()+'x' if p.exists() else 'x')\nprint(json.dumps(e['Context']))\n"
        );
        var invocation = AgentInvocation("script") with
        {
            Task = new WorkflowTask
            {
                Id = "script",
                PromptTemplate = "",
                Delegate = DelegateKind.Script,
                Script = "once.py",
            },
        };
        var first = await fixture.Create().InvokeAsync(invocation);
        first.Status.Should().Be(WorkflowInvocationStatus.Completed, first.Error);
        var output = JsonNode.Parse(first.Output!)!;
        output["RunId"]!
            .GetValue<string>()
            .Should()
            .Be(fixture.Workspace.Run.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        output["Attempt"]!.GetValue<int>().Should().Be(1);
        var recovered = await fixture.Create().ReconcileAsync(invocation);
        recovered.Should().Be(first);
        (await File.ReadAllTextAsync(Path.Combine(fixture.Directory, "calls.txt"))).Should().Be("x");
        System.IO.Directory.GetFiles(Path.Combine(fixture.Directory, "private"), "*.json").Should().ContainSingle();
    }

    [Fact]
    public async Task Missing_script_evidence_never_runs_an_unknown_script()
    {
        using var fixture = new Fixture();
        var invocation = AgentInvocation("script") with
        {
            Task = new WorkflowTask
            {
                Id = "script",
                PromptTemplate = "",
                Delegate = DelegateKind.Script,
                Script = "missing.py",
            },
        };
        var result = await fixture.Create().ReconcileAsync(invocation);
        result.Status.Should().Be(WorkflowInvocationStatus.Unknown);
        fixture.Handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Confirmed_script_failure_is_failed_without_replaying_it_on_reconciliation()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Directory, "fail.py"), "import sys\nsys.exit(7)\n");
        var invocation = AgentInvocation("script-fail") with
        {
            Task = new WorkflowTask
            {
                Id = "script",
                PromptTemplate = "",
                Delegate = DelegateKind.Script,
                Script = "fail.py",
            },
        };
        var result = await fixture.Create().InvokeAsync(invocation);
        result.Status.Should().Be(WorkflowInvocationStatus.Failed);
        (await fixture.Create().ReconcileAsync(invocation)).Should().Be(result);
    }

    [Fact]
    public async Task Unresolved_publication_receipt_prevents_agent_completion()
    {
        using var fixture = new Fixture();
        await fixture.PrepareAsync();
        fixture.Workspace.Store.EnqueueOutbox(
            new OutboxEntry
            {
                ReviewRunId = fixture.Workspace.Run.Id,
                Provider = "github",
                IdempotencyKey = "pending-publication",
                Operation = "workflow-publish-summary",
                ArtifactKind = "workflow-publication",
                Status = OutboxStatus.Sending,
            }
        );
        fixture.Handler.OnJson(
            HttpMethod.Post,
            "/messages",
            "{\"inputId\":\"input-1\",\"idempotencyKeyHonored\":true}"
        );
        var result = await fixture.Create().InvokeAsync(AgentInvocation("one"));
        result.Status.Should().Be(WorkflowInvocationStatus.Unknown);
    }

    [Fact]
    public async Task Expired_initial_invocation_has_no_external_effect()
    {
        using var fixture = new Fixture();
        var result = await fixture
            .Create()
            .InvokeAsync(AgentInvocation("expired") with { DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(-1) });
        result.Status.Should().Be(WorkflowInvocationStatus.Failed);
        fixture.Handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Supported_reviewer_catalog_is_verified_before_the_host_is_provisioned()
    {
        using var fixture = new Fixture();
        await fixture.PrepareAsync();
        fixture.GatewaySkills.Support = new(true, 3, []);
        fixture.GatewaySkills.OnProbe = () => fixture.Handler.Requests.Should().BeEmpty();
        fixture.Handler.OnJson(
            HttpMethod.Post,
            "/messages",
            "{\"inputId\":\"input-1\",\"idempotencyKeyHonored\":true}"
        );

        var result = await fixture
            .Create(
                new CodeReviewDaemonOptions
                {
                    LmStreamingReviewMarketplace = "review-market",
                    LmStreamingProviderId = "provider",
                    ReviewSubAgentBarrierQuietSeconds = 1,
                }
            )
            .InvokeAsync(AgentInvocation("supported"));

        result.Status.Should().Be(WorkflowInvocationStatus.Completed, result.Error);
        fixture.GatewaySkills.Calls.Should().Be(1);
        fixture.GatewaySkills.LastMarketplaces.Should().Equal("review-market");
        fixture.Handler.Requests.Should().Contain(request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Missing_required_reviewer_skill_blocks_the_host_before_any_external_effect()
    {
        using var fixture = new Fixture();
        fixture.GatewaySkills.Support = new(false, 3, []);

        var result = await fixture.Create().InvokeAsync(AgentInvocation("missing-skill"));

        result.Status.Should().Be(WorkflowInvocationStatus.Failed);
        result.Error.Should().Contain("supported reviewer catalog").And.Contain("MISSING");
        fixture.GatewaySkills.Calls.Should().Be(1);
        fixture.Handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_reviewer_specialist_blocks_the_host_before_any_external_effect()
    {
        using var fixture = new Fixture();
        fixture.GatewaySkills.Support = new(true, 0, []);

        var result = await fixture.Create().InvokeAsync(AgentInvocation("missing-specialist"));

        result.Status.Should().Be(WorkflowInvocationStatus.Failed);
        result.Error.Should().Contain("sub-agents=0");
        fixture.GatewaySkills.Calls.Should().Be(1);
        fixture.Handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Reviewer_catalog_probe_failure_prevents_host_provisioning_and_leaves_the_work_retryable()
    {
        using var fixture = new Fixture();
        fixture.GatewaySkills.Failure = new IOException("gateway unavailable");

        var result = await fixture.Create().InvokeAsync(AgentInvocation("probe-failure"));

        result.Status.Should().Be(WorkflowInvocationStatus.Unknown);
        result.Error.Should().Contain("could not be verified").And.NotContain("gateway unavailable");
        fixture.GatewaySkills.Calls.Should().Be(1);
        fixture.Handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancellation_during_reviewer_catalog_admission_prevents_host_provisioning()
    {
        using var fixture = new Fixture();
        fixture.GatewaySkills.ObserveCancellation = true;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await fixture
            .Create()
            .Invoking(value => value.InvokeAsync(AgentInvocation("cancelled-admission"), cancellation.Token))
            .Should()
            .ThrowAsync<OperationCanceledException>();
        fixture.GatewaySkills.Calls.Should().Be(1);
        fixture.Handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Persisted_parent_pins_model_and_rejects_conflicting_authored_model()
    {
        using var fixture = new Fixture();
        await fixture.PrepareAsync();
        fixture.Handler.OnJson(
            HttpMethod.Post,
            "/messages",
            "{\"inputId\":\"input-1\",\"idempotencyKeyHonored\":true}"
        );
        (await fixture.Create().InvokeAsync(AgentInvocation("first")))
            .Status.Should()
            .Be(WorkflowInvocationStatus.Completed);
        var changedOptions = new CodeReviewDaemonOptions
        {
            LmStreamingProviderId = "different-default",
            ReviewSubAgentBarrierQuietSeconds = 1,
        };
        (await fixture.Create(changedOptions).InvokeAsync(AgentInvocation("next")))
            .Status.Should()
            .Be(WorkflowInvocationStatus.Completed);
        var conflicting = AgentInvocation("conflict");
        var before = fixture.Handler.Requests.Count;
        var result = await fixture
            .Create()
            .InvokeAsync(conflicting with { Task = conflicting.Task with { ModelId = "different-model" } });
        result.Status.Should().Be(WorkflowInvocationStatus.Failed);
        result.Error.Should().Contain("InvalidOperationException");
        fixture.Handler.Requests.Count.Should().Be(before);
    }

    [Fact]
    public async Task Persisted_parent_without_confirmed_network_policy_is_never_replaced_or_sent()
    {
        using var fixture = new Fixture();
        await fixture.PrepareAsync();
        fixture.Handler.OnJson(
            HttpMethod.Post,
            "/messages",
            "{\"inputId\":\"input-1\",\"idempotencyKeyHonored\":true}"
        );
        (await fixture.Create().InvokeAsync(AgentInvocation("first")))
            .Status.Should()
            .Be(WorkflowInvocationStatus.Completed);
        var artifact = fixture
            .Workspace.Store.GetArtifacts(fixture.Workspace.Run.Id)
            .Single(a => a.ArtifactKind.StartsWith("workflow-session:", StringComparison.Ordinal));
        var legacy = JsonNode.Parse(artifact.Payload)!.AsObject();
        legacy.Remove("PublicationPolicyVersion");
        fixture.Workspace.Store.AddArtifact(
            new ReviewArtifact
            {
                ReviewRunId = fixture.Workspace.Run.Id,
                ArtifactKind = artifact.ArtifactKind,
                ArtifactSchemaVersion = 1,
                Provider = artifact.Provider,
                Payload = legacy.ToJsonString(),
            }
        );
        var before = fixture.Handler.Requests.Count;
        var result = await fixture.Create().InvokeAsync(AgentInvocation("next"));
        result.Status.Should().Be(WorkflowInvocationStatus.Failed);
        fixture.Handler.Requests.Skip(before).Should().OnlyContain(request => request.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task Expired_reconciliation_reads_terminal_evidence_without_sending_again()
    {
        using var fixture = new Fixture();
        await fixture.PrepareAsync();
        fixture.Handler.OnJson(
            HttpMethod.Post,
            "/messages",
            "{\"inputId\":\"input-1\",\"idempotencyKeyHonored\":true}"
        );
        (await fixture.Create().InvokeAsync(AgentInvocation("provision")))
            .Status.Should()
            .Be(WorkflowInvocationStatus.Completed);
        var before = fixture.Handler.Requests.Count;
        var result = await fixture
            .Create()
            .ReconcileAsync(
                AgentInvocation("already-accepted") with
                {
                    DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                }
            );
        result.Status.Should().Be(WorkflowInvocationStatus.Completed, result.Error);
        fixture.Handler.Requests.Skip(before).Should().OnlyContain(request => request.Method == HttpMethod.Get);
        fixture
            .Handler.Requests.Skip(before)
            .Should()
            .Contain(request =>
                request.Uri.AbsolutePath.Contains("thread-parent", StringComparison.Ordinal)
                && request.Uri.Query.Contains("inputId=", StringComparison.Ordinal)
            );
    }

    [Fact]
    public async Task Expired_reconciliation_does_not_accept_unknown_descendants()
    {
        using var fixture = new Fixture();
        await fixture.PrepareAsync();
        fixture.Handler.OnJson(
            HttpMethod.Post,
            "/messages",
            "{\"inputId\":\"input-1\",\"idempotencyKeyHonored\":true}"
        );
        (await fixture.Create().InvokeAsync(AgentInvocation("provision")))
            .Status.Should()
            .Be(WorkflowInvocationStatus.Completed);
        fixture.SubagentsJson =
            "{\"schemaVersion\":1,\"nodes\":[{\"agentId\":\"child\",\"threadId\":\"child-thread\",\"parentThreadId\":\"thread-parent\",\"depth\":1,\"template\":\"reviewer\",\"status\":\"Unknown\"}]}";
        var result = await fixture
            .Create()
            .ReconcileAsync(
                AgentInvocation("already-accepted") with
                {
                    DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                }
            );
        result.Status.Should().Be(WorkflowInvocationStatus.Unknown);
        result.Error.Should().Contain("Descendant work remains unresolved");
    }

    [Fact]
    public async Task Real_runtime_script_agent_script_resumes_accepted_input_and_durably_completes_once()
    {
        using var fixture = new Fixture();
        await fixture.PrepareAsync();
        foreach (var id in new[] { "prepare", "retain" })
            await File.WriteAllTextAsync(
                Path.Combine(fixture.Directory, id + ".py"),
                $"import json,pathlib,sys\ne=json.load(sys.stdin)\np=pathlib.Path('{id}-calls.txt')\np.write_text(p.read_text()+'x' if p.exists() else 'x')\nprint(json.dumps({{'Done':True}}))\n"
            );
        fixture.Handler.OnJson(
            HttpMethod.Post,
            "/messages",
            "{\"inputId\":\"input-1\",\"idempotencyKeyHonored\":true}"
        );
        fixture.StatusUnavailable = true;
        var runtime = WorkflowRuntime.CreateNew();
        runtime.AutomaticInvocationTimeout = TimeSpan.FromMinutes(2);
        var steps = new[] { "prepare", "agent", "retain" };
        runtime.LoadDefinition(
            new WorkflowDefinition
            {
                Objective = "Composed durable resume",
                SchemaVersion = 1,
                StrictContracts = true,
                Nodes =
                [
                    new StartNode
                    {
                        Id = "start",
                        Title = "Start",
                        Next = ["prepare"],
                    },
                    .. steps.Select(
                        (id, index) =>
                            new ProceduralNode
                            {
                                Id = id,
                                Title = id,
                                Next = [index == 2 ? "done" : steps[index + 1]],
                                TaskList =
                                [
                                    new WorkflowTask
                                    {
                                        Id = id,
                                        PromptTemplate = "Return the typed result.",
                                        Delegate = index == 1 ? DelegateKind.Agent : DelegateKind.Script,
                                        Script = index == 1 ? null : id + ".py",
                                        Session = index == 1 ? "parent" : null,
                                        SubagentType = index == 1 ? "general-purpose" : null,
                                        Input =
                                            index == 0 ? [] : new JsonObject { ["from"] = "state." + steps[index - 1] },
                                        OutputSchema = JsonNode.Parse(
                                            "{\"type\":\"object\",\"properties\":{\"Done\":{\"type\":\"boolean\"}},\"required\":[\"Done\"],\"additionalProperties\":false}"
                                        ),
                                        Writes = new WriteSpec { To = "state." + id },
                                    },
                                ],
                            }
                    ),
                    new TerminalNode { Id = "done", Title = "Done" },
                ],
            }
        );
        var store = new FileWorkflowStore(Path.Combine(fixture.Directory, "snapshots"));
        (await runtime.RunAutomaticAsync(store, "instance", fixture.Create()))
            .Should()
            .Be(WorkflowInvocationStatus.Unknown);
        var snapshot = (await store.LoadAsync("instance"))!;
        snapshot.IsComplete.Should().BeFalse();
        snapshot.Tasks.Should().Contain(task => task.Status == WorkflowTaskStatus.InFlight);
        fixture.StatusUnavailable = false;
        var resumed = WorkflowRuntime.FromSnapshot(snapshot);
        resumed.AutomaticInvocationTimeout = TimeSpan.FromMinutes(2);
        (await resumed.RunAutomaticAsync(store, "instance", fixture.Create()))
            .Should()
            .Be(WorkflowInvocationStatus.Completed);
        (await store.LoadAsync("instance"))!.IsComplete.Should().BeTrue();
        foreach (var id in new[] { "prepare", "retain" })
            (await File.ReadAllTextAsync(Path.Combine(fixture.Directory, id + "-calls.txt"))).Should().Be("x");
        fixture
            .Handler.Requests.Count(request =>
                request.Method == HttpMethod.Post
                && request.Uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal)
            )
            .Should()
            .Be(1);
        fixture
            .Handler.Requests.Count(request =>
                request.Method == HttpMethod.Post && request.Uri.AbsolutePath == "/api/conversations"
            )
            .Should()
            .Be(1);
        fixture
            .Handler.Requests.Should()
            .Contain(request =>
                request.Method == HttpMethod.Get
                && request.Uri.AbsolutePath.Contains("thread-parent", StringComparison.Ordinal)
                && request.Uri.Query.Contains("inputId=", StringComparison.Ordinal)
            );
    }

    private static WorkflowInvocation AgentInvocation(string id) =>
        new()
        {
            InstanceId = "instance",
            InvocationId = id,
            UnitName = "node:1:task",
            SessionId = "review-parent-1",
            Input = new JsonObject { ["Value"] = true },
            DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(2),
            Task = new WorkflowTask
            {
                Id = "task",
                PromptTemplate = "Use the typed input.",
                OutputSchema = JsonNode.Parse("{\"type\":\"object\"}"),
            },
        };

    private sealed class Fixture : IDisposable
    {
        public WorkflowWorkspaceTests.Fixture Workspace { get; } = new();
        public string Directory { get; } =
            Path.Combine(Path.GetTempPath(), "workflow-composite-" + Guid.NewGuid().ToString("N"));
        public FakeHttpMessageHandler Handler { get; } = new();
        public int ScopeCreations { get; private set; }
        public IReviewCommentPublisher Publisher { get; set; } = new FakeReviewCommentPublisher();
        public FakeGatewaySkillProbe GatewaySkills { get; } = new();
        public WorkflowPublicationScopes Scopes { get; }
        public bool StatusUnavailable { get; set; }
        public string SubagentsJson { get; set; } = "{\"schemaVersion\":1,\"nodes\":[]}";
        private readonly HttpClient _http;
        private readonly LmStreamingS2SClient _client;

        public Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            Handler
                .OnJson(
                    HttpMethod.Get,
                    "/capabilities",
                    "{\"messageIdempotency\":true,\"spawnSuppression\":true,\"rootReasoningEffort\":true,\"actionToolSuppression\":true,\"workflowPublication\":true,\"workflowPublicationProviderId\":\"provider\",\"workflowPublicationModeId\":\"code-review-daemon\"}"
                )
                .On(
                    request =>
                        request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/conversations",
                    _ =>
                        new(HttpStatusCode.OK)
                        {
                            Content = new StringContent(
                                "{\"threadId\":\"thread-parent\",\"reasoningEffortAccepted\":true}"
                            ),
                        }
                )
                .On(
                    request =>
                        request.Method == HttpMethod.Get
                        && request.RequestUri!.AbsolutePath.EndsWith("/status", StringComparison.Ordinal),
                    _ =>
                        StatusUnavailable
                            ? throw new IOException("Host status response interrupted after input acceptance.")
                            : new(HttpStatusCode.OK)
                            {
                                Content = new StringContent(
                                    "{\"status\":\"Completed\",\"runId\":\"host-run\",\"response\":{\"text\":\"{\\\"Done\\\":true}\"}}"
                                ),
                            }
                )
                .On(
                    request =>
                        request.Method == HttpMethod.Get
                        && request.RequestUri!.AbsolutePath.EndsWith("/subagents", StringComparison.Ordinal),
                    _ => new(HttpStatusCode.OK) { Content = new StringContent(SubagentsJson) }
                );
            _http = new HttpClient(Handler) { BaseAddress = new Uri("https://host/") };
            _client = new LmStreamingS2SClient(_http, "secret", "app", "key");
            Scopes = new WorkflowPublicationScopes(new InMemoryWorkflowStore(), _client);
        }

        public async Task PrepareAsync()
        {
            await Workspace.Workspace.AcquireAsync(Workspace.Run, "instance", default);
            await Workspace.Workspace.PrepareAssignedAsync(Workspace.Run, Workspace.Admission, "instance", default);
        }

        public ReviewWorkflowInvoker Create(CodeReviewDaemonOptions? configuredOptions = null)
        {
            var options =
                configuredOptions
                ?? new CodeReviewDaemonOptions
                {
                    LmStreamingProviderId = "provider",
                    ReviewSubAgentBarrierQuietSeconds = 1,
                };
            var dispatcher = new WorkflowOperationDispatcher(
                Workspace.Store,
                Workspace.Workspace,
                options,
                Directory,
                (_, _) => throw new InvalidOperationException("No artifact operation is configured for this test.")
            );
            return new(
                Workspace.Run,
                "instance",
                Directory,
                Directory,
                new WorkflowScriptInvoker(),
                dispatcher,
                _client,
                Workspace.Workspace,
                Workspace.Store,
                options,
                Scopes,
                (run, instance, _) =>
                {
                    ScopeCreations++;
                    return Task.FromResult(
                        new ReviewPublicationTools(
                            run,
                            Workspace.Store.GetRepo(run.RepoId)!,
                            instance,
                            new ReviewPoster(Publisher, Workspace.Store, NullLogger<ReviewPoster>.Instance),
                            new MockPrProvider(
                                "github",
                                [],
                                new OpaqueCursor
                                {
                                    Provider = "github",
                                    Scope = "test",
                                    CursorVersion = 1,
                                    CursorPayload = "{}",
                                }
                            )
                            {
                                CurrentHeadSha = run.HeadSha,
                            },
                            new DiffManifest("base", "head", []),
                            false,
                            () => true
                        )
                    );
                },
                GatewaySkills,
                NullLoggerFactory.Instance
            );
        }

        public void Dispose()
        {
            _http.Dispose();
            Workspace.Dispose();
            System.IO.Directory.Delete(Directory, true);
        }
    }

    private sealed class FakeGatewaySkillProbe : IGatewaySkillProbe
    {
        public GatewaySkillSupport Support { get; set; } = new(true, 1, []);
        public Exception? Failure { get; set; }
        public bool ObserveCancellation { get; set; }
        public Action? OnProbe { get; set; }
        public int Calls { get; private set; }
        public IReadOnlyList<string> LastMarketplaces { get; private set; } = [];

        public Task<GatewaySkillSupport> ProbeAsync(
            IReadOnlyList<string> marketplaces,
            CancellationToken cancellationToken
        )
        {
            Calls++;
            LastMarketplaces = marketplaces;
            OnProbe?.Invoke();
            if (ObserveCancellation)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            return Failure is null ? Task.FromResult(Support) : Task.FromException<GatewaySkillSupport>(Failure);
        }
    }

    private sealed class LostResponsePublisher : IReviewCommentPublisher
    {
        public string Provider => "github";
        public bool ProofVisible { get; set; }
        public int PostCount { get; private set; }
        private string? _key;

        public Task<PostedComment?> FindPostedCommentAsync(
            ReviewCommentTarget target,
            string idempotencyKey,
            CancellationToken cancellationToken
        ) => Task.FromResult<PostedComment?>(ProofVisible && _key == idempotencyKey ? new PostedComment("22") : null);

        public Task<PostedComment> PostReviewCommentAsync(
            ReviewCommentTarget target,
            string idempotencyKey,
            string body,
            CancellationToken cancellationToken
        )
        {
            PostCount++;
            _key = idempotencyKey;
            return Task.FromException<PostedComment>(new IOException("Remote post succeeded; response was lost."));
        }

        public Task<IReadOnlyList<ExistingReviewComment>> ListExistingReviewCommentsAsync(
            ReviewCommentTarget target,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<ExistingReviewComment>>([]);
    }
}
