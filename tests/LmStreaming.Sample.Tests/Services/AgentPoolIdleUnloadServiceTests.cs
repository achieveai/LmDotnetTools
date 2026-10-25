using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmTestUtils;
using LmStreaming.Sample.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// The host side of #830: the settings that drive <see cref="MultiTurnAgentPool.UnloadIdleAgentsAsync"/>.
/// What counts as idle is the pool's call and is covered by <c>MultiTurnAgentPoolIdleUnloadTests</c>.
/// </summary>
public sealed class AgentPoolIdleUnloadServiceTests
{
    private static readonly AgentProfile Mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;

    [Fact]
    public async Task ASweep_UnloadsAConversationIdleForTheConfiguredWindow()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var pool = CreatePool(time);
        var service = CreateService(pool, ("IdleUnloadAfter", "00:02:00"));
        _ = pool.GetOrCreateAgent("thread-idle", Mode);

        time.Advance(TimeSpan.FromMinutes(1));
        await service.TrySweepAsync(CancellationToken.None);
        pool.HasAgent("thread-idle").Should().BeTrue("the configured window is two minutes, not the default");

        time.Advance(TimeSpan.FromMinutes(1));
        await service.TrySweepAsync(CancellationToken.None);
        pool.HasAgent("thread-idle").Should().BeFalse();
    }

    [Fact]
    public async Task TheRunningService_UnloadsAnIdleConversation_OnItsOwnTimer()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var pool = CreatePool(time);
        var service = CreateService(pool, ("IdleUnloadAfter", "00:02:00"), ("IdleSweepInterval", "00:00:00.020"));
        _ = pool.GetOrCreateAgent("thread-idle", Mode);
        time.Advance(TimeSpan.FromMinutes(2));

        await service.StartAsync(CancellationToken.None);
        try
        {
            await Wait.UntilAsync(() => !pool.HasAgent("thread-idle"), "a tick of the service's timer unloaded it");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AZeroWindow_TurnsUnloadingOff_AndSaysSo()
    {
        await using var pool = CreatePool(TimeProvider.System);
        var logger = new CollectingLogger<AgentPoolIdleUnloadService>();
        var service = CreateService(pool, logger, ("IdleUnloadAfter", "00:00:00"));

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

        logger
            .Entries.Should()
            .ContainSingle(e => e.Level == LogLevel.Warning && e.Message.Contains("OFF", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ANonPositiveSweepInterval_IsRefusedAtStartup()
    {
        await using var pool = CreatePool(TimeProvider.System);

        var create = () => CreateService(pool, ("IdleSweepInterval", "00:00:00"));

        create.Should().Throw<InvalidOperationException>().WithMessage("*IdleSweepInterval*");
    }

    [Fact]
    public async Task ASweepAfterThePoolIsDisposed_IsQuiet()
    {
        // The pool is disposed with the host; a tick that lands during shutdown is not a failure.
        var pool = CreatePool(TimeProvider.System);
        var logger = new CollectingLogger<AgentPoolIdleUnloadService>();
        var service = CreateService(pool, logger);
        await pool.DisposeAsync();

        await service.TrySweepAsync(CancellationToken.None);

        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
    }

    private static MultiTurnAgentPool CreatePool(TimeProvider time) =>
        new(
            (threadId, _, _) =>
                new MultiTurnAgentPool.AgentCreationResult(
                    new MultiTurnAgentLoop(new Mock<IStreamingAgent>().Object, new FunctionRegistry(), threadId)
                ),
            NullLogger<MultiTurnAgentPool>.Instance
        )
        {
            TimeProvider = time,
        };

    private static AgentPoolIdleUnloadService CreateService(
        MultiTurnAgentPool pool,
        params (string Key, string Value)[] settings
    ) => CreateService(pool, new CollectingLogger<AgentPoolIdleUnloadService>(), settings);

    private static AgentPoolIdleUnloadService CreateService(
        MultiTurnAgentPool pool,
        ILogger<AgentPoolIdleUnloadService> logger,
        params (string Key, string Value)[] settings
    ) =>
        new(
            pool,
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    settings.Select(s => new KeyValuePair<string, string?>($"LmStreaming:AgentPool:{s.Key}", s.Value))
                )
                .Build(),
            logger
        );

    private sealed class CollectingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose() { }
        }
    }
}
