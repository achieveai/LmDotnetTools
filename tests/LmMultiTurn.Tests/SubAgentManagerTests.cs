using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.ClientTools;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Unit tests for SubAgentManager lifecycle operations: synchronous and background
/// spawning, peeking, completion relay, continuation via SendMessageAsync,
/// concurrency enforcement, and disposal.
/// </summary>
public class SubAgentManagerTests : IAsyncLifetime
{
    private readonly Mock<IMultiTurnAgent> _parentMock = new();
    private readonly Mock<IStreamingAgent> _subAgentMock = new();
    private SubAgentManager? _manager;

    public Task InitializeAsync()
    {
        // Default parent mock: accept any SendAsync call
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
    public async Task SpawnAsync_Synchronous_ReturnsFinalTextWithoutParentRelay()
    {
        // Arrange: sub-agent returns a single text response then the run completes
        SetupSubAgentResponse([
            new TextMessage { Text = "Sub-agent result", Role = Role.Assistant },
        ]);

        _manager = CreateManager();

        // Act: synchronous spawn (default) blocks and returns the final answer directly
        var result = await _manager.SpawnAsync("test-agent", "Do some work");

        // Assert: the tool result is the sub-agent's final text, not a JSON receipt
        result.Should().Be("Sub-agent result");

        // The synchronous path must NOT relay the result to the parent — the result
        // flows back only as this tool result, in the same parent turn.
        _parentMock.Verify(
            p => p.SendAsync(
                It.IsAny<List<IMessage>>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SpawnAsync_Synchronous_Error_ThrowsAndDoesNotRelayToParent()
    {
        // Arrange: sub-agent throws -> MultiTurnAgentLoop completes the run with IsError=true
        _subAgentMock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("API call failed"));

        _manager = CreateManager();

        // Act: synchronous spawn surfaces the failure as a typed exception
        var act = () => _manager.SpawnAsync("test-agent", "error-prone task");

        // Assert
        await act.Should().ThrowAsync<SubAgentExecutionException>()
            .WithMessage("*test-agent*failed*");

        // No parent relay on the synchronous path.
        _parentMock.Verify(
            p => p.SendAsync(
                It.IsAny<List<IMessage>>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SpawnAsync_Background_ReturnsSpawnReceipt()
    {
        // Arrange
        SetupSubAgentResponse([
            new TextMessage { Text = "Sub-agent result", Role = Role.Assistant },
        ]);

        _manager = CreateManager(maxConcurrent: 5);

        // Act: background spawn returns immediately with a JSON receipt
        var resultJson = await _manager.SpawnAsync(
            "test-agent", "Do some work", runInBackground: true);

        // Assert
        using var doc = JsonDocument.Parse(resultJson);
        var root = doc.RootElement;

        root.GetProperty("agent_id").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("template").GetString().Should().Be("test-agent");
        root.GetProperty("status").GetString().Should().Be("spawned");
    }

    [Fact]
    public async Task SpawnAsync_ThrowsOnUnknownTemplate()
    {
        // Arrange
        _manager = CreateManager();

        // Act
        var act = () => _manager.SpawnAsync("non-existent-template", "task");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Unknown template*non-existent-template*");
    }

    // --- subagent_type resolution (plugin-prefix tolerance) -------------------------------------
    // Authored workflows and controller LLMs routinely reference an agent by a bare or mis-prefixed
    // name (e.g. 'logging-review' or 'code-reviewer:logging-review' when the registered key is
    // 'debugging:logging-review'). TryResolveTemplateName recovers the intended template so the run
    // does not silently collapse to general-purpose. These tests pin every resolution branch.

    [Theory]
    [InlineData("debugging:logging-review")] // exact
    [InlineData("Debugging:Logging-Review")] // case-insensitive exact
    [InlineData("logging-review")]           // bare skill segment
    [InlineData("code-reviewer:logging-review")] // wrong plugin prefix, right segment
    public void TryResolveTemplateName_ResolvesToQualifiedKey(string requested)
    {
        var templates = new Dictionary<string, SubAgentTemplate>
        {
            ["debugging:logging-review"] = MakeTemplate(),
            ["general-purpose"] = MakeTemplate(),
        };

        var ok = SubAgentManager.TryResolveTemplateName(
            requested, templates, out var resolved, out var suggestions);

        ok.Should().BeTrue();
        resolved.Should().Be("debugging:logging-review");
        suggestions.Should().BeEmpty();
    }

    [Fact]
    public void TryResolveTemplateName_ExactMatchWinsOverSegment()
    {
        // A key that IS an exact (ordinal) match must never be re-routed by segment logic,
        // even when another key shares its trailing skill segment.
        var templates = new Dictionary<string, SubAgentTemplate>
        {
            ["review"] = MakeTemplate(),
            ["code-reviewer:review"] = MakeTemplate(),
        };

        var ok = SubAgentManager.TryResolveTemplateName(
            "review", templates, out var resolved, out var suggestions);

        ok.Should().BeTrue();
        resolved.Should().Be("review");
        suggestions.Should().BeEmpty();
    }

    [Fact]
    public void TryResolveTemplateName_AmbiguousSegment_ReturnsSuggestions()
    {
        // Two agents share the 'logging-review' segment under different plugins: the request is
        // genuinely ambiguous, so it must NOT auto-resolve; both candidates come back as suggestions.
        var templates = new Dictionary<string, SubAgentTemplate>
        {
            ["debugging:logging-review"] = MakeTemplate(),
            ["code-reviewer:logging-review"] = MakeTemplate(),
        };

        var ok = SubAgentManager.TryResolveTemplateName(
            "logging-review", templates, out var resolved, out var suggestions);

        ok.Should().BeFalse();
        resolved.Should().BeEmpty();
        suggestions.Should().BeEquivalentTo(
            ["debugging:logging-review", "code-reviewer:logging-review"]);
    }

    [Fact]
    public void TryResolveTemplateName_Unknown_ReturnsFalseWithNoSuggestions()
    {
        var templates = new Dictionary<string, SubAgentTemplate>
        {
            ["debugging:logging-review"] = MakeTemplate(),
        };

        var ok = SubAgentManager.TryResolveTemplateName(
            "totally-unrelated", templates, out var resolved, out var suggestions);

        ok.Should().BeFalse();
        resolved.Should().BeEmpty();
        suggestions.Should().BeEmpty();
    }

    [Fact]
    public async Task SpawnAsync_ResolvesBareName_AndRuns()
    {
        // End-to-end: a bare 'test-agent'-segment name registered under a plugin prefix must spawn.
        SetupSubAgentResponse([
            new TextMessage { Text = "resolved result", Role = Role.Assistant },
        ]);
        _manager = CreateManager(qualifiedTemplateKey: "debugging:test-agent");

        var result = await _manager.SpawnAsync("test-agent", "Do some work");

        result.Should().Be("resolved result");
    }

    [Fact]
    public async Task SpawnAsync_AmbiguousName_ThrowsWithSuggestions()
    {
        // When the requested segment matches several registered agents, SpawnAsync throws an
        // actionable message listing the candidates so the controller can re-issue an exact name.
        _manager = CreateManagerWithTemplates(
            "debugging:logging-review", "code-reviewer:logging-review");

        var act = () => _manager.SpawnAsync("logging-review", "task");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Ambiguous subagent_type*logging-review*"
                + "code-reviewer:logging-review*debugging:logging-review*");
    }

    [Fact]
    public async Task SpawnAsync_PoolFull_QueuesSpawnInsteadOfThrowing()
    {
        // Defer-queue cap behaviour: when the pool is saturated, a further spawn is ACCEPTED and
        // enqueued (status="queued") rather than rejected with "Max concurrent sub-agents reached".
        // The queued spawn still gets a stable agent_id handle immediately.
        var blockingTcs = new TaskCompletionSource<bool>();
        SetupBlockingSubAgent(blockingTcs);

        _manager = CreateManager(maxConcurrent: 1);

        // First background spawn takes the only permit and blocks (stays Running).
        var firstJson = await _manager.SpawnAsync("test-agent", "first task", runInBackground: true);
        using (var firstDoc = JsonDocument.Parse(firstJson))
        {
            firstDoc.RootElement.GetProperty("status").GetString().Should().Be("spawned");
        }

        // Second background spawn finds the pool full -> queued (no throw).
        var secondJson = await _manager.SpawnAsync(
            "test-agent", "second task", runInBackground: true);

        using var secondDoc = JsonDocument.Parse(secondJson);
        secondDoc.RootElement.GetProperty("status").GetString().Should().Be("queued");
        secondDoc.RootElement.GetProperty("agent_id").GetString().Should().NotBeNullOrEmpty();
        secondDoc.RootElement.GetProperty("template").GetString().Should().Be("test-agent");

        // Cleanup: unblock so dispose doesn't hang
        blockingTcs.SetResult(true);
    }

    [Fact]
    public async Task SpawnAsync_QueuedHandleIsImmediatelyObservable()
    {
        var release = new TaskCompletionSource<bool>();
        SetupBlockingSubAgent(release);
        _manager = CreateManager(maxConcurrent: 1);

        _ = await _manager.SpawnAsync("test-agent", "first", runInBackground: true);
        var queuedJson = await _manager.SpawnAsync(
            "test-agent", "second", name: "queued-worker", runInBackground: true);
        using var queuedDoc = JsonDocument.Parse(queuedJson);
        var queuedId = queuedDoc.RootElement.GetProperty("agent_id").GetString()!;

        _manager.TryPeek(queuedId, out var peek).Should().BeTrue();
        JsonDocument.Parse(peek).RootElement.GetProperty("status").GetString().Should().Be("queued");
        _manager.KnownAgentIds().Should().Contain(queuedId);
        var observed = _manager.CheckAgents([queuedId, "queued-worker"]);
        observed.Entries.Should().OnlyContain(x => x.Status == "queued" && x.AgentId == queuedId);
        _manager.ListAgents().Should().Contain(x => x.AgentId == queuedId && x.Status == SubAgentStatus.Queued);

        release.SetResult(true);
    }

    [Fact]
    public async Task SpawnAsync_CanceledForegroundQueueEntryNeverStarts()
    {
        var release = new TaskCompletionSource<bool>();
        SetupBlockingSubAgent(release);
        _manager = CreateManager(maxConcurrent: 1);
        _ = await _manager.SpawnAsync("test-agent", "first", runInBackground: true);
        using var cts = new CancellationTokenSource();

        var queued = _manager.SpawnAsync("test-agent", "must-not-run", ct: cts.Token);
        cts.Cancel();
        var act = async () => await queued;
        await act.Should().ThrowAsync<OperationCanceledException>();
        release.SetResult(true);
        await Task.Delay(150);

        _subAgentMock.Verify(
            a => a.GenerateReplyStreamingAsync(
                It.Is<IEnumerable<IMessage>>(messages =>
                    messages.OfType<TextMessage>().Any(m => m.Text == "must-not-run")),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SpawnAsync_RejectsWhenBoundedQueueIsFull()
    {
        var release = new TaskCompletionSource<bool>();
        SetupBlockingSubAgent(release);
        var options = CreateOptions(maxConcurrent: 1) with { MaxQueuedSubAgents = 1 };
        _manager = new SubAgentManager(
            _parentMock.Object,
            [],
            new Dictionary<string, ToolHandler>(),
            options,
            new MutableSubAgentTemplateSource(options.Templates));

        _ = await _manager.SpawnAsync("test-agent", "first", runInBackground: true);
        _ = await _manager.SpawnAsync("test-agent", "queued", runInBackground: true);
        var act = () => _manager.SpawnAsync("test-agent", "overflow", runInBackground: true);

        await act.Should().ThrowAsync<SubAgentQueueFullException>();
        release.SetResult(true);
    }

    [Fact]
    public async Task SendMessageAsync_QueuedTargetReturnsActionableError()
    {
        var release = new TaskCompletionSource<bool>();
        SetupBlockingSubAgent(release);
        _manager = CreateManager(maxConcurrent: 1);
        _ = await _manager.SpawnAsync("test-agent", "first", runInBackground: true);
        var queuedJson = await _manager.SpawnAsync(
            "test-agent", "queued", name: "queued-worker", runInBackground: true);
        using var queuedDoc = JsonDocument.Parse(queuedJson);
        var queuedId = queuedDoc.RootElement.GetProperty("agent_id").GetString()!;

        var byId = () => _manager.SendMessageAsync(queuedId, "follow up");
        var byName = () => _manager.SendMessageAsync("queued-worker", "follow up");

        await byId.Should().ThrowAsync<InvalidOperationException>().WithMessage("*queued*CheckAgent*");
        await byName.Should().ThrowAsync<InvalidOperationException>().WithMessage("*queued*CheckAgent*");
        release.SetResult(true);
    }

    [Fact]
    public async Task SpawnAsync_RejectsAfterDisposalBegins()
    {
        _manager = CreateManager();
        await _manager.DisposeAsync();

        var act = () => _manager.SpawnAsync("test-agent", "late", runInBackground: true);

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task SpawnAsync_QueuedSpawn_StartsAndCompletesOncePermitFrees()
    {
        // RED->GREEN for defer-queue end-to-end: with a pool of 1, a second background spawn is
        // queued while the first holds the only permit. Once the first finishes and frees its slot,
        // the background pump starts the queued spawn and it runs to completion. Before the fix the
        // second spawn threw "Max concurrent reached" and never ran.
        var release = new TaskCompletionSource<bool>();
        SetupBlockingSubAgent(release);

        _manager = CreateManager(maxConcurrent: 1);

        var firstJson = await _manager.SpawnAsync("test-agent", "first task", runInBackground: true);
        using var firstDoc = JsonDocument.Parse(firstJson);
        var firstId = firstDoc.RootElement.GetProperty("agent_id").GetString()!;

        var secondJson = await _manager.SpawnAsync(
            "test-agent", "second task", runInBackground: true);
        using var secondDoc = JsonDocument.Parse(secondJson);
        secondDoc.RootElement.GetProperty("status").GetString().Should().Be("queued");
        var secondId = secondDoc.RootElement.GetProperty("agent_id").GetString()!;

        // Release the first agent so its permit frees and the pump can start the queued one.
        release.SetResult(true);

        // Both agents must reach 'completed' — the queued one only starts after a permit frees.
        await WaitForConditionAsync(
            () =>
            {
                try
                {
                    return _manager!.Peek(firstId).Contains("\"completed\"")
                        && _manager!.Peek(secondId).Contains("\"completed\"");
                }
                catch
                {
                    return false;
                }
            },
            TimeSpan.FromSeconds(15));

        using var firstPeek = JsonDocument.Parse(_manager.Peek(firstId));
        firstPeek.RootElement.GetProperty("status").GetString()
            .Should().Be("completed", "the first agent finishes once released");

        using var secondPeek = JsonDocument.Parse(_manager.Peek(secondId));
        secondPeek.RootElement.GetProperty("status").GetString()
            .Should().Be("completed", "the queued agent runs after the first frees the permit");
    }

    [Fact]
    public void Peek_ThrowsOnUnknownAgentId()
    {
        // Arrange
        _manager = CreateManager();

        // Act
        var act = () => _manager.Peek("non-existent-id");

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Unknown agent ID*non-existent-id*");
    }

    [Fact]
    public async Task Peek_ReturnsStatusAndTurns()
    {
        // Arrange: sub-agent returns a text response
        SetupSubAgentResponse([
            new TextMessage { Text = "Working on it...", Role = Role.Assistant },
        ]);

        _manager = CreateManager();
        var resultJson = await _manager.SpawnAsync(
            "test-agent", "Do analysis", runInBackground: true);

        using var spawnDoc = JsonDocument.Parse(resultJson);
        var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        // Poll until monitoring task has processed messages
        await WaitForConditionAsync(
            () =>
            {
                try
                {
                    var json = _manager!.Peek(agentId);
                    return json.Contains("\"status\"");
                }
                catch
                {
                    return false;
                }
            },
            TimeSpan.FromSeconds(10));

        // Act
        var peekJson = _manager.Peek(agentId);

        // Assert
        using var peekDoc = JsonDocument.Parse(peekJson);
        var peekRoot = peekDoc.RootElement;

        peekRoot.GetProperty("agent_id").GetString().Should().Be(agentId);
        peekRoot.GetProperty("template").GetString().Should().Be("test-agent");
        peekRoot.GetProperty("task").GetString().Should().Be("Do analysis");
        peekRoot.TryGetProperty("status", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Completion_Background_SendsWrappedResultToParent()
    {
        // Arrange: sub-agent returns a text response then the run completes
        SetupSubAgentResponse([
            new TextMessage { Text = "Analysis complete: found 3 issues", Role = Role.Assistant },
        ]);

        _manager = CreateManager();
        await _manager.SpawnAsync(
            "test-agent", "Analyze the codebase", runInBackground: true);

        // Poll until the sub-agent completion is relayed to parent
        var parentCalled = false;
        await WaitForConditionAsync(
            () =>
            {
                try
                {
                    _parentMock.Verify(
                        p => p.SendAsync(
                            It.Is<List<IMessage>>(msgs =>
                                msgs.Count == 1
                                && ContainsSubAgentResult(msgs[0], "test-agent", "Analysis complete: found 3 issues")),
                            It.IsAny<string?>(),
                            It.IsAny<string?>(),
                            It.IsAny<CancellationToken>()),
                        Times.AtLeastOnce);
                    parentCalled = true;
                    return true;
                }
                catch
                {
                    return false;
                }
            },
            TimeSpan.FromSeconds(10));

        parentCalled.Should().BeTrue("parent should have received the sub-agent result");

        // Assert: parent's SendAsync was called with the wrapped sub-agent result
        _parentMock.Verify(
            p => p.SendAsync(
                It.Is<List<IMessage>>(msgs =>
                    msgs.Count == 1
                    && ContainsSubAgentResult(msgs[0], "test-agent", "Analysis complete: found 3 issues")),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task Completion_Background_WhenParkedOnAskUserQuestion_DoesNotClaimCompleted()
    {
        // Arrange: sub-agent defers on AskUserQuestion — which every MultiTurnAgentLoop registers
        // unconditionally, including the child loop this manager builds for "test-agent" — instead of
        // returning a final answer. The run still terminates (HasPendingMessages == false: nothing is
        // queued, the loop is simply waiting on an external resolution that nothing here can provide),
        // so HandleRunCompletionAsync takes the same "terminal" branch a genuinely finished run would.
        var askArgs = JsonSerializer.Serialize(new
        {
            context = "Need input before continuing.",
            questions = new[]
            {
                new
                {
                    prompt = "Which color?",
                    options = new object[] { new { label = "Red" }, new { label = "Blue" } },
                },
            },
        });
        SetupSubAgentResponse([
            new ToolCallMessage
            {
                FunctionName = AskUserQuestionToolProvider.ToolName,
                FunctionArgs = askArgs,
                ToolCallId = "tc_color",
                Role = Role.Assistant,
            },
        ]);

        _manager = CreateManager();
        await _manager.SpawnAsync("test-agent", "Pick a color", runInBackground: true);

        // Poll until the sub-agent's (mis)completion is relayed to parent.
        await WaitForConditionAsync(
            () =>
            {
                try
                {
                    _parentMock.Verify(
                        p => p.SendAsync(
                            It.IsAny<List<IMessage>>(),
                            It.IsAny<string?>(),
                            It.IsAny<string?>(),
                            It.IsAny<CancellationToken>()),
                        Times.AtLeastOnce);
                    return true;
                }
                catch
                {
                    return false;
                }
            },
            TimeSpan.FromSeconds(10));

        // Assert: a child parked on a question it asked (and cannot get answered through any path
        // reachable here) must not be reported to the parent as "[Completed] ... (no text response)" —
        // that tells the parent LLM the sub-agent finished with nothing to show, when it actually never
        // got to finish at all.
        _parentMock.Verify(
            p => p.SendAsync(
                It.Is<List<IMessage>>(msgs => msgs.Count == 1 && ContainsMisleadingCompletedTag(msgs[0])),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "a sub-agent parked on its own pending question is not '[Completed]' with no result");
    }

    [Fact]
    public async Task Completion_Background_WhenParkedOnAskUserQuestion_DeliversExactlyOneDescendantQuestionNotification()
    {
        // Arrange: same parking-on-AskUserQuestion setup as the sibling test above, but this test
        // asserts on the DISTINCT #246 signal itself — the descendant-question NotifyMessage relayed
        // via the manager's descendantQuestionSink (default here: straight to the parent agent, since
        // this manager was built with no upstream root target) — rather than the ordinary
        // SubAgentCompletion relay's wording.
        var askArgs = JsonSerializer.Serialize(new
        {
            context = "Need input before continuing.",
            questions = new[]
            {
                new
                {
                    prompt = "Which color?",
                    options = new object[] { new { label = "Red" }, new { label = "Blue" } },
                },
            },
        });
        SetupSubAgentResponse([
            new ToolCallMessage
            {
                FunctionName = AskUserQuestionToolProvider.ToolName,
                FunctionArgs = askArgs,
                ToolCallId = "tc_color",
                Role = Role.Assistant,
            },
        ]);

        _manager = CreateManager();
        var spawnJson = await _manager.SpawnAsync("test-agent", "Pick a color", runInBackground: true);
        using var spawnDoc = JsonDocument.Parse(spawnJson);
        var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        // Poll until the descendant-question notification specifically has been relayed.
        await WaitForConditionAsync(
            () =>
            {
                try
                {
                    _parentMock.Verify(
                        p => p.SendAsync(
                            It.Is<List<IMessage>>(msgs =>
                                msgs.Count == 1
                                && ContainsDescendantQuestionNotification(msgs[0], agentId, "test-agent")),
                            It.IsAny<string?>(),
                            It.IsAny<string?>(),
                            It.IsAny<CancellationToken>()),
                        Times.AtLeastOnce);
                    return true;
                }
                catch
                {
                    return false;
                }
            },
            TimeSpan.FromSeconds(10));

        // Assert: EXACTLY one descendant-question notification was delivered — not zero (it must
        // fire), and not more than one (a duplicate would let the client re-navigate/re-announce the
        // same pending question spuriously).
        _parentMock.Verify(
            p => p.SendAsync(
                It.Is<List<IMessage>>(msgs =>
                    msgs.Count == 1
                    && ContainsDescendantQuestionNotification(msgs[0], agentId, "test-agent")),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(1),
            "the #246 descendant-question signal must fire exactly once per parked question, not be duplicated");

        // Re-observing the already-completed background spawn (e.g. a client reconnect re-polling
        // completion) must not cause a second relay: HandleRunCompletionAsync only ever runs once per
        // RunCompletedMessage, and ObserveCompletionAsync merely awaits the (already-resolved)
        // completion latch — it never re-derives or re-sends the notification.
        _ = await _manager.ObserveCompletionAsync(agentId, CancellationToken.None);

        _parentMock.Verify(
            p => p.SendAsync(
                It.Is<List<IMessage>>(msgs =>
                    msgs.Count == 1
                    && ContainsDescendantQuestionNotification(msgs[0], agentId, "test-agent")),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(1),
            "re-observing an already-completed spawn (simulating a client reconnect) must not " +
            "re-deliver the notification");
    }

    [Fact]
    public async Task DisposeAsync_StopsAllAgents()
    {
        // Arrange: create a background sub-agent with a delayed response
        var blockingTcs = new TaskCompletionSource<bool>();
        SetupBlockingSubAgent(blockingTcs);

        _manager = CreateManager(maxConcurrent: 5);
        await _manager.SpawnAsync(
            "test-agent", "long-running task 1", runInBackground: true);

        // Act & Assert: dispose should not throw even with running agents
        blockingTcs.SetResult(true);
        var act = async () => await _manager.DisposeAsync();
        await act.Should().NotThrowAsync();

        // Prevent double-dispose in DisposeAsync
        _manager = null;
    }

    [Fact]
    public async Task SendMessageAsync_RunningAgent_SendsMessage()
    {
        // Arrange: sub-agent that blocks so it stays in Running state
        var blockingTcs = new TaskCompletionSource<bool>();
        SetupBlockingSubAgent(blockingTcs);

        _manager = CreateManager();
        var spawnJson = await _manager.SpawnAsync(
            "test-agent", "initial task", runInBackground: true);

        using var spawnDoc = JsonDocument.Parse(spawnJson);
        var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        // Act: continue with a new message (background) while the agent is running
        var resumeJson = await _manager.SendMessageAsync(
            agentId, "follow-up message", runInBackground: true);

        // Assert
        using var resumeDoc = JsonDocument.Parse(resumeJson);
        resumeDoc.RootElement.GetProperty("status").GetString()
            .Should().Be("message_sent");

        // Cleanup
        blockingTcs.SetResult(true);
    }

    [Fact]
    public async Task SendMessageAsync_CompletedAgent_RestartsRun()
    {
        // Arrange: sub-agent completes quickly
        SetupSubAgentResponse([
            new TextMessage { Text = "First result", Role = Role.Assistant },
        ]);

        _manager = CreateManager();
        var spawnJson = await _manager.SpawnAsync(
            "test-agent", "initial task", runInBackground: true);

        using var spawnDoc = JsonDocument.Parse(spawnJson);
        var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        // Poll until the sub-agent completes
        await WaitForConditionAsync(
            () =>
            {
                try
                {
                    var json = _manager!.Peek(agentId);
                    return json.Contains("\"completed\"");
                }
                catch
                {
                    return false;
                }
            },
            TimeSpan.FromSeconds(10));

        // Verify it completed
        var peekJson = _manager.Peek(agentId);
        using var peekDoc = JsonDocument.Parse(peekJson);
        peekDoc.RootElement.GetProperty("status").GetString()
            .Should().Be("completed");

        // Act: continue the completed agent - this restarts a new run.
        // Set up the mock to respond again for the restart.
        SetupSubAgentResponse([
            new TextMessage { Text = "Second result", Role = Role.Assistant },
        ]);

        var resumeJson = await _manager.SendMessageAsync(
            agentId, "continue work", runInBackground: true);

        // Assert
        using var resumeDoc = JsonDocument.Parse(resumeJson);
        resumeDoc.RootElement.GetProperty("status").GetString()
            .Should().Be("resumed");
    }

    [Fact]
    public async Task SendMessageAsync_ResolvesAgentByName()
    {
        // Arrange: blocking agent stays Running so it can receive a follow-up message
        var blockingTcs = new TaskCompletionSource<bool>();
        SetupBlockingSubAgent(blockingTcs);

        _manager = CreateManager();

        // Spawn with a caller-supplied name in the background
        await _manager.SpawnAsync(
            "test-agent", "initial task", name: "researcher", runInBackground: true);

        // Act: address the agent by its name instead of its generated id
        var resumeJson = await _manager.SendMessageAsync(
            "researcher", "follow-up message", runInBackground: true);

        // Assert
        using var resumeDoc = JsonDocument.Parse(resumeJson);
        var root = resumeDoc.RootElement;
        root.GetProperty("name").GetString().Should().Be("researcher");
        root.GetProperty("status").GetString().Should().Be("message_sent");

        // Cleanup
        blockingTcs.SetResult(true);
    }

    [Fact]
    public async Task SpawnAsync_WithoutName_DerivesReadableNameFromSubagentType_AndIsAddressableBySendMessage()
    {
        // Arrange: blocking agent stays Running so it can receive a follow-up message
        var blockingTcs = new TaskCompletionSource<bool>();
        SetupBlockingSubAgent(blockingTcs);

        _manager = CreateManager();

        // Act: spawn WITHOUT a caller-supplied name. Every agent must still surface a
        // human-readable handle (derived from the subagent_type) rather than only an opaque id.
        var spawnJson = await _manager.SpawnAsync(
            "test-agent", "initial task", runInBackground: true);

        // Assert: a readable name was derived from the template and is not just the raw id.
        using var spawnDoc = JsonDocument.Parse(spawnJson);
        var spawnRoot = spawnDoc.RootElement;
        var derivedName = spawnRoot.GetProperty("name").GetString();
        var agentId = spawnRoot.GetProperty("agent_id").GetString();

        derivedName.Should().NotBeNullOrWhiteSpace();
        derivedName.Should().StartWith("test-agent-");
        derivedName.Should().NotBe(agentId);

        // And the derived name is a first-class handle: SendMessage can address the agent by it.
        var resumeJson = await _manager.SendMessageAsync(
            derivedName!, "follow-up message", runInBackground: true);

        using var resumeDoc = JsonDocument.Parse(resumeJson);
        var resumeRoot = resumeDoc.RootElement;
        resumeRoot.GetProperty("name").GetString().Should().Be(derivedName);
        resumeRoot.GetProperty("status").GetString().Should().Be("message_sent");

        // Cleanup
        blockingTcs.SetResult(true);
    }

    [Fact]
    public async Task SpawnAsync_WithExplicitName_KeepsItVerbatim_NoDerivedFallback()
    {
        // Arrange
        var blockingTcs = new TaskCompletionSource<bool>();
        SetupBlockingSubAgent(blockingTcs);

        _manager = CreateManager();

        // Act: an explicitly supplied name must be kept exactly - the readable-name fallback
        // only fills in when the caller omits a name; it never rewrites a caller's choice.
        var spawnJson = await _manager.SpawnAsync(
            "test-agent", "initial task", name: "custom-name", runInBackground: true);

        // Assert
        using var spawnDoc = JsonDocument.Parse(spawnJson);
        spawnDoc.RootElement.GetProperty("name").GetString().Should().Be("custom-name");

        // Cleanup
        blockingTcs.SetResult(true);
    }

    [Fact]
    public async Task SendMessageAsync_InjectIntoRunningBackgroundAgent_DoesNotOverReleaseConcurrencyGate()
    {
        // Regression guard for the gate single-release invariant. A background sub-agent
        // continued in place via SendMessage (while still Running) feeds a SECOND run
        // under the SAME monitor, so that one monitor observes two RunCompletedMessages.
        // The concurrency slot is acquired once (at spawn), so it must be released exactly
        // once. Releasing per completion (the original bug) over-releases the SemaphoreSlim,
        // which throws SemaphoreFullException inside the monitor and flips the agent to
        // Error status. The fix releases once per monitor, so the agent settles 'completed'.
        var entered = new TaskCompletionSource<bool>();
        var release = new TaskCompletionSource<bool>();
        _subAgentMock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                async (_, _, ct) =>
                {
                    // Signal the first turn is in-flight, then block so the task input is
                    // already consumed before the follow-up is injected (forcing two runs).
                    _ = entered.TrySetResult(true);
                    await release.Task.WaitAsync(ct);
                    return ToAsyncEnumerable([
                        new TextMessage { Text = "done", Role = Role.Assistant },
                    ]);
                });

        _manager = CreateManager(maxConcurrent: 1);

        var spawnJson = await _manager.SpawnAsync(
            "test-agent", "initial task", runInBackground: true);
        using var spawnDoc = JsonDocument.Parse(spawnJson);
        var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        // Wait until the first run has consumed the task and is blocked, so the follow-up
        // becomes a distinct second run rather than collapsing into the first batch.
        (await entered.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();

        // Inject the follow-up while the first run is still Running -> same-monitor path.
        var resumeJson = await _manager.SendMessageAsync(
            agentId, "follow-up", runInBackground: true);
        using var resumeDoc = JsonDocument.Parse(resumeJson);
        resumeDoc.RootElement.GetProperty("status").GetString()
            .Should().Be("message_sent");

        // Release the block: the first run completes, then the queued follow-up drives a
        // second run — both completions are observed by the one monitor.
        release.SetResult(true);

        // The agent must settle in 'completed', NOT 'error'. Under the over-release bug the
        // monitor faults on the second completion (SemaphoreFullException) -> Error status.
        await WaitForConditionAsync(
            () =>
            {
                try
                {
                    return _manager!.Peek(agentId).Contains("\"completed\"");
                }
                catch
                {
                    return false;
                }
            },
            TimeSpan.FromSeconds(10));

        var peekJson = _manager.Peek(agentId);
        using var peekDoc = JsonDocument.Parse(peekJson);
        peekDoc.RootElement.GetProperty("status").GetString()
            .Should().Be(
                "completed",
                "the monitor must release the concurrency slot exactly once across both runs");
    }

    [Fact]
    public async Task SendMessageAsync_UnknownTarget_Throws()
    {
        // Arrange
        _manager = CreateManager();

        // Act
        var act = () => _manager.SendMessageAsync("non-existent-id", "some message");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Unknown sub-agent*non-existent-id*");
    }

    [Fact]
    public async Task Completion_Background_Error_SendsWrappedErrorToParent()
    {
        // Arrange: sub-agent throws on first call to trigger error run completion.
        // MultiTurnAgentLoop catches this and calls CompleteRunAsync(isError: true),
        // which produces RunCompletedMessage with IsError=true.
        _subAgentMock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("API call failed"));

        _manager = CreateManager();
        await _manager.SpawnAsync(
            "test-agent", "error-prone task", runInBackground: true);

        // Poll until parent receives error notification
        await WaitForConditionAsync(
            () =>
            {
                try
                {
                    _parentMock.Verify(
                        p => p.SendAsync(
                            It.Is<List<IMessage>>(msgs =>
                                msgs.Count == 1
                                && ContainsSubAgentError(msgs[0], "test-agent")),
                            It.IsAny<string?>(),
                            It.IsAny<string?>(),
                            It.IsAny<CancellationToken>()),
                        Times.AtLeastOnce);
                    return true;
                }
                catch
                {
                    return false;
                }
            },
            TimeSpan.FromSeconds(10));

        // Assert: parent received error notification specifically (not [Completed])
        _parentMock.Verify(
            p => p.SendAsync(
                It.Is<List<IMessage>>(msgs =>
                    msgs.Count == 1
                    && ContainsSubAgentError(msgs[0], "test-agent")),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Theory]
    [InlineData(null, null, null, null)]
    [InlineData(new[] { "tool1", "tool2" }, null, null, new[] { "tool1", "tool2" })]
    [InlineData(new[] { "tool1" }, new[] { "tool2" }, null, new[] { "tool1", "tool2" })]
    [InlineData(new[] { "tool1", "tool2" }, null, new[] { "tool2" }, new[] { "tool1" })]
    [InlineData(null, new[] { "tool1" }, null, new[] { "tool1" })]
    [InlineData(null, new[] { "tool1", "tool2" }, new[] { "tool1" }, new[] { "tool2" })]
    public void BuildEnabledToolSet_FiltersToolsCorrectly(
        string[]? templateTools,
        string[]? addTools,
        string[]? removeTools,
        string[]? expectedTools)
    {
        // Act
        var result = SubAgentManager.BuildEnabledToolSet(
            templateTools?.ToList(), addTools, removeTools);

        // Assert
        if (expectedTools == null)
        {
            result.Should().BeNull();
        }
        else
        {
            result.Should().NotBeNull();
            result.Should().BeEquivalentTo(expectedTools);
        }
    }

    [Fact]
    public void BuildEnabledToolSet_RemoveWithoutBaseSet_Throws()
    {
        // Act
        var act = () => SubAgentManager.BuildEnabledToolSet(
            templateEnabledTools: null,
            addTools: null,
            removeTools: ["tool1"]);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Cannot specify removeTools without enabledTools or addTools*");
    }

    [Fact]
    public async Task TryDeliverToRunningAsync_RunningAgent_DeliversContextToSubAgentNotParent()
    {
        // AC2: directory context routed to a Running sub-agent lands in THAT sub-agent's conversation
        // (its next model call), and never on the parent. The sub-agent's first turn blocks (so it stays
        // Running while we deliver) then makes a tool call, forcing a SECOND turn whose
        // GenerateReplyStreamingAsync argument must carry the injected context.
        var call1Entered = new TaskCompletionSource<bool>();
        var releaseCall1 = new TaskCompletionSource<bool>();
        var call2Entered = new TaskCompletionSource<bool>();
        var releaseCall2 = new TaskCompletionSource<bool>();
        List<IMessage>? secondCallMessages = null;
        var callCount = 0;

        _subAgentMock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                async (msgs, options, ct) =>
                {
                    var n = Interlocked.Increment(ref callCount);
                    if (n == 1)
                    {
                        _ = call1Entered.TrySetResult(true);
                        await releaseCall1.Task.WaitAsync(ct);
                        return ToAsyncEnumerable([
                            new ToolCallMessage
                            {
                                FunctionName = "noop",
                                FunctionArgs = "{}",
                                ToolCallId = "call_1",
                                Role = Role.Assistant,
                            },
                        ]);
                    }

                    secondCallMessages = [.. msgs];
                    _ = call2Entered.TrySetResult(true);
                    await releaseCall2.Task.WaitAsync(ct);
                    return ToAsyncEnumerable([
                        new TextMessage { Text = "done", Role = Role.Assistant },
                    ]);
                });

        _manager = CreateManager();
        var spawnJson = await _manager.SpawnAsync("test-agent", "initial task", runInBackground: true);
        using var spawnDoc = JsonDocument.Parse(spawnJson);
        var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        // Wait until the first turn is in-flight (sub-agent Running).
        (await call1Entered.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();

        // Act: deliver directory context to the running sub-agent.
        const string contextMarker = "CTX_MARKER directory rules";
        var result = await _manager.TryDeliverToRunningAsync(
            agentId,
            [new TextMessage { Role = Role.User, Text = contextMarker }],
            CancellationToken.None);

        // Assert: accepted for the running target.
        result.Should().Be(SubAgentContextDeliveryResult.Delivered);

        // Let the first turn finish with a tool call so the loop polls the injected context and runs turn 2.
        releaseCall1.SetResult(true);
        (await call2Entered.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();

        // The injected context reached the SUB-AGENT's next model call...
        secondCallMessages.Should().NotBeNull();
        secondCallMessages!
            .OfType<TextMessage>()
            .Any(m => m.Text != null && m.Text.Contains(contextMarker))
            .Should().BeTrue("routed context must be delivered into the sub-agent's own conversation");

        // ...and NOT to the parent conversation (no fan-out / no relay for a still-running sub-agent).
        _parentMock.Verify(
            p => p.SendAsync(
                It.IsAny<List<IMessage>>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        // Cleanup: let the run finish.
        releaseCall2.SetResult(true);
    }

    [Fact]
    public async Task TryDeliverToRunningAsync_CompletedAgent_ReturnsTargetNotDeliverable_NoRestartNoRelay()
    {
        // AC3/AC5: a delivery to a completed sub-agent is dropped — the sub-agent is NOT restarted and no
        // spurious completion is relayed to the parent.
        SetupSubAgentResponse([
            new TextMessage { Text = "First result", Role = Role.Assistant },
        ]);

        _manager = CreateManager();
        var spawnJson = await _manager.SpawnAsync("test-agent", "initial task", runInBackground: true);
        using var spawnDoc = JsonDocument.Parse(spawnJson);
        var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        // Wait for the background completion to relay to the parent (so the run is genuinely finished).
        await WaitForConditionAsync(
            () =>
            {
                try
                {
                    _parentMock.Verify(
                        p => p.SendAsync(
                            It.IsAny<List<IMessage>>(),
                            It.IsAny<string?>(),
                            It.IsAny<string?>(),
                            It.IsAny<CancellationToken>()),
                        Times.AtLeastOnce);
                    return true;
                }
                catch
                {
                    return false;
                }
            },
            TimeSpan.FromSeconds(10));

        // Ignore the legitimate completion relay; assert the delivery adds none.
        _parentMock.Invocations.Clear();

        // Act
        var result = await _manager.TryDeliverToRunningAsync(
            agentId,
            [new TextMessage { Role = Role.User, Text = "late context" }],
            CancellationToken.None);

        // Assert: dropped (never restarted), no spurious relay.
        result.Should().Be(SubAgentContextDeliveryResult.TargetNotDeliverable);

        _subAgentMock.Verify(
            a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "a completed target must not be restarted");

        _parentMock.Verify(
            p => p.SendAsync(
                It.IsAny<List<IMessage>>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "dropping late context must not relay a spurious completion to the parent");
    }

    [Fact]
    public async Task TryDeliverToRunningAsync_UnknownId_ReturnsNotOwned()
    {
        // A discovery whose agent_id matches no sub-agent of this manager is NotOwned — the injector keeps
        // looking / drops without marking-seen so a gateway redelivery can still route it later.
        _manager = CreateManager();

        var result = await _manager.TryDeliverToRunningAsync(
            "no-such-agent",
            [new TextMessage { Role = Role.User, Text = "ctx" }],
            CancellationToken.None);

        result.Should().Be(SubAgentContextDeliveryResult.NotOwned);
    }

    [Fact]
    public async Task TryDeliverToRunningAsync_AfterCompletion_ParentRelayHappensExactlyOnce()
    {
        // Completion-boundary contract (blind-spot #2): a context delivery racing a just-finished
        // background sub-agent must never spawn a second run, relay a second completion, or run against a
        // disposed provider. Proven deterministically: after the legitimate completion relay, a delivery is
        // refused and the total parent relay count stays exactly one, with the agent settled 'completed'.
        SetupSubAgentResponse([
            new TextMessage { Text = "boundary result", Role = Role.Assistant },
        ]);

        _manager = CreateManager();
        var spawnJson = await _manager.SpawnAsync("test-agent", "boundary task", runInBackground: true);
        using var spawnDoc = JsonDocument.Parse(spawnJson);
        var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        await WaitForConditionAsync(
            () =>
            {
                try
                {
                    _parentMock.Verify(
                        p => p.SendAsync(
                            It.IsAny<List<IMessage>>(),
                            It.IsAny<string?>(),
                            It.IsAny<string?>(),
                            It.IsAny<CancellationToken>()),
                        Times.AtLeastOnce);
                    return true;
                }
                catch
                {
                    return false;
                }
            },
            TimeSpan.FromSeconds(10));

        var result = await _manager.TryDeliverToRunningAsync(
            agentId,
            [new TextMessage { Role = Role.User, Text = "boundary context" }],
            CancellationToken.None);
        result.Should().Be(SubAgentContextDeliveryResult.TargetNotDeliverable);

        // Exactly one parent relay total (the legitimate completion) — no double relay.
        _parentMock.Verify(
            p => p.SendAsync(
                It.IsAny<List<IMessage>>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // The sub-agent settled 'completed', not 'error' (no run against a disposed provider).
        using var peekDoc = JsonDocument.Parse(_manager.Peek(agentId));
        peekDoc.RootElement.GetProperty("status").GetString().Should().Be("completed");
    }

    #region Helpers
    private static async Task WaitForConditionAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
    }

    /// <summary>
    /// Checks if a message is a sub-agent-completion NotifyMessage containing the completion markers.
    /// Extracted as a static method to avoid pattern matching in Moq expression trees.
    /// </summary>
    private static bool ContainsSubAgentResult(
        IMessage message,
        string templateName,
        string expectedResultText)
    {
        if (message is not NotifyMessage { NotifyKind: NotifyKinds.SubAgentCompletion } nm)
        {
            return false;
        }

        var text = nm.GetText() ?? string.Empty;
        return text.Contains($"<sub-agent name=\"{templateName}\"")
            && text.Contains("</sub-agent>")
            && text.Contains(expectedResultText);
    }

    /// <summary>
    /// Checks if a message is a sub-agent-completion NotifyMessage containing the error markers.
    /// Verifies the [Error] tag specifically to distinguish from [Completed].
    /// </summary>
    private static bool ContainsSubAgentError(
        IMessage message,
        string templateName)
    {
        if (message is not NotifyMessage { NotifyKind: NotifyKinds.SubAgentCompletion } nm)
        {
            return false;
        }

        var text = nm.GetText() ?? string.Empty;
        return text.Contains($"<sub-agent name=\"{templateName}\"")
            && text.Contains("</sub-agent>")
            && text.Contains("[Error]");
    }

    /// <summary>
    /// Checks if a message is a sub-agent-completion NotifyMessage that misleadingly claims the child
    /// finished with no result ("[Completed] ... (no text response)"), the exact text a child parked on
    /// its own pending <c>AskUserQuestion</c> must never be reported with.
    /// </summary>
    private static bool ContainsMisleadingCompletedTag(IMessage message)
    {
        if (message is not NotifyMessage { NotifyKind: NotifyKinds.SubAgentCompletion } nm)
        {
            return false;
        }

        var text = nm.GetText() ?? string.Empty;
        return text.Contains("[Completed]") && text.Contains("(no text response)");
    }

    /// <summary>
    /// Checks if a message is the #246 descendant-question NotifyMessage for the given descendant
    /// <paramref name="expectedAgentId"/>/<paramref name="expectedTemplateName"/>: the right
    /// <see cref="NotifyKinds.DescendantQuestion"/> kind, <see cref="NotifyMessage.SourceToolCallId"/>
    /// stamped with the descendant's own agent id (not a tool-call id belonging to the question
    /// itself), and the "awaiting answer" wording rather than "[Completed]".
    /// </summary>
    private static bool ContainsDescendantQuestionNotification(
        IMessage message,
        string expectedAgentId,
        string expectedTemplateName)
    {
        if (message is not NotifyMessage { NotifyKind: NotifyKinds.DescendantQuestion } nm)
        {
            return false;
        }

        return nm.SourceToolCallId == expectedAgentId
            && nm.Label == expectedTemplateName
            && (nm.GetText() ?? string.Empty).Contains("[AwaitingAnswer]");
    }

    /// <summary>
    /// Creates a SubAgentManager with the test's mock sub-agent and parent. When
    /// <paramref name="qualifiedTemplateKey"/> is supplied, the single template is registered under
    /// that key instead of "test-agent" (used to exercise plugin-prefix resolution).
    /// </summary>
    private SubAgentManager CreateManager(int maxConcurrent = 5, string? qualifiedTemplateKey = null)
    {
        var options = CreateOptions(maxConcurrent, qualifiedTemplateKey);
        return new SubAgentManager(
            parentAgent: _parentMock.Object,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: options,
            source: new MutableSubAgentTemplateSource(options.Templates));
    }

    /// <summary>
    /// Creates a SubAgentManager whose source registers a template under each of the given keys
    /// (all backed by the mock sub-agent). Used to build ambiguous-segment resolution scenarios.
    /// </summary>
    private SubAgentManager CreateManagerWithTemplates(params string[] templateKeys)
    {
        var templates = templateKeys.ToDictionary(key => key, _ => MakeTemplate());
        var options = new SubAgentOptions
        {
            Templates = templates,
            MaxConcurrentSubAgents = 5,
        };
        return new SubAgentManager(
            parentAgent: _parentMock.Object,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: options,
            source: new MutableSubAgentTemplateSource(options.Templates));
    }

    /// <summary>A minimal template backed by the shared mock sub-agent.</summary>
    private SubAgentTemplate MakeTemplate() => new()
    {
        SystemPrompt = "You are a test agent.",
        AgentFactory = () => _subAgentMock.Object,
    };

    /// <summary>
    /// Creates SubAgentOptions with a single template backed by the mock sub-agent, keyed by
    /// <paramref name="qualifiedTemplateKey"/> when given (defaults to "test-agent").
    /// </summary>
    private SubAgentOptions CreateOptions(int maxConcurrent = 5, string? qualifiedTemplateKey = null)
    {
        return new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                [qualifiedTemplateKey ?? "test-agent"] = MakeTemplate(),
            },
            MaxConcurrentSubAgents = maxConcurrent,
        };
    }

    /// <summary>
    /// Configures the mock sub-agent to return the given messages from
    /// GenerateReplyStreamingAsync on every call.
    /// </summary>
    private void SetupSubAgentResponse(List<IMessage> messages)
    {
        _subAgentMock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(ToAsyncEnumerable(messages)));
    }

    /// <summary>
    /// Configures the mock sub-agent to block until <paramref name="release"/> is
    /// completed (or the run is cancelled), keeping it in the Running state. Used by
    /// concurrency, disposal, and continuation tests that need a long-running agent.
    /// </summary>
    private void SetupBlockingSubAgent(TaskCompletionSource<bool> release)
    {
        _subAgentMock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                async (_, _, ct) =>
                {
                    await release.Task.WaitAsync(ct);
                    return ToAsyncEnumerable([
                        new TextMessage { Text = "done", Role = Role.Assistant },
                    ]);
                });
    }

    /// <summary>
    /// Converts a list of messages to an IAsyncEnumerable for mock setup.
    /// </summary>
    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
        List<IMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var msg in messages)
        {
            ct.ThrowIfCancellationRequested();
            yield return msg;
            await Task.Yield();
        }
    }

    #endregion
}
