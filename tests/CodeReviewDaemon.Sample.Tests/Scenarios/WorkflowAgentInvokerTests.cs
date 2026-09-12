using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

public sealed class WorkflowAgentInvokerTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(),
        "workflow-agent-" + Guid.NewGuid().ToString("N")
    );

    public WorkflowAgentInvokerTests()
    {
        Directory.CreateDirectory(Path.Combine(_workspace, "skills"));
        File.WriteAllText(Path.Combine(_workspace, "skills", "review.md"), "TRUSTED SKILL: read the admitted context.");
    }

    public void Dispose() => Directory.Delete(_workspace, recursive: true);

    private static WorkflowInvocation Invocation(bool correction = false) =>
        new()
        {
            InstanceId = "instance-1",
            InvocationId = correction ? "invocation-correction" : "invocation-1",
            UnitName = "assess:1:assess:task",
            SessionId = "review-parent-binding",
            IsCorrection = correction,
            ValidationError = correction ? "$.Decision must be Boolean; received string." : null,
            Input = JsonNode.Parse("""{"Decision":true,"Description":"literal {{not-a-binding}}"}""")!,
            Task = new WorkflowTask
            {
                Id = "assess:task",
                Session = "review-parent",
                Skills = ["skills/review.md"],
                PromptTemplate = "Assess the discussion. Use only admitted scoped tools.",
                MaxValidationRetries = 1,
                OutputSchema = JsonNode.Parse(
                    """{"type":"object","additionalProperties":false,"required":["Decision"],"properties":{"Decision":{"type":"boolean"}}}"""
                ),
            },
        };

    private static FakeHttpMessageHandler Handler(bool acknowledge = true) =>
        new FakeHttpMessageHandler()
            .OnCurrentReviewHostCapabilities()
            .OnJson(
                HttpMethod.Post,
                "/messages",
                JsonSerializer.Serialize(
                    new
                    {
                        inputId = "input-1",
                        idempotencyKeyHonored = true,
                        spawningSuppressed = true,
                        actionToolsSuppressed = acknowledge,
                    }
                )
            )
            .OnJson(
                HttpMethod.Get,
                "/status",
                """{"status":"Completed","runId":"host-run-1","response":{"text":"{\"Decision\":true}"}}"""
            );

    private static S2SReviewAgent Agent(HttpClient http) =>
        new(
            new LmStreamingS2SClient(http, "secret", "app", "key"),
            "workspace-id",
            "provider",
            "mode",
            "host policy",
            null,
            NullLogger<S2SReviewAgent>.Instance,
            pollInterval: TimeSpan.FromMilliseconds(1),
            pollMaxInterval: TimeSpan.FromMilliseconds(1),
            overallTimeout: TimeSpan.FromSeconds(2),
            existingThreadId: "thread-parent"
        );

    private WorkflowAgentInvoker Invoker(S2SReviewAgent agent, WorkflowInvocationResult? unsettled = null) =>
        new(_workspace, (_, _, _) => Task.FromResult<S2SReviewAgent?>(agent), (_, _, _) => Task.FromResult(unsettled));

    [Fact]
    public async Task Sends_trusted_skill_instruction_typed_input_and_exact_schema_to_existing_parent()
    {
        var handler = Handler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var invocation = Invocation();
        var result = await Invoker(Agent(http)).InvokeAsync(invocation);
        result.Status.Should().Be(WorkflowInvocationStatus.Completed);
        result.Output.Should().Be("{\"Decision\":true}");
        var post = handler.Requests.Single(request => request.Method == HttpMethod.Post);
        post.Uri.AbsolutePath.Should().Be("/api/conversations/thread-parent/messages");
        var body = JsonNode.Parse(post.Body!)!;
        var prompt = body["text"]!.GetValue<string>();
        prompt
            .Should()
            .Contain("TRUSTED SKILL")
            .And.Contain(invocation.Task.PromptTemplate)
            .And.Contain(invocation.Input.ToJsonString())
            .And.Contain(invocation.Task.OutputSchema!.ToJsonString())
            .And.Contain("No Markdown fences, extra fields or surrounding prose.");
        body["suppressActionTools"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Correction_reuses_parent_and_requests_acknowledged_tool_free_turn()
    {
        var handler = Handler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var invoker = Invoker(Agent(http));
        (await invoker.InvokeAsync(Invocation())).Status.Should().Be(WorkflowInvocationStatus.Completed);
        (await invoker.InvokeAsync(Invocation(correction: true)))
            .Status.Should()
            .Be(WorkflowInvocationStatus.Completed);
        var posts = handler.Requests.Where(request => request.Method == HttpMethod.Post).ToArray();
        posts.Should().HaveCount(2);
        posts.Select(post => post.Uri.AbsolutePath).Distinct().Should().ContainSingle();
        var correction = JsonNode.Parse(posts[1].Body!)!;
        correction["suppressActionTools"]!.GetValue<bool>().Should().BeTrue();
        correction["suppressSubAgentSpawning"]!.GetValue<bool>().Should().BeTrue();
        correction["text"]!
            .GetValue<string>()
            .Should()
            .Contain("Do not repeat tool calls or publish anything")
            .And.Contain("$.Decision must be Boolean; received string.");
    }

    [Fact]
    public async Task Unacknowledged_correction_is_unknown_even_when_response_could_look_successful()
    {
        var handler = Handler(acknowledge: false);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        (await Invoker(Agent(http)).InvokeAsync(Invocation(correction: true)))
            .Status.Should()
            .Be(WorkflowInvocationStatus.Unknown);
        handler.CountRequests("/status").Should().Be(0);
    }

    [Fact]
    public async Task Reconcile_only_polls_the_exact_accepted_input_and_never_posts()
    {
        var handler = Handler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var invocation = Invocation(correction: true);
        var result = await Invoker(Agent(http)).ReconcileAsync(invocation);
        result.Status.Should().Be(WorkflowInvocationStatus.Completed);
        handler.Requests.Should().NotContain(request => request.Method == HttpMethod.Post);
        var query = Uri.UnescapeDataString(
            handler.Requests.Single(request => request.Uri.AbsolutePath.EndsWith("/status")).Uri.Query
        );
        query.Should().Contain(IdempotentInputId.Create(WorkflowAgentInvoker.InvocationKey(invocation), true, true));
    }

    [Fact]
    public async Task Authoritative_unsettled_effects_override_valid_model_payload()
    {
        var handler = Handler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var result = await Invoker(
                Agent(http),
                new(WorkflowInvocationStatus.Unknown, Error: "Publication receipt unresolved")
            )
            .InvokeAsync(Invocation());
        result.Status.Should().Be(WorkflowInvocationStatus.Unknown);
        result.Output.Should().BeNull();
    }

    [Fact]
    public async Task Reconcile_cannot_create_a_replacement_for_an_unavailable_session()
    {
        var invoker = new WorkflowAgentInvoker(
            _workspace,
            (_, create, _) =>
            {
                create.Should().BeFalse();
                return Task.FromResult<S2SReviewAgent?>(null);
            },
            (_, _, _) => throw new InvalidOperationException("No accepted run to settle.")
        );
        (await invoker.ReconcileAsync(Invocation())).Status.Should().Be(WorkflowInvocationStatus.Unknown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Terminal_failure_requires_effect_settlement_before_becoming_failed(bool unsettled)
    {
        var handler = new FakeHttpMessageHandler()
            .OnCurrentReviewHostCapabilities()
            .OnJson(HttpMethod.Post, "/messages", """{"inputId":"input-1","idempotencyKeyHonored":true}""")
            .OnJson(HttpMethod.Get, "/status", """{"status":"Errored","runId":"failed-run"}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var pending = unsettled
            ? new WorkflowInvocationResult(WorkflowInvocationStatus.Unknown, Error: "Unknown send")
            : null;
        var result = await Invoker(Agent(http), pending).InvokeAsync(Invocation());
        result.Status.Should().Be(unsettled ? WorkflowInvocationStatus.Unknown : WorkflowInvocationStatus.Failed);
    }

    [Fact]
    public async Task Old_host_is_rejected_by_read_only_preflight_before_first_send()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "conversations/capabilities",
            """{"messageIdempotency":true,"spawnSuppression":true,"rootReasoningEffort":true}"""
        );
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        (await Invoker(Agent(http)).InvokeAsync(Invocation())).Status.Should().Be(WorkflowInvocationStatus.Failed);
        handler.Requests.Should().NotContain(request => request.Method == HttpMethod.Post);
    }
}
