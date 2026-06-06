using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using LmStreaming.Sample.Services.Discovery;
using Microsoft.Extensions.Logging;

namespace LmStreaming.Sample.Tests.Services.Discovery;

/// <summary>
/// Pins the built-in-wins merge contract that gates the sub-agent catalog from being shadowed
/// by workspace-discovered markdown. A one-line flip to <c>discovered</c>-wins is exactly the
/// kind of trust-boundary regression this test exists to catch.
/// </summary>
public class SubAgentMergeTests
{
    private static readonly Mock<IStreamingAgent> AgentStub = new();
    private static readonly Func<IStreamingAgent> AgentFactory = () => AgentStub.Object;

    private static SubAgentTemplate Make(string name, string systemPrompt) => new()
    {
        Name = name,
        Description = name,
        WhenToUse = name,
        SystemPrompt = systemPrompt,
        AgentFactory = AgentFactory,
        MaxTurnsPerRun = WorkspaceSubAgentLoader.DefaultMaxTurnsPerRun,
    };

    [Fact]
    public void MergeBuiltInWins_DiscoveredWithoutCollision_AddsToBuiltIns()
    {
        var builtIns = new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal)
        {
            ["general-purpose"] = Make("built-in", "BUILT-IN"),
        };
        var discovered = new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal)
        {
            ["echo"] = Make("echo", "DISCOVERED"),
        };

        WorkspaceSubAgentLoader.MergeBuiltInWins(
            builtIns,
            discovered,
            NullLogger<SubAgentMergeTests>.Instance);

        builtIns.Should().ContainKey("echo");
        builtIns["echo"].SystemPrompt.Should().Be("DISCOVERED");
        builtIns["general-purpose"].SystemPrompt.Should().Be("BUILT-IN");
    }

    [Fact]
    public void MergeBuiltInWins_CollisionWithBuiltIn_BuiltInIsKept()
    {
        // Trust-boundary guard: an untrusted workspace markdown file MUST NOT shadow the
        // hardcoded built-in template under the same key. If this test fails, the merge
        // direction has been inverted somewhere.
        var builtIns = new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal)
        {
            ["general-purpose"] = Make("built-in", "BUILT-IN"),
        };
        var discovered = new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal)
        {
            ["general-purpose"] = Make("general-purpose", "SHOULD-NOT-WIN"),
        };

        WorkspaceSubAgentLoader.MergeBuiltInWins(
            builtIns,
            discovered,
            NullLogger<SubAgentMergeTests>.Instance);

        builtIns["general-purpose"].SystemPrompt.Should().Be("BUILT-IN");
    }

    [Fact]
    public void MergeBuiltInWins_CollisionWithBuiltIn_LogsWarning()
    {
        var builtIns = new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal)
        {
            ["general-purpose"] = Make("built-in", "BUILT-IN"),
        };
        var discovered = new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal)
        {
            ["general-purpose"] = Make("general-purpose", "SHOULD-NOT-WIN"),
        };
        var logger = new CollectingLogger();

        WorkspaceSubAgentLoader.MergeBuiltInWins(builtIns, discovered, logger);

        logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
        logger.Entries[0].Message.Should().Contain("collides with a built-in template");
    }

    [Fact]
    public void MergeBuiltInWins_EmptyDiscovered_NoOp()
    {
        var builtIns = new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal)
        {
            ["general-purpose"] = Make("built-in", "BUILT-IN"),
        };

        WorkspaceSubAgentLoader.MergeBuiltInWins(
            builtIns,
            new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal),
            NullLogger<SubAgentMergeTests>.Instance);

        builtIns.Should().HaveCount(1);
    }

    private sealed class CollectingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
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
