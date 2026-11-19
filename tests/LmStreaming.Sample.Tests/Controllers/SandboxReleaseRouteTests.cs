using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.Services;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
/// The sandbox release route driven directly, for the states a composition host cannot hold still: a run in
/// progress, a turn that rebuilds the agent while the old one is being disposed, and a host without a sandbox
/// registry. The pool and the registry are the production types; the agent is <see cref="FakeMultiTurnAgent"/> and
/// the gateway is <see cref="OwnedSandboxGateway"/>.
/// </summary>
public sealed class SandboxReleaseRouteTests : LoggingTestBase
{
    private const string ThreadId = "thread-review-7";
    private static readonly AgentProfile WorkspaceMode = SystemChatModes.GetById(SystemChatModes.WorkspaceAgentModeId)!;

    public SandboxReleaseRouteTests(ITestOutputHelper output)
        : base(output) { }

    [Fact]
    public async Task The_release_route_refuses_while_a_run_is_in_progress_and_deletes_nothing()
    {
        var gateway = new OwnedSandboxGateway();
        await using var registry = gateway.CreateRegistry(LoggerFactory, "lm-release-route-test");
        await using var pool = ConversationsControllerTests.CreatePool();
        var store = await SeedAsync("PRs/r-7");
        var agent = (FakeMultiTurnAgent)pool.GetOrCreateAgent(ThreadId, WorkspaceMode);
        agent.StartRun("run-1");
        var session = await registry.GetOrCreateSessionAsync(OwnedRef());
        var controller = CreateController(store, pool, registry);

        var result = await controller.ReleaseSandbox(
            ThreadId,
            new ReleaseSandboxRequest { WorkingDirectoryRelPath = "PRs/r-9" },
            CancellationToken.None
        );
        Log(result, gateway);

        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        JsonSerializer
            .Serialize(conflict.Value)
            .Should()
            .Contain("sandbox_release_while_running")
            .And.Contain("work in hand", "the pool reports the run as Busy");
        pool.GetOrCreateAgent(ThreadId, WorkspaceMode).Should().BeSameAs(agent);
        registry
            .TryGetExistingSession(SandboxSessionRegistry.DefaultWorkspaceId, false, ThreadId)
            .Should()
            .BeSameAs(session);
        gateway.DeleteAttempts.Should().Be(0);
        (await ConversationWorkingDirectory.ReadAsync(store, ThreadId)).Should().Be("PRs/r-7");
    }

    [Fact]
    public async Task A_turn_that_rebuilds_the_agent_while_the_old_one_is_disposed_keeps_its_sandbox_and_the_route_answers_409()
    {
        var gateway = new OwnedSandboxGateway();
        await using var registry = gateway.CreateRegistry(LoggerFactory, "lm-release-route-test");
        await using var pool = ConversationsControllerTests.CreatePool();
        var store = await SeedAsync("PRs/r-7");
        var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = (FakeMultiTurnAgent)pool.GetOrCreateAgent(ThreadId, WorkspaceMode);
        old.DisposeGate = disposing.Task;
        var session = await registry.GetOrCreateSessionAsync(OwnedRef());
        var controller = CreateController(store, pool, registry);

        var release = controller.ReleaseSandbox(
            ThreadId,
            new ReleaseSandboxRequest { WorkingDirectoryRelPath = "PRs/r-9" },
            CancellationToken.None
        );
        await Wait.UntilAsync(() => pool.ActiveAgentCount == 0, "the route took the old agent out of the pool");
        // A send arrives during the old agent's dispose: the replacement is built, from the sandbox still cached.
        var replacement = pool.GetOrCreateAgent(ThreadId, WorkspaceMode);
        (await registry.GetOrCreateSessionAsync(OwnedRef())).Should().BeSameAs(session);
        disposing.SetResult();
        var result = await release;
        Log(result, gateway);

        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        JsonSerializer
            .Serialize(conflict.Value)
            .Should()
            .Contain("sandbox_release_while_running")
            .And.Contain("a new turn took the conversation", "the old agent was idle; the replacement is what refuses");
        replacement.Should().NotBeSameAs(old);
        pool.GetOrCreateAgent(ThreadId, WorkspaceMode).Should().BeSameAs(replacement);
        gateway.DeleteAttempts.Should().Be(0, "the replacement's sandbox must not be deleted under it");
        registry
            .TryGetExistingSession(SandboxSessionRegistry.DefaultWorkspaceId, false, ThreadId)
            .Should()
            .BeSameAs(session);
        (await ConversationWorkingDirectory.ReadAsync(store, ThreadId))
            .Should()
            .Be("PRs/r-7", "the replacement was built for this directory");
    }

    [Fact]
    public async Task The_release_route_on_a_host_without_a_sandbox_registry_answers_400_and_changes_nothing()
    {
        await using var pool = ConversationsControllerTests.CreatePool();
        var store = await SeedAsync("PRs/r-7");
        var before = await store.LoadMetadataAsync(ThreadId);
        var agent = pool.GetOrCreateAgent(ThreadId, WorkspaceMode);
        var controller = CreateController(store, pool, registry: null);

        var result = await controller.ReleaseSandbox(
            ThreadId,
            new ReleaseSandboxRequest { WorkingDirectoryRelPath = "PRs/r-9" },
            CancellationToken.None
        );
        Logger.LogInformation("Release without a registry answered {Result}", Describe(result));

        var refusal = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var body = JsonSerializer.Serialize(refusal.Value);
        body.Should().Contain("dedicated_sandbox_required").And.Contain("no sandbox registry");
        (await store.LoadMetadataAsync(ThreadId)).Should().BeEquivalentTo(before);
        pool.GetOrCreateAgent(ThreadId, WorkspaceMode).Should().BeSameAs(agent);
    }

    private static WorkspaceRef OwnedRef() =>
        new(SandboxSessionRegistry.DefaultWorkspaceId, WorkingDir: "PRs/r-7", OwnerThreadId: ThreadId);

    private async Task<InMemoryConversationStore> SeedAsync(string directory)
    {
        var store = new InMemoryConversationStore();
        await store.UpdateMetadataAsync(
            ThreadId,
            _ => new ThreadMetadata
            {
                ThreadId = ThreadId,
                LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Properties = ImmutableDictionary<string, object>
                    .Empty.Add(MultiTurnAgentPool.ModePropertyKey, SystemChatModes.WorkspaceAgentModeId)
                    .Add(ConversationWorkingDirectory.PropertyKey, directory)
                    .Add(ConversationDedicatedSandbox.PropertyKey, true),
            }
        );
        Logger.LogInformation("Seeded dedicated thread {ThreadId} in {Directory}", ThreadId, directory);
        return store;
    }

    private static ConversationsController CreateController(
        IConversationStore store,
        MultiTurnAgentPool pool,
        SandboxSessionRegistry? registry
    ) =>
        ConversationsControllerTests.CreateController(
            store,
            pool,
            ConversationsControllerTests.ModeStoreResolvingSystemModes(),
            sandboxSessionRegistry: registry
        );

    private void Log(IActionResult result, OwnedSandboxGateway gateway) =>
        Logger.LogInformation(
            "Release answered {Result}; gateway saw {Creates} create(s), {DeleteAttempts} delete attempt(s)",
            Describe(result),
            gateway.Creates.Count,
            gateway.DeleteAttempts
        );

    private static string Describe(IActionResult result) =>
        result is ObjectResult objectResult
            ? $"{objectResult.StatusCode} {JsonSerializer.Serialize(objectResult.Value)}"
            : result.GetType().Name;
}
