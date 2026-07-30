using AchieveAi.LmDotnetTools.LmCore.Approval;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.Extensions.Logging;

namespace LmStreaming.Sample.Tests.Agents;

/// <summary>
/// How the lifecycle/approval bundle reaches — or provably fails to reach — the agents a
/// <see cref="MultiTurnAgentPool"/> creates. The context-aware constructor hands the bundle to the
/// factory; the back-compat overloads structurally cannot, so they say so out loud instead.
/// </summary>
/// <remarks>
/// The overloads take loose positional factories that predate <c>AgentCreationContext</c>, so there
/// is no seam through which a gate could be delivered. That is tolerable for observation — nothing
/// unsafe happens if events go unpublished — but approval failing open silently is not: a host that
/// configured a gate and got ungated tools would only find out from whatever the tools did. Hence a
/// construction-time warning, asserted here against the constant so its wording survives refactors
/// that log-scrapers depend on.
/// </remarks>
public class MultiTurnAgentPoolLifecycleWiringTests
{
    [Fact]
    public async Task LegacyProviderFactory_WarnsOnce_WhenApprovalIsConfigured()
    {
        var logger = new CapturingLogger();

        await using var pool = new MultiTurnAgentPool(
            (threadId, _, _, _) => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(threadId)),
            providerRegistry: null,
            conversationStore: null,
            logger,
            Gated()
        );

        logger
            .Warnings.Should()
            .ContainSingle("the host is told once, at construction — not once per agent")
            .Which.Should()
            .Be(MultiTurnAgentPool.LegacyFactoryApprovalWarning);
    }

    [Fact]
    public async Task LegacyProviderlessFactory_Warns_WhenApprovalIsConfigured()
    {
        // The oldest overload has the same hole, so it must not be the quiet way in.
        var logger = new CapturingLogger();

        await using var pool = new MultiTurnAgentPool(
            (threadId, _, _) => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(threadId)),
            logger,
            Gated()
        );

        logger.Warnings.Should().ContainSingle().Which.Should().Be(MultiTurnAgentPool.LegacyFactoryApprovalWarning);
    }

    [Fact]
    public async Task LegacyProviderFactory_StaysQuiet_ForAnObservationOnlyBundle()
    {
        // Events that never get published are a gap in telemetry, not a safety hole. Warning here
        // would train hosts to ignore the message that does matter.
        var logger = new CapturingLogger();

        await using var pool = new MultiTurnAgentPool(
            (threadId, _, _, _) => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(threadId)),
            providerRegistry: null,
            conversationStore: null,
            logger,
            MultiTurnLifecycleServices.ForObservationOnly(Gated())
        );

        logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task LegacyProviderFactory_StaysQuiet_WhenNoBundleIsWired()
    {
        var logger = new CapturingLogger();

        await using var pool = new MultiTurnAgentPool(
            (threadId, _, _, _) => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(threadId)),
            providerRegistry: null,
            conversationStore: null,
            logger
        );

        logger.Warnings.Should().BeEmpty("the overloads behaved this way long before approval existed");
    }

    [Fact]
    public async Task ContextFactory_DeliversTheBundle_AndSaysNothing()
    {
        // The positive control the warnings above are measured against: on the supported constructor
        // the same bundle arrives at the factory, so the loop it builds is genuinely gated.
        var logger = new CapturingLogger();
        var services = Gated();
        MultiTurnLifecycleServices? seen = null;

        await using var pool = new MultiTurnAgentPool(
            context =>
            {
                seen = context.LifecycleServices;
                return new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(context.ThreadId));
            },
            providerRegistry: null,
            conversationStore: null,
            logger,
            bindingSink: null,
            services
        );

        _ = pool.GetOrCreateAgent("thread-lifecycle-wiring", SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);

        seen.Should().BeSameAs(services);
        logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task ContextFactory_DeliversNullBundle_WhenTheHostWiredNone()
    {
        // Null, not MultiTurnLifecycleServices.Disabled: the pool passes on what it was given, and
        // every loop already reads null as fully disabled. Substituting a bundle here would make a
        // factory that checks for null believe observation was configured.
        MultiTurnLifecycleServices? seen = new();

        await using var pool = new MultiTurnAgentPool(
            context =>
            {
                seen = context.LifecycleServices;
                return new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(context.ThreadId));
            },
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance
        );

        _ = pool.GetOrCreateAgent("thread-lifecycle-unwired", SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);

        seen.Should().BeNull();
    }

    /// <summary>A bundle whose approval half is armed — the only half the warning is about.</summary>
    private static MultiTurnLifecycleServices Gated() =>
        new()
        {
            Approval = new ToolInvocationPreparer(new ToolApprovalOptions { RequireApproval = true }),
        };

    /// <summary>Keeps rendered warnings so a test can assert the exact wording, and the count.</summary>
    private sealed class CapturingLogger : ILogger<MultiTurnAgentPool>
    {
        private readonly List<string> _warnings = [];

        public IReadOnlyList<string> Warnings => _warnings;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (logLevel >= LogLevel.Warning)
            {
                _warnings.Add(formatter(state, exception));
            }
        }
    }
}
