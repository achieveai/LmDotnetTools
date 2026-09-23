using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using LmStreaming.Sample.Tests.TestDoubles;

namespace LmStreaming.Sample.Tests.Agents;

/// <summary>
/// The published 1.0.x surface of <see cref="MultiTurnAgentPool"/> that the in-place switch must not
/// break: <c>RecreateAgentWithModeAsync</c> / <c>RecreateAgentWithProviderAsync</c> still return the
/// agent itself, and <see cref="MultiTurnAgentPool.AgentCreationContext"/> still has the positional
/// constructor and <c>Deconstruct</c> a consumer compiled against 1.0.33 binds to. The richer result
/// lives on <see cref="MultiTurnAgentPool.SwitchModeAsync"/> and
/// <see cref="MultiTurnAgentPool.SwitchProviderAsync"/>.
/// </summary>
/// <remarks>
/// Each behavioural check is paired with a reflection check on the CLR signature, because source
/// compatibility alone is not what an already-compiled package consumer needs: a method whose return
/// type changed still compiles against by name and then fails to bind at runtime.
/// </remarks>
[Collection("EnvironmentVariables")]
public class MultiTurnAgentPoolCompatibilityTests
{
    private static AgentProfile DefaultMode => SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;

    [Fact]
    public async Task RecreateAgentWithModeAsync_StillReturnsTheAgentTheSwitchLeft()
    {
        var store = new InMemoryConversationStore();
        await using var pool = CreatePool(store);
        var original = pool.GetOrCreateAgent(
            "thread-compat-mode",
            DefaultMode,
            requestedProviderId: "test",
            requestResponseDumpFileName: null
        );
        AgentProfile newMode = SystemChatModes.All.First(m => m.Id != DefaultMode.Id);

        IMultiTurnAgent returned = await pool.RecreateAgentWithModeAsync("thread-compat-mode", newMode);

        returned
            .Should()
            .BeSameAs(
                original,
                "the factory served the switch in place, and the old entry point reports the agent it left"
            );
        pool.GetAgentMode("thread-compat-mode")
            .Should()
            .BeSameAs(newMode, "it is the same switch SwitchModeAsync performs");
        typeof(MultiTurnAgentPool)
            .GetMethod(nameof(MultiTurnAgentPool.RecreateAgentWithModeAsync))!
            .ReturnType.Should()
            .Be(typeof(Task<IMultiTurnAgent>), "an already-compiled consumer binds the 1.0.33 return type");
    }

    [Fact]
    public async Task RecreateAgentWithProviderAsync_StillReturnsTheAgentTheSwitchLeft()
    {
        var store = new InMemoryConversationStore();
        await using var pool = CreatePool(store);
        var original = pool.GetOrCreateAgent(
            "thread-compat-provider",
            DefaultMode,
            requestedProviderId: "test",
            requestResponseDumpFileName: null
        );

        IMultiTurnAgent returned = await pool.RecreateAgentWithProviderAsync(
            "thread-compat-provider",
            "openai",
            DefaultMode
        );

        returned.Should().BeSameAs(original);
        pool.GetEffectiveProviderId("thread-compat-provider", null).Should().Be("openai");
        typeof(MultiTurnAgentPool)
            .GetMethod(nameof(MultiTurnAgentPool.RecreateAgentWithProviderAsync))!
            .ReturnType.Should()
            .Be(typeof(Task<IMultiTurnAgent>));
    }

    [Fact]
    public void AgentCreationContext_KeepsThePositionalConstructorAndDeconstructOf1_0_x()
    {
        // The 1.0.33 shape, with the trailing optionals omitted exactly as a consumer would.
        var context = new MultiTurnAgentPool.AgentCreationContext("thread", DefaultMode, "test", null, null);

        context.Existing.Should().BeNull("the switch context is state the old shape never carried");
        context.CallerCredential.Should().BeNull();
        context.LifecycleServices.Should().BeNull();

        Type[] published =
        [
            typeof(string),
            typeof(AgentProfile),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(SandboxCredential),
            typeof(MultiTurnLifecycleServices),
        ];
        var type = typeof(MultiTurnAgentPool.AgentCreationContext);
        type.GetConstructor(published)
            .Should()
            .NotBeNull("an assembly compiled against 1.0.33 binds this exact constructor");
        type.GetConstructors()
            .Should()
            .ContainSingle(
                "a second positional overload would make every source call that omits the optionals ambiguous"
            );
        type.GetMethod("Deconstruct")!
            .GetParameters()
            .Should()
            .HaveCount(published.Length, "positional deconstruction keeps its published arity");
    }

    /// <summary>A pool whose factory serves every switch in place, so the old entry points have a live agent to return.</summary>
    private static MultiTurnAgentPool CreatePool(InMemoryConversationStore store) =>
        new(
            context => new MultiTurnAgentPool.AgentCreationResult(
                context.Existing?.Agent ?? new FakeMultiTurnAgent(context.ThreadId)
            ),
            new FakeProviderRegistry(defaultProviderId: "test", available: ["test", "openai"]).ToReal(),
            store,
            NullLogger<MultiTurnAgentPool>.Instance
        );
}
