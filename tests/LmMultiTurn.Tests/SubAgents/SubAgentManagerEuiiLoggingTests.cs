using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.SubAgents;

/// <summary>
/// EUII / privacy guard for WI #194 (PR #209 review). The interactive focus feature relays a user's
/// typed prompt through <see cref="SubAgentManager.SendMessageAsync"/>, and a background spawn carries
/// the task text. Neither the spawn task nor the relayed prompt may appear in <b>any</b> log the
/// manager emits — only content-free metadata (ids, lengths, categories). This captures every log
/// level (not just the WebSocket-manager logger the transport-level test observes) so a downstream
/// leak in the core manager is caught.
/// </summary>
public class SubAgentManagerEuiiLoggingTests : IAsyncLifetime
{
    private const string SecretTask = "TASK-SENTINEL-c0ffee-do-not-log-this-task";
    private const string SecretPrompt = "PROMPT-SENTINEL-deadbeef-do-not-log-this-prompt";

    private readonly Mock<IMultiTurnAgent> _parentMock = new();
    private readonly CaptureAllLogger _logger = new();
    private SubAgentManager? _manager;

    public Task InitializeAsync()
    {
        _parentMock
            .Setup(p => p.SendAsync(
                It.IsAny<List<IMessage>>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendReceipt("receipt-1", null, DateTimeOffset.UtcNow));

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_manager != null)
        {
            await _manager.DisposeAsync();
        }
    }

    [Fact]
    public async Task SpawnAndSend_NeverLogTheTaskOrPromptContent()
    {
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["worker"] = new SubAgentTemplate
                {
                    Name = "worker",
                    SystemPrompt = "You are a worker.",
                    AgentFactory = () => throw new NotSupportedException("Bypassed by TestAgentFactoryOverride."),
                },
            },
            MaxConcurrentSubAgents = 5,
        };

        var manager = new SubAgentManager(
            parentAgent: _parentMock.Object,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: options,
            source: new MutableSubAgentTemplateSource(options.Templates),
            logger: _logger);
        _manager = manager;

        manager.TestAgentFactoryOverride = (agentId, _) => new ObservableFakeAgent
        {
            ThreadId = $"subagent-{agentId}",
            RunMessages = [new TextMessage { Text = "ack", Role = Role.Assistant }],
        };

        // Background spawn carries the (secret) task text; the relayed follow-up carries the (secret) prompt.
        var spawnJson = await manager.SpawnAsync("worker", SecretTask, runInBackground: true);
        var agentId = ParseAgentId(spawnJson);
        _ = await manager.SendMessageAsync(agentId, SecretPrompt, runInBackground: true);

        var allLogs = _logger.Snapshot();
        allLogs.Should().NotBeEmpty("the manager logs lifecycle events (so the guard is meaningful)");
        allLogs.Should().NotContain(
            line => line.Contains(SecretTask, StringComparison.Ordinal),
            "the spawn task is user/model content and must never be logged");
        allLogs.Should().NotContain(
            line => line.Contains(SecretPrompt, StringComparison.Ordinal),
            "the relayed prompt is user EUII and must never be logged");
    }

    private static string ParseAgentId(string spawnJson)
    {
        using var doc = JsonDocument.Parse(spawnJson);
        return doc.RootElement.GetProperty("agent_id").GetString()!;
    }

    private sealed class CaptureAllLogger : ILogger
    {
        private readonly List<string> _lines = [];
        private readonly Lock _lock = new();

        public IReadOnlyList<string> Snapshot()
        {
            lock (_lock)
            {
                return [.. _lines];
            }
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var line = formatter(state, exception);
            lock (_lock)
            {
                _lines.Add(line);
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
