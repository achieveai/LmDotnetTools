using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using Xunit;
using static AchieveAi.LmDotnetTools.LmWorkflow.Tests.StartWorkflowTestHarness;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     A workflow controller's persistence thread id must be scoped to the CONVERSATION that launched the run,
///     not just the human-chosen (non-unique) <c>workflowId</c>. This is the regression guard for the "a fresh
///     workflow agent resumed a previous workflow agent because they shared a name" bug: two different
///     conversations can pick the same readable <c>workflowId</c>, and with an unscoped
///     <c>workflow-{workflowId}</c> thread over the SHARED conversation store they would land on the same
///     thread and inherit each other's controller history. Folding the launching conversation id into the
///     thread id (<c>workflow-{workflowId}-{conversationId}</c>) keeps each conversation's workflow agent on
///     its own thread, while staying deterministic so resuming the SAME conversation reconstructs the SAME id.
///     See <see cref="WorkflowControllerFreshStartTests"/> for the complementary same-thread suppression guard.
/// </summary>
public class WorkflowManagerConversationScopeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task StartAsync_ScopesControllerThread_ToLaunchingConversation()
    {
        var store = new InMemoryConversationStore();
        var controller = ScriptedController(DriveMinimalToTerminal);

        await using var manager = new WorkflowManager(
            () => controller.Object,
            EmptyControllerOptions(),
            controllerConversationStore: store,
            launchConversationId: () => "thread-conv-alpha"
        );

        var result = await manager.StartAsync("pr-review", MinimalDefinition(), WorkflowStartMode.Sync);
        result.Status.Should().Be("completed");

        // The controller conversation persists under the CONVERSATION-SCOPED thread ...
        store
            .GetMessageCount("workflow-pr-review-thread-conv-alpha")
            .Should()
            .BeGreaterThan(0, "the workflow agent's thread must be scoped to the launching conversation");

        // ... and NOT under the legacy unscoped thread that a different conversation reusing "pr-review" would hit.
        store
            .GetMessageCount("workflow-pr-review")
            .Should()
            .Be(0, "the unscoped workflow-{id} thread is what caused cross-conversation inheritance");

        // The presentation summary exposes the real thread id so the host does not reconstruct the stale one.
        manager
            .ListRuns()
            .Should()
            .ContainSingle()
            .Which.ThreadId.Should()
            .Be("workflow-pr-review-thread-conv-alpha");
    }

    [Fact]
    public async Task StartAsync_TwoConversations_ReusingOneWorkflowId_PersistOnSeparateThreads()
    {
        // The core guarantee: the SAME readable workflowId launched from TWO different conversations never
        // shares a controller thread, so neither inherits the other's conversation. (Completed ids are retained
        // per-manager, so distinct conversations are modeled as distinct managers over one shared store — the
        // real topology, where each conversation builds its own manager but the file store is process-wide.)
        var store = new InMemoryConversationStore();

        await using (
            var managerA = new WorkflowManager(
                () => ScriptedController(DriveMinimalToTerminal).Object,
                EmptyControllerOptions(),
                controllerConversationStore: store,
                launchConversationId: () => "thread-alpha"
            )
        )
        {
            (await managerA.StartAsync("shared-id", MinimalDefinition(), WorkflowStartMode.Sync)).Status.Should().Be("completed");
        }

        await using (
            var managerB = new WorkflowManager(
                () => ScriptedController(DriveMinimalToTerminal).Object,
                EmptyControllerOptions(),
                controllerConversationStore: store,
                launchConversationId: () => "thread-beta"
            )
        )
        {
            (await managerB.StartAsync("shared-id", MinimalDefinition(), WorkflowStartMode.Sync)).Status.Should().Be("completed");
        }

        store.GetMessageCount("workflow-shared-id-thread-alpha").Should().BeGreaterThan(0);
        store.GetMessageCount("workflow-shared-id-thread-beta").Should().BeGreaterThan(0);
        // Two physically separate threads: neither conversation's workflow agent can see the other's history.
        store
            .GetMessageCount("workflow-shared-id")
            .Should()
            .Be(0, "a shared unscoped thread is exactly the cross-conversation reuse this fix removes");
    }

    [Fact]
    public async Task StartAsync_WithoutLaunchConversation_KeepsLegacyUnscopedThread()
    {
        // Backward-compatibility: a headless host (no launching conversation) still persists under the plain
        // workflow-{id} thread, so existing resume/lookup by that id is unaffected.
        var store = new InMemoryConversationStore();
        var controller = ScriptedController(DriveMinimalToTerminal);

        await using var manager = new WorkflowManager(
            () => controller.Object,
            EmptyControllerOptions(),
            controllerConversationStore: store
        );

        var result = await manager.StartAsync("headless", MinimalDefinition(), WorkflowStartMode.Sync);
        result.Status.Should().Be("completed");

        store.GetMessageCount("workflow-headless").Should().BeGreaterThan(0);
        manager.ListRuns().Should().ContainSingle().Which.ThreadId.Should().Be("workflow-headless");
    }
}
