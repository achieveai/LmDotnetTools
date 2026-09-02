using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.SubAgents;

/// <summary>
/// EUII / privacy guard for WI #194 (PR #209 review). The interactive focus feature relays a user's
/// typed prompt through <see cref="SubAgentManager.SendMessageAsync(string, string, bool, CancellationToken)"/>,
/// and a background spawn carries
/// the task text. Neither the spawn task nor the relayed prompt may appear in <b>any</b> log the
/// manager emits — only content-free metadata (ids, lengths, categories). This captures every log
/// level (not just the WebSocket-manager logger the transport-level test observes) so a downstream
/// leak in the core manager is caught.
/// </summary>
public class SubAgentManagerEuiiLoggingTests : IAsyncLifetime
{
    private const string SecretTask = "TASK-SENTINEL-c0ffee-do-not-log-this-task";
    private const string SecretPrompt = "PROMPT-SENTINEL-deadbeef-do-not-log-this-prompt";
    private const string SecretRole = "ROLE-SENTINEL-a11ce-do-not-log-this-role";
    private const string SecretDescription = "DESC-SENTINEL-b0b-do-not-log-this-description";
    private const string SecretBody = "BODY-SENTINEL-facade-do-not-log-this-message-body";
    private const string SecretTranscript = "TRANSCRIPT-SENTINEL-decade-do-not-log-this-reply";

    private readonly Mock<IMultiTurnAgent> _parentMock = new();
    private readonly CaptureAllLogger _logger = new();
    private SubAgentManager? _manager;

    public Task InitializeAsync()
    {
        _parentMock
            .Setup(p =>
                p.SendAsync(
                    It.IsAny<List<IMessage>>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new SendReceipt("receipt-1", null, DateTimeOffset.UtcNow));

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_manager != null)
        {
            // Bounded: an unbounded teardown turns one stalled test into an aborted run (#362).
            await Wait.ForTeardownAsync(_manager, "the sub-agent manager under test");
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
            logger: _logger
        );
        _manager = manager;

        manager.TestAgentFactoryOverride = (agentId, _) =>
            new ObservableFakeAgent
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
        allLogs
            .Should()
            .NotContain(
                line => line.Contains(SecretTask, StringComparison.Ordinal),
                "the spawn task is user/model content and must never be logged"
            );
        allLogs
            .Should()
            .NotContain(
                line => line.Contains(SecretPrompt, StringComparison.Ordinal),
                "the relayed prompt is user EUII and must never be logged"
            );
    }

    [Fact]
    public async Task Spawn_LogsFinalEffectiveRoutingAsStructuredProperties()
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
                    DefaultOptions = new GenerateReplyOptions { ModelId = "tier-five-model" },
                    IsModelTierResolved = true,
                    ModelIntelligence = 5,
                    Effort = ReasoningEffort.Xhigh,
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
            logger: _logger
        );
        _manager = manager;
        manager.TestAgentFactoryOverride = (agentId, _) =>
            new ObservableFakeAgent
            {
                ThreadId = $"subagent-{agentId}",
                RunMessages = [new TextMessage { Text = "ack", Role = Role.Assistant }],
            };

        _ = await manager.SpawnAsync("worker", SecretTask, name: "workflow:1:task", runInBackground: true);

        var routing = _logger
            .StructuredSnapshot()
            .Single(entry =>
                entry.TryGetValue("RoutingSelectionSource", out var source)
                && string.Equals(source?.ToString(), "template-tier", StringComparison.Ordinal)
            );
        routing["TemplateName"].Should().Be("worker");
        routing["SpawnName"].Should().Be("workflow:1:task");
        routing["RequestedModelIntelligence"].Should().BeNull();
        routing["TemplateModelIntelligence"].Should().Be(5);
        routing["EffectiveModelId"].Should().Be("tier-five-model");
        routing["EffectiveModelIntelligence"].Should().Be(5);
        routing["RequestedReasoningEffort"].Should().Be("xhigh");
        routing["ShapedReasoningEffort"].Should().BeNull("the test factory does not shape provider metadata");
    }

    /// <summary>
    /// The collaboration surface widened what a manager handles: an agent now carries a role and a
    /// description, and agents write typed messages to each other. All four are content, and ADR 0009
    /// says logs carry identifiers, types, outcomes and lengths only — so none of them may appear.
    /// </summary>
    /// <remarks>
    /// Role and description are the easiest to leak by accident precisely because they look like
    /// metadata: they are short, they are chosen by a model rather than typed by the user, and they are
    /// exactly the sort of field a debugging log line reaches for when describing "which agent". They
    /// are still author-supplied free text and can restate the task.
    ///
    /// Both the rendered line and the structured property values are checked. Rendering alone would
    /// miss a value attached to a template placeholder that a formatter drops, and structure alone
    /// would miss content interpolated into the template itself.
    /// </remarks>
    [Fact]
    public async Task Collaboration_NeverLogsRoleDescriptionMessageBodyOrTranscript()
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

        var root = AgentCollaborationSetup.CreateRoot(new AgentCollaborationOptions());
        _ = root.Directory.TryRegister(root.Context, root.Name, AgentCollaborationStatuses.Running);

        var source = new MutableSubAgentTemplateSource(options.Templates);
        var manager = new SubAgentManager(
            parentAgent: _parentMock.Object,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: options,
            source: source,
            logger: _logger,
            collaboration: root
        );
        _manager = manager;

        // The reply is the sub-agent's transcript: it is model output relayed to the parent, and the
        // manager sees all of it on the way through.
        manager.TestAgentFactoryOverride = (agentId, _) =>
            new ObservableFakeAgent
            {
                ThreadId = $"subagent-{agentId}",
                RunMessages = [new TextMessage { Text = SecretTranscript, Role = Role.Assistant }],
            };

        var spawnJson = await manager.SpawnAsync(
            "worker",
            SecretTask,
            name: "helper",
            runInBackground: true,
            role: SecretRole,
            description: SecretDescription
        );
        var agentId = ParseAgentId(spawnJson);

        // The guard is only worth anything if the role and description actually reached the system, so
        // the directory is asked to confirm it is holding the very values the log must not show.
        var entry = root.Directory.FindById(agentId)!;
        entry.Role.Should().Be(SecretRole);
        entry.Description.Should().Be(SecretDescription);

        // A typed agent-to-agent message routes through the child's write endpoint, so this exercises
        // the manager's own delivery path rather than a directory lookup.
        var send = new SubAgentToolProvider(manager, source)
            .GetFunctions()
            .First(f => f.Contract.Name == "SendMessage");
        var delivery = await send.Handler(
            JsonSerializer.Serialize(
                new
                {
                    target = agentId,
                    content = SecretBody,
                    msg_type = "steer",
                }
            ),
            new ToolCallContext(),
            CancellationToken.None
        );
        delivery
            .Should()
            .BeOfType<ToolHandlerResult.Resolved>()
            .Which.Payload.IsError.Should()
            .BeFalse("the message must really be delivered");

        AssertNoSentinelWasLogged();
    }

    /// <summary>
    /// Fails with the offending line when any sentinel reached a rendered log line or a structured
    /// property value.
    /// </summary>
    private void AssertNoSentinelWasLogged()
    {
        string[] sentinels = [SecretTask, SecretPrompt, SecretRole, SecretDescription, SecretBody, SecretTranscript];

        var lines = _logger.Snapshot();
        lines.Should().NotBeEmpty("the manager logs lifecycle events (so the guard is meaningful)");

        var values = _logger
            .StructuredSnapshot()
            .SelectMany(entry => entry.Values)
            .Select(value => value?.ToString() ?? string.Empty)
            .ToList();

        foreach (var sentinel in sentinels)
        {
            lines
                .Should()
                .NotContain(
                    line => line.Contains(sentinel, StringComparison.Ordinal),
                    "content must never be rendered into a log line (sentinel {0})",
                    sentinel
                );
            values
                .Should()
                .NotContain(
                    value => value.Contains(sentinel, StringComparison.Ordinal),
                    "content must never be attached as a log property (sentinel {0})",
                    sentinel
                );
        }
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

        private readonly List<IReadOnlyDictionary<string, object?>> _structured = [];

        public IReadOnlyList<string> Snapshot()
        {
            lock (_lock)
            {
                return [.. _lines];
            }
        }

        public IReadOnlyList<IReadOnlyDictionary<string, object?>> StructuredSnapshot()
        {
            lock (_lock)
            {
                return [.. _structured];
            }
        }

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
            var line = formatter(state, exception);
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                : [];
            lock (_lock)
            {
                _lines.Add(line);
                _structured.Add(properties);
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose() { }
        }
    }
}
