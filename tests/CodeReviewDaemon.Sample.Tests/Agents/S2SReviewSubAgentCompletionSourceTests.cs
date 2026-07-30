using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;

namespace CodeReviewDaemon.Sample.Tests.Agents;

/// <summary>
/// Unit tests for <see cref="S2SReviewSubAgentCompletionSource"/>: a thin adapter that delegates straight
/// to <see cref="LmStreamingS2SClient.GetSubAgentTreeAsync"/> — the schema-v1 mapping, version-skew
/// fail-closed behavior, and malformed-status-to-Unknown mapping are all already covered by
/// <c>LmStreamingS2SClientTests</c>; these tests only pin the adapter's own contract: it forwards the
/// given parent thread id as the root thread id to poll, ignores the <c>run</c> argument (the poll target
/// is determined entirely by the thread id, exactly like the in-process source), and propagates the
/// client's snapshot/exception unchanged.
/// </summary>
public sealed class S2SReviewSubAgentCompletionSourceTests
{
    private static HttpClient NewHttp(FakeHttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("http://localhost:5051/") };

    private static ReviewRun TestRun() =>
        new()
        {
            RepoId = 1,
            PrId = "42",
            HeadSha = "head-sha",
            BaseSha = "base-sha",
            TriggerWatermark = "watermark-1",
            ReviewKind = "full",
            VariantId = "primary",
            Mode = "collect-only",
            Stage = ReviewStage.Reviewed,
            WorkflowStatus = WorkflowStatus.Running,
            PrLifecycleState = PrLifecycleState.Open,
        };

    [Fact]
    public async Task GetSnapshotAsync_PollsTheGivenParentThreadId_AndReturnsTheClientsSnapshot()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "api/conversations/thread-root/subagents?recursive=true",
                "{\"schemaVersion\":1,\"nodes\":[{\"agentId\":\"a1\",\"threadId\":\"thread-a1\","
                    + "\"parentThreadId\":\"thread-root\",\"depth\":1,\"template\":\"reviewer\","
                    + "\"status\":\"running\"}]}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");
        var source = new S2SReviewSubAgentCompletionSource(client);

        var snapshot = await source.GetSnapshotAsync(TestRun(), "thread-root", CancellationToken.None);

        var node = snapshot.Nodes.Should().ContainSingle().Subject;
        node.AgentId.Should().Be("a1");
        node.ParentThreadId.Should().Be("thread-root");
        node.Status.Should().Be(ReviewSubAgentStatus.Running);
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task GetSnapshotAsync_PropagatesTheClientsFailClosedException_WithoutSwallowingIt()
    {
        // An incompatible/unavailable host response must never surface as an empty-success snapshot — the
        // adapter must let the client's InvalidOperationException propagate unchanged.
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "subagents", "{\"schemaVersion\":2,\"nodes\":[]}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");
        var source = new S2SReviewSubAgentCompletionSource(client);

        var act = () => source.GetSnapshotAsync(TestRun(), "thread-root", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void Constructor_ThrowsOnANullClient()
    {
        var act = () => new S2SReviewSubAgentCompletionSource(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
