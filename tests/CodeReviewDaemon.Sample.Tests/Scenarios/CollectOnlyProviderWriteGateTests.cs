using System.Net;
using System.Net.Http.Headers;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// The collect-only capability gate. The repository owner's standing instruction is "do not post the review
/// comments, collect exactly what you would have posted", which every run has honoured — but only because
/// each posting attempt happened to die at the sandbox's egress proxy with
/// <c>policy_evaluation_failed</c>. That string appears nowhere in this repository: the control that held was
/// an OUTAGE in someone else's component, and its policy evaluation is healthy again.
/// <para>
/// These tests pin the daemon-side control that replaces that luck. When
/// <see cref="CodeReviewDaemonOptions.EnableCommentPosting"/> is false the daemon's own provider client has
/// no write capability at all — the write is refused at the policy seam, the credential is stripped, and the
/// refusal is RECORDED — while every read the review depends on (CI status, existing comments, the work-item
/// ancestry) still succeeds. With posting enabled nothing here refuses anything, which is what makes this a
/// gate rather than a removal of the feature.
/// </para>
/// <para>
/// Note what is deliberately NOT asserted anywhere below: that <c>review_outbox</c> holds no Posted row. That
/// assertion is vacuous for this defect. A review sub-agent posting straight to the provider REST API over
/// the egress proxy never touches that table, which is precisely why the existing evidence that "collect-only
/// is honoured" could not see this class of event at all.
/// </para>
/// </summary>
public sealed class CollectOnlyProviderWriteGateTests : LoggingTestBase
{
    public CollectOnlyProviderWriteGateTests(ITestOutputHelper output)
        : base(output)
    {
    }

    private const string AdoRepoEntry = "contoso/Platform/core";
    private const string PrThreadsRoute =
        "https://dev.azure.com/contoso/Platform/_apis/git/repositories/core/pullRequests/5501220/threads";

    /// <summary>An <see cref="IPolicyRefusalRecorder"/> that only remembers, so a test can ask what was recorded.</summary>
    private sealed class RecordingRefusals : IPolicyRefusalRecorder
    {
        public List<PolicyRefusalRecord> Recorded { get; } = [];

        public void Record(PolicyRefusalRecord refusal) => Recorded.Add(refusal);
    }

    private static CodeReviewDaemonOptions Options(bool enableCommentPosting) =>
        new()
        {
            EnabledRepos = [AdoRepoEntry],
            EnableCommentPosting = enableCommentPosting,
        };

    /// <summary>
    /// Builds the handler over the policies the PRODUCTION factory would build for this posture — not a
    /// hand-rolled policy. The factory is where <c>EnableCommentPosting</c> becomes a capability, so a test
    /// that constructed its own <see cref="OperationPolicy"/> would prove the policy works and say nothing
    /// about whether the daemon ever passes it the flag.
    /// </summary>
    private (HttpClient Client, FakeHttpMessageHandler Inner, RecordingRefusals Refusals) BuildAdoClient(
        bool enableCommentPosting)
    {
        var refusals = new RecordingRefusals();
        var factory = new PolicyEnforcedHttpClientFactory(
            Options(enableCommentPosting),
            LoggerFactory.CreateLogger<OperationPolicyHandler>(),
            LoggerFactory.CreateLogger<RetryHandler>(),
            refusals);

        var inner = new FakeHttpMessageHandler();
        _ = inner.On(_ => true, _ => new HttpResponseMessage(HttpStatusCode.OK));
        var handler = new OperationPolicyHandler(
            factory.BuildPolicies("ado"),
            "ado",
            LoggerFactory.CreateLogger<OperationPolicyHandler>(),
            refusals)
        {
            InnerHandler = inner,
        };

        return (new HttpClient(handler), inner, refusals);
    }

    [Fact]
    public async Task CollectOnly_refuses_a_provider_write_blocks_egress_and_records_the_refusal()
    {
        var (client, inner, refusals) = BuildAdoClient(enableCommentPosting: false);

        // The exact shape a posting agent would produce: a comment thread POST on the reviewed PR's own,
        // fully in-scope route. Nothing about the ROUTE is wrong here — the capability is what is missing.
        using var request = new HttpRequestMessage(HttpMethod.Post, PrThreadsRoute)
            .WithOperation(SandboxOperation.PostReviewComment);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "bot-token");

        var act = () => client.SendAsync(request, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<OperationDeniedException>()).Which;
        thrown.Operation.Should().Be(SandboxOperation.PostReviewComment);
        inner.Requests.Should().BeEmpty("a refused write must never reach the network");
        request.Headers.Authorization.Should()
            .BeNull("a refused write must also be credential-withheld (fail closed both ways)");

        refusals.Recorded.Should().ContainSingle("a refusal that leaves no trace is not auditable");
        var recorded = refusals.Recorded[0];
        recorded.Kind.Should().Be(PolicyRefusalKind.ProviderWrite);
        recorded.Provider.Should().Be("ado");
        recorded.Subject.Should().Be(nameof(SandboxOperation.PostReviewComment));
        recorded.Method.Should().Be("POST");
        recorded.Target.Should().Contain("pullRequests/5501220/threads");
        recorded.Reason.Should().Contain("collect-only");
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task CollectOnly_refuses_every_mutating_method_even_under_a_read_classification(string method)
    {
        // The tag is what the caller CLAIMED; the method is what the request would have done. A collect-only
        // run must refuse on the second, or a write mis-tagged as a read walks straight through the arm that
        // exists to serve CI status and work items.
        var (client, inner, refusals) = BuildAdoClient(enableCommentPosting: false);

        using var request = new HttpRequestMessage(new HttpMethod(method), PrThreadsRoute)
            .WithOperation(SandboxOperation.ReadProviderMetadata);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "bot-token");

        var act = () => client.SendAsync(request, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<OperationDeniedException>()).Which;
        thrown.Reason.Should().Contain(
            "collect-only",
            "the refusal must name the capability that was missing, not merely the method mismatch");
        inner.Requests.Should().BeEmpty();
        refusals.Recorded.Should().ContainSingle();
        refusals.Recorded[0].Kind.Should().Be(PolicyRefusalKind.ProviderWrite);
    }

    /// <summary>
    /// Every provider READ the review actually depends on, on a collect-only run. These are not decoration:
    /// the CI routes are what let the reviewer see a failing pipeline, the PR thread route is what lets it
    /// read the comments already on the PR, and the work-item route — added very recently — is the entire
    /// answer to "what was this change asked to do". A gate that closed any of them would be a regression
    /// dressed as a safety net.
    /// </summary>
    [Theory]
    // Existing comments on the PR (the reviewed repo's own route).
    [InlineData("https://dev.azure.com/contoso/Platform/_apis/git/repositories/core/pullRequests/5501220/threads")]
    // The PR's own linked-work-item list, which also sits under the repo route.
    [InlineData("https://dev.azure.com/contoso/Platform/_apis/git/repositories/core/pullRequests/5501220/workitems")]
    // CI verdict: the policy evaluation that names the build, the build + its timeline, the test summary.
    [InlineData("https://dev.azure.com/contoso/Platform/_apis/policy/evaluations?artifactId=x")]
    [InlineData("https://dev.azure.com/contoso/Platform/_apis/build/builds/39168345/timeline?api-version=7.1")]
    [InlineData("https://dev.azure.com/contoso/Platform/_apis/test/ResultSummaryByBuild?buildId=39168345")]
    // The work items THEMSELVES, walked up the parent chain to the Epic.
    [InlineData("https://dev.azure.com/contoso/Platform/_apis/wit/workitems?ids=1,2&$expand=relations")]
    // The org-scoped project route the confidentiality trust signal is established from.
    [InlineData("https://dev.azure.com/contoso/_apis/projects/Platform")]
    public async Task CollectOnly_still_permits_the_provider_reads_the_review_depends_on(string url)
    {
        var (client, inner, refusals) = BuildAdoClient(enableCommentPosting: false);

        using var request = new HttpRequestMessage(HttpMethod.Get, url)
            .WithOperation(SandboxOperation.ReadProviderMetadata);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "bot-token");

        using var response = await client.SendAsync(request, CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.Requests.Should().ContainSingle("a collect-only run reads provider metadata exactly as before");
        inner.Requests[0].Authorization.Should()
            .Be("Bearer bot-token", "the read keeps its credential; only writes lose theirs");
        refusals.Recorded.Should().BeEmpty();
    }

    [Fact]
    public async Task PostingEnabled_permits_the_same_provider_write()
    {
        // The conditional half. Same request, same route, same credential — only the operator's posture
        // differs. If this ever fails, the gate has stopped being a gate and become a ban.
        var (client, inner, refusals) = BuildAdoClient(enableCommentPosting: true);

        using var request = new HttpRequestMessage(HttpMethod.Post, PrThreadsRoute)
            .WithOperation(SandboxOperation.PostReviewComment);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "bot-token");

        using var response = await client.SendAsync(request, CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.Requests.Should().ContainSingle("posting is authorized, so the write must go through");
        inner.Requests[0].Authorization.Should().Be("Bearer bot-token");
        refusals.Recorded.Should().BeEmpty("nothing was refused");
    }

    [Fact]
    public void The_refusal_is_durable_and_readable_back_from_the_store()
    {
        // "Recorded" has to survive the process, or the next operator asking whether collect-only held is
        // back to reading logs that have already rolled.
        using var database = new TempSqliteDatabase();
        using var store = new ReviewStore(database.ConnectionString);
        var recorder = new StorePolicyRefusalRecorder(
            store,
            LoggerFactory.CreateLogger<StorePolicyRefusalRecorder>());

        recorder.Record(new PolicyRefusalRecord(
            new DateTimeOffset(2026, 8, 10, 4, 5, 6, TimeSpan.Zero),
            PolicyRefusalKind.ProviderWrite,
            "ado",
            nameof(SandboxOperation.PostReviewComment),
            "POST",
            PrThreadsRoute,
            "this policy is collect-only and has no provider-API write capability"));

        var rows = store.ListPolicyRefusals();

        rows.Should().ContainSingle();
        rows[0].Kind.Should().Be(PolicyRefusalKind.ProviderWrite);
        rows[0].Subject.Should().Be(nameof(SandboxOperation.PostReviewComment));
        rows[0].Target.Should().Be(PrThreadsRoute);
        rows[0].AtUtc.Should().Be(new DateTimeOffset(2026, 8, 10, 4, 5, 6, TimeSpan.Zero));
    }
}

/// <summary>
/// The second half of the gate: which sub-agent TEMPLATES a collect-only run may run. The lead reviewer
/// dispatched <c>ado:ado-devops-assistant</c> on eleven collect-only runs with briefs that spelled out the
/// posting — "Add inline comments where possible… then add/reply to summary with small delta and REQUEST
/// CHANGES" — and nothing anywhere recorded that it had happened.
/// </summary>
public sealed class ReviewSpawnGateTests : LoggingTestBase
{
    public ReviewSpawnGateTests(ITestOutputHelper output)
        : base(output)
    {
    }

    private sealed class RecordingRefusals : IPolicyRefusalRecorder
    {
        public List<PolicyRefusalRecord> Recorded { get; } = [];

        public void Record(PolicyRefusalRecord refusal) => Recorded.Add(refusal);
    }

    [Theory]
    [InlineData("ado:ado-devops-assistant")]
    [InlineData("ado-devops-assistant")]
    [InlineData("ADO:ADO-DevOps-Assistant")]
    [InlineData("code-reviewer:post-pr-review")]
    [InlineData("ado:ado-publish-pr")]
    [InlineData("ado:ado-babysit-pr")]
    [InlineData("ado:ado-pr-tender")]
    public void CollectOnly_refuses_a_posting_capable_template_and_names_it(string template)
    {
        var logs = new CapturingLoggerFactory();
        var refusals = new RecordingRefusals();
        var gate = new ReviewSpawnGate(
            postingEnabled: false,
            logs.CreateLogger("spawn-gate"),
            refusals);

        var allowed = gate.IsSpawnAllowed(146, template, "thread abc (agent def)");

        allowed.Should().BeFalse();
        logs.Capturing.CountAtLevel(LogLevel.Error, template).Should()
            .Be(1, "the refusal must NAME the template, or an operator cannot tell which agent was stopped");
        logs.Capturing.CountAtLevel(LogLevel.Error, "REFUSED").Should().Be(1);

        refusals.Recorded.Should().ContainSingle();
        refusals.Recorded[0].Kind.Should().Be(PolicyRefusalKind.SubAgentSpawn);
        refusals.Recorded[0].Subject.Should().Be(template);
        refusals.Recorded[0].Method.Should().Be("spawn");
        refusals.Recorded[0].Target.Should().Be("thread abc (agent def)");
    }

    [Fact]
    public void PostingEnabled_allows_the_same_posting_capable_template()
    {
        // Conditional, not a ban: an operator who turned posting on turned on the agents that post.
        var logs = new CapturingLoggerFactory();
        var refusals = new RecordingRefusals();
        var gate = new ReviewSpawnGate(postingEnabled: true, logs.CreateLogger("spawn-gate"), refusals);

        gate.IsSpawnAllowed(146, "ado:ado-devops-assistant").Should().BeTrue();

        logs.Capturing.MessagesAtLevel(LogLevel.Error).Should().BeEmpty();
        refusals.Recorded.Should().BeEmpty();
    }

    [Theory]
    [InlineData("code-reviewer:performance-review")]
    [InlineData("code-reviewer:pr-context-gatherer")]
    [InlineData("code-reviewer:test-coverage-review")]
    [InlineData("general-purpose")]
    public void CollectOnly_still_allows_the_review_specialists(string template)
    {
        // The specialists ARE the review. A gate that refused them would silently produce the zero-dispatch
        // shape the executor already warns about — a review with nothing behind it that still looks like one.
        var logs = new CapturingLoggerFactory();
        var refusals = new RecordingRefusals();
        var gate = new ReviewSpawnGate(postingEnabled: false, logs.CreateLogger("spawn-gate"), refusals);

        gate.IsSpawnAllowed(146, template).Should().BeTrue();

        refusals.Recorded.Should().BeEmpty();
    }

    /// <summary>A scripted roster source: returns the same snapshot on every poll.</summary>
    private sealed class ScriptedRoster : IReviewSubAgentCompletionSource
    {
        private readonly ReviewSubAgentTreeSnapshot _snapshot;

        public ScriptedRoster(params ReviewSubAgentNode[] nodes) =>
            _snapshot = new ReviewSubAgentTreeSnapshot(nodes);

        public int Polls { get; private set; }

        public Task<ReviewSubAgentTreeSnapshot> GetSnapshotAsync(
            ReviewRun run,
            string parentThreadId,
            CancellationToken ct)
        {
            Polls++;
            return Task.FromResult(_snapshot);
        }
    }

    private static ReviewSubAgentNode Node(string agentId, string template) =>
        new()
        {
            AgentId = agentId,
            ThreadId = $"thread-{agentId}",
            ParentThreadId = "root",
            Depth = 1,
            Status = ReviewSubAgentStatus.Completed,
            Template = template,
        };

    private static ReviewRun TestRun() =>
        new()
        {
            Id = 5501220,
            RepoId = 1,
            PrId = "5501220",
            HeadSha = "head",
            BaseSha = "base",
            TriggerWatermark = "w1",
            ReviewKind = "full",
            VariantId = "primary",
            Mode = "collect-only",
            Stage = ReviewStage.Reviewed,
            WorkflowStatus = WorkflowStatus.Running,
            PrLifecycleState = PrLifecycleState.Open,
        };

    [Fact]
    public async Task Gated_roster_source_refuses_once_per_template_and_forwards_the_snapshot_unchanged()
    {
        var logs = new CapturingLoggerFactory();
        var refusals = new RecordingRefusals();
        var inner = new ScriptedRoster(
            Node("a1", "code-reviewer:performance-review"),
            Node("a2", "ado:ado-devops-assistant"),
            Node("a3", "code-reviewer:test-coverage-review"));
        var source = new SpawnGatedSubAgentCompletionSource(
            inner,
            new ReviewSpawnGate(postingEnabled: false, logs.CreateLogger("spawn-gate"), refusals));

        var first = await source.GetSnapshotAsync(TestRun(), "root", CancellationToken.None);
        var second = await source.GetSnapshotAsync(TestRun(), "root", CancellationToken.None);

        // The snapshot is forwarded whole: filtering the refused child out would open the barrier while it
        // was still running, and would delete the very evidence that it ran.
        first.Nodes.Should().HaveCount(3);
        second.Nodes.Should().HaveCount(3);
        inner.Polls.Should().Be(2);

        source.RefusedTemplates.Should().ContainSingle().Which.Should().Be("ado:ado-devops-assistant");
        refusals.Recorded.Should().ContainSingle(
            "a barrier polls for minutes; one refusal per template per review, not one per poll");
        logs.Capturing.CountAtLevel(LogLevel.Error, "ado:ado-devops-assistant").Should().Be(1);
    }

    [Fact]
    public async Task Gated_roster_source_refuses_nothing_when_posting_is_enabled()
    {
        var logs = new CapturingLoggerFactory();
        var refusals = new RecordingRefusals();
        var source = new SpawnGatedSubAgentCompletionSource(
            new ScriptedRoster(Node("a2", "ado:ado-devops-assistant")),
            new ReviewSpawnGate(postingEnabled: true, logs.CreateLogger("spawn-gate"), refusals));

        var snapshot = await source.GetSnapshotAsync(TestRun(), "root", CancellationToken.None);

        snapshot.Nodes.Should().ContainSingle();
        source.RefusedTemplates.Should().BeEmpty();
        refusals.Recorded.Should().BeEmpty();
    }
}
