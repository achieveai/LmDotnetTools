using AchieveAi.LmDotnetTools.LmTestUtils;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.ClientTools;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
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
            // Bounded: an unbounded teardown turns one stalled test into an aborted run (#362).
            await Wait.ForTeardownAsync(_manager, "the sub-agent manager under test");
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

    /// <summary>
    /// A non-positive <see cref="SubAgentOptions.OutputChannelCapacity"/> is rejected where the host
    /// hands its options over, not where a child loop finally uses them.
    /// </summary>
    /// <remarks>
    /// The value is only read when a spawned child builds a bounded output channel, so without this
    /// guard a misconfigured host fails as an <see cref="ArgumentOutOfRangeException"/> thrown from
    /// deep inside a live stream — surfacing as a broken sub-agent run rather than as bad configuration.
    /// Mirrors the existing <c>MaxConcurrentSubAgents</c>/<c>MaxQueuedSubAgents</c> checks.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsNonPositiveOutputChannelCapacity(int capacity)
    {
        var options = CreateOptions() with { OutputChannelCapacity = capacity };

        var act = () => new SubAgentManager(
            _parentMock.Object,
            [],
            new Dictionary<string, ToolHandler>(),
            options,
            new MutableSubAgentTemplateSource(options.Templates));

        act.Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithMessage("*OutputChannelCapacity*");
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
        await Wait.UntilAsync(
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
            "both queued sub-agents reported completed",
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

        // Poll until the monitoring task has processed the run to completion. `json.Contains("\"status\"")`
        // (#357) is trivially true from the instant the agent is spawned -- EVERY Peek payload has a
        // "status" key, whatever its value -- so it proved nothing about the sub-agent's own progress.
        // Waiting for the specific terminal value instead ties the wait to the behaviour under test.
        await Wait.UntilAsync(
            () =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(_manager!.Peek(agentId));
                    return doc.RootElement.GetProperty("status").GetString() == "completed";
                }
                catch
                {
                    return false;
                }
            },
            "the spawned sub-agent reported completed",
            TimeSpan.FromSeconds(10));

        // Act
        var peekJson = _manager.Peek(agentId);

        // Assert
        using var peekDoc = JsonDocument.Parse(peekJson);
        var peekRoot = peekDoc.RootElement;

        peekRoot.GetProperty("agent_id").GetString().Should().Be(agentId);
        peekRoot.GetProperty("template").GetString().Should().Be("test-agent");
        peekRoot.GetProperty("task").GetString().Should().Be("Do analysis");
        peekRoot.GetProperty("status").GetString().Should().Be("completed");

        // The other half of the name this test claims to cover: not just a status, but the turns
        // themselves. #357's original version never inspected recent_turns at all, so a regression
        // that stopped recording turns entirely would have passed silently.
        var recentTurns = peekRoot.GetProperty("recent_turns").EnumerateArray().ToArray();
        recentTurns.Should().NotBeEmpty("the run that produced the assistant text must have recorded a turn");
        recentTurns
            .Select(turn => turn.TryGetProperty("text", out var text) ? text.GetString() : null)
            .Should()
            .Contain(
                text => text != null && text.Contains("Working on it...", StringComparison.Ordinal),
                "the recorded turn must reflect the assistant text the sub-agent actually produced");
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
        await Wait.UntilAsync(
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
            "the parent received the wrapped sub-agent result",
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
        await Wait.UntilAsync(
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
            "the parent received a relay from the parked sub-agent",
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
        await Wait.UntilAsync(
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
            "the parent received the descendant-question notification",
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

        // Re-observing the parked spawn (e.g. a client reconnect re-polling completion) must not cause
        // a second relay. Unlike a genuinely finished run, a child parked on its own question is NOT
        // terminal (see the Finding-2 fix in HandleRunCompletionAsync): its Completion latch is
        // deliberately left unresolved until the human's answer produces a real final run, so observing
        // it with a bounded token times out rather than returning a (stale/placeholder) result — and,
        // critically, must not re-derive or re-send the notification.
        using var reobserveCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var reobserve = async () => await _manager.ObserveCompletionAsync(agentId, reobserveCts.Token);
        await reobserve.Should().ThrowAsync<OperationCanceledException>(
            "the child is parked (not terminal), so its Completion latch is intentionally left "
                + "unresolved until the human answers the pending question");

        _parentMock.Verify(
            p => p.SendAsync(
                It.Is<List<IMessage>>(msgs =>
                    msgs.Count == 1
                    && ContainsDescendantQuestionNotification(msgs[0], agentId, "test-agent")),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(1),
            "re-observing a parked spawn (simulating a client reconnect) must not re-deliver the " +
            "notification");

        // The sub-agent itself is still reported Running (non-terminal): the parked question keeps its
        // loop/provider live and its concurrency slot held, rather than being misreported "completed".
        using var peekDoc = JsonDocument.Parse(_manager.Peek(agentId));
        peekDoc.RootElement.GetProperty("status").GetString().Should().Be("running");
    }

    [Fact]
    public async Task SpawnAsync_Foreground_WhenParkedOnAskUserQuestion_BlocksThenReturnsRealAnswerAfterResolution()
    {
        // Arrange: same AskUserQuestion-parking setup as the background tests above, but this test
        // drives the actual FOREGROUND (runInBackground: false) path, which blocks on
        // AwaitCompletionAsync(state, ct) -> state.Completion.Task. Before the Finding-2 fix, a parked
        // question was (mis)treated as terminal: TryCompleteWithResult ran immediately with the
        // misleading "(no text response)" placeholder, so this foreground call would return almost
        // instantly with the WRONG text, and the sub-agent's concurrency slot/loop/provider would
        // already be torn down — losing the real answer forever. The fix keeps Completion unresolved
        // while parked, so the foreground call must still be pending when the question is observed,
        // and must only complete (with the REAL final text) after the deferred AskUserQuestion tool
        // call is resolved and the resulting run finishes.
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

        // maxConcurrent: 1 so the capacity assertion below (a second spawn must queue, not start) is
        // meaningful: the parked foreground spawn must still be holding its permit.
        _manager = CreateManager(maxConcurrent: 1);

        var foregroundTask = _manager.SpawnAsync(
            "test-agent", "Pick a color", name: "color-agent", runInBackground: false);

        // Poll (rather than a fixed delay, which would be flaky under load) until the child actually
        // parks on its own AskUserQuestion — i.e. GetDeferredToolCallsAsync reports it — resolving
        // the live MultiTurnAgentLoop instance through the same seam SubAgentManager itself has no
        // dedicated "answer a sub-agent's question" API for: TryGetAgent + a cast to the concrete
        // loop type, exactly as a real caller would have to.
        MultiTurnAgentLoop? loop = null;
        IReadOnlyList<DeferredToolCallInfo> deferred = [];
        await Wait.UntilAsync(
            async () =>
            {
                if (_manager!.TryGetAgent("color-agent", out var agent) && agent is MultiTurnAgentLoop l)
                {
                    loop = l;
                    deferred = await l.GetDeferredToolCallsAsync();
                    return deferred.Any(d => d.ToolCallId == "tc_color");
                }

                return false;
            },
            "the child registered as a MultiTurnAgentLoop and parked tc_color",
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(50),
            observed: () => loop is null
                ? "no MultiTurnAgentLoop registered yet for 'color-agent'"
                : $"deferred tool calls: [{string.Join(", ", deferred.Select(d => d.ToolCallId))}]");

        loop.Should().NotBeNull("the child must have registered as a MultiTurnAgentLoop by now");
        deferred.Should().Contain(
            d => d.ToolCallId == "tc_color",
            "the child must have parked its AskUserQuestion tool call for external resolution");

        // Peek (unlike TryGetAgent/SendMessageAsync) is keyed strictly by the internal agent_id, not
        // the caller-supplied name, so resolve it once via ListAgents now that the child is registered.
        var agentId = _manager!.ListAgents().Single(a => a.Name == "color-agent").AgentId;

        // The deferred registry is populated DURING the run, so seeing tc_color above only proves the
        // child parked — not that the monitor has yet dequeued that run's RunCompletedMessage and
        // classified it. Wait for the descendant-question notification, which HandleRunCompletionAsync
        // emits from inside the awaiting-question branch itself: its arrival is the only observable
        // proof that the parked completion was processed AND deliberately left the latch unresolved.
        // Answering before it would also be answering earlier than any real client can — the
        // notification is precisely how a client learns the question exists (#246) — and would race
        // the monitor's own pending-question probe, which reads the registry live: an answer that
        // lands first empties the registry, and the parked run is then misread as genuinely terminal
        // and settled with the "(no text response)" placeholder this test exists to forbid.
        await Wait.UntilAsync(
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
            "the parent received the descendant-question notification",
            TimeSpan.FromSeconds(10));

        // Assert (lifecycle): the foreground caller must still be blocked — NOT resolved with a
        // placeholder — while the question is outstanding. Non-vacuous now: the wait above proves the
        // monitor already handled the parked run's completion and chose not to settle it.
        foregroundTask.IsCompleted.Should().BeFalse(
            "a foreground spawn parked on its own pending question must keep blocking for the real "
                + "answer, not settle immediately with a '(no text response)' placeholder");

        using (var peekDoc2 = JsonDocument.Parse(_manager.Peek(agentId)))
        {
            peekDoc2.RootElement.GetProperty("status").GetString().Should().Be(
                "running",
                "the parked child is not terminal: its loop/provider/concurrency slot all stay live");
        }

        // Assert (capacity): with maxConcurrent: 1, the parked child must still be holding its
        // permit, so a second spawn attempt is deferred (queued), never started outright.
        var secondJson = await _manager.SpawnAsync(
            "test-agent", "unrelated second task", runInBackground: true);
        using (var secondDoc = JsonDocument.Parse(secondJson))
        {
            secondDoc.RootElement.GetProperty("status").GetString().Should().Be(
                "queued",
                "the parked foreground child must still hold its concurrency permit, so a second "
                    + "spawn cannot start until the real answer resolves the first");
        }

        // Act: reconfigure the mock for the run the answer triggers, then resolve the deferred call
        // exactly the way a real client answering the AskUserQuestion prompt would — directly on the
        // child's own live MultiTurnAgentLoop (the production resolution mechanism).
        SetupSubAgentResponse([
            new TextMessage { Text = "Final answer: chose Red.", Role = Role.Assistant },
        ]);

        var outcome = await loop!.TryResolveToolCallAsync("tc_color", "Red");
        outcome.Should().Be(ResolveToolCallOutcome.Resolved);

        // Assert: the foreground caller unblocks with the REAL final text from the answer-triggered
        // run — not the placeholder, and exactly once (a stray second completion would mean the
        // parked-question branch and the genuine-terminal branch both tried to settle it).
        var result = await foregroundTask.WaitAsync(TimeSpan.FromSeconds(10));
        result.Should().Be("Final answer: chose Red.");

        using (var finalPeekDoc = JsonDocument.Parse(_manager.Peek(agentId)))
        {
            finalPeekDoc.RootElement.GetProperty("status").GetString().Should().Be(
                "completed",
                "only the answer-triggered run's genuine completion should flip the child terminal");
        }
    }

    /// <summary>
    /// The AskUserQuestion call in the shape a STREAMING provider actually emits it (#262): a
    /// <see cref="ToolsCallUpdateMessage"/> carrying update chunks, never a consolidated
    /// <c>ToolCallMessage</c>.
    /// </summary>
    /// <remarks>
    /// This distinction is the whole point. The loop's publishing middleware sits UPSTREAM of the joiner
    /// (Provider -> MessageTransformation -> JsonFragment -> Publishing -> Joiner -> ToolCall), so a
    /// subscriber — and therefore the sub-agent monitor — sees tool-call UPDATES and never the
    /// consolidated message the joiner builds downstream. Anthropic and OpenAI chat-completions both emit
    /// only the streaming shape; only OpenAiResponsesAgent emits the consolidated one. A mock that emitted
    /// the consolidated shape would exercise a path most deployments never take, which is exactly how an
    /// earlier version of this test passed against a fix that could not have fired in production.
    ///
    /// <para>
    /// <c>GenerationId</c> is load-bearing twice over, which is why it is echoed from the request's own
    /// <see cref="GenerateReplyOptions"/> exactly as a real provider echoes it. First,
    /// <c>MessageTransformationMiddleware</c> returns a message unchanged when its generation id is null
    /// or empty, and that same pass is what converts a plural <see cref="ToolsCallUpdateMessage"/> into
    /// the singular updates the joiner folds into a <c>ToolCallMessage</c> — without it the pipeline
    /// yields a plural <c>ToolsCallMessage</c>, which the loop's turn body does not match, so the tool
    /// never runs. Second, the deferred entry is stamped with the tool call's generation id, and the run
    /// loop parks only when that matches the TURN's generation — so a fabricated id lets the tool defer
    /// and then leaves the loop marching into the next turn, where the deferral precondition rejects it.
    /// </para>
    /// </remarks>
    private static ToolsCallUpdateMessage StreamingAskUserQuestionCall(
        string toolCallId, string args, string? generationId) =>
        new()
        {
            Role = Role.Assistant,
            GenerationId = generationId,
            ToolCallUpdates =
            [
                new ToolCallUpdate
                {
                    ToolCallId = toolCallId,
                    Index = 0,
                    FunctionName = AskUserQuestionToolProvider.ToolName,
                    FunctionArgs = args,
                },
            ],
        };

    /// <summary>The AskUserQuestion arguments used by the #262 regression tests.</summary>
    private static string AskColorArgs() =>
        JsonSerializer.Serialize(new
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

    [Fact]
    public async Task SpawnAsync_Foreground_WhenPostResolutionRunHasNoText_StillSettlesWithTheRealAnswer()
    {
        // Arrange (#262): the sibling test above proves a parked question keeps the foreground caller
        // blocked, but it lets the answer and the answer's text land in the SAME synthetic run, so it
        // never enters the window this test is about — the interval that opens the instant the deferred
        // AskUserQuestion is resolved (which EMPTIES the loop's live deferred-call registry) and closes
        // only when the answer-triggered work actually produces assistant text. Any run completion
        // landing inside it reports "no question pending" for a benign reason, so the terminal gate read
        // it as genuinely finished and settled the one-shot Completion latch with the
        // "(no text response)" placeholder — permanently discarding the real answer that followed,
        // because a TaskCompletionSource settles once.
        //
        // The provider script below stages exactly that: run 1 parks on the question, run 2 (the one the
        // answer triggers) yields NOTHING AT ALL — the zero-model-turn shape a resolution-triggered child
        // run has when it is not the one clearing the last outstanding call (#227), and the shape that
        // used to settle the caller with the placeholder — and run 3 carries the real answer. Run 1 emits
        // the STREAMING tool-call shape, so the latch is driven by the deferred placeholder the loop
        // publishes rather than by a consolidated message most providers never produce; see
        // StreamingAskUserQuestionCall.
        var askArgs = AskColorArgs();

        const string RealAnswer = "Final answer: chose Red.";
        var providerCalls = 0;
        _subAgentMock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>((_, opts, _) =>
            {
                List<IMessage> reply = Interlocked.Increment(ref providerCalls) switch
                {
                    1 => [StreamingAskUserQuestionCall("tc_color", askArgs, opts.GenerationId)],

                    // The answer-triggered run: zero model turns, so nothing for the caller yet.
                    2 => [],

                    _ => [new TextMessage { Text = RealAnswer, Role = Role.Assistant }],
                };

                return Task.FromResult(ToAsyncEnumerable(reply));
            });

        // maxConcurrent: 1 so the permit assertions below are meaningful.
        _manager = CreateManager(maxConcurrent: 1);

        var foregroundTask = _manager.SpawnAsync(
            "test-agent", "Pick a color", name: "color-agent", runInBackground: false);

        MultiTurnAgentLoop? loop = null;
        IReadOnlyList<DeferredToolCallInfo> deferred = [];
        await Wait.UntilAsync(
            async () =>
            {
                if (_manager!.TryGetAgent("color-agent", out var agent) && agent is MultiTurnAgentLoop l)
                {
                    loop = l;
                    deferred = await l.GetDeferredToolCallsAsync();
                    return deferred.Any(d => d.ToolCallId == "tc_color");
                }

                return false;
            },
            "the child registered as a MultiTurnAgentLoop and parked tc_color",
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(50),
            observed: () => loop is null
                ? "no MultiTurnAgentLoop registered yet for 'color-agent'"
                : $"deferred tool calls: [{string.Join(", ", deferred.Select(d => d.ToolCallId))}]");

        var agentId = _manager!.ListAgents().Single(a => a.Name == "color-agent").AgentId;

        // Answering before the monitor has classified the parked run is a DIFFERENT ordering, covered by
        // its own test below; the descendant-question notification is emitted from inside the parked
        // branch itself, so its arrival proves that classification already happened here.
        await Wait.UntilAsync(
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
            "the parent received the descendant-question notification",
            TimeSpan.FromSeconds(10),
            observed: () =>
                $"provider calls: {Volatile.Read(ref providerCalls)}, "
                + $"parent invocations: {_parentMock.Invocations.Count}, "
                + $"deferred tool calls: [{string.Join(", ", deferred.Select(d => d.ToolCallId))}]");

        // Watch the child's own message stream on a SECOND subscription — the same public seam the
        // manager's monitor uses, and independent of it. This is what makes the window deterministic
        // rather than timed: a RunCompletedMessage is published (with HasPendingMessages already
        // computed) before any subscriber dequeues it, so once this side has counted run 2's completion,
        // the monitor is guaranteed to process that exact text-free, no-pending-messages completion —
        // whatever the test does next.
        //
        // The subscription must be REGISTERED before the answer, and registering it from a Task.Run
        // body would not be: `SubscribeAsync` only reaches its registration when the enumerator is
        // first advanced, so under the full suite's load the thread pool can leave this side
        // unregistered until after run 2 has already completed. Replay would not cover for that —
        // publishing a RunCompletedMessage clears the replay buffer and marks the run inactive, so a
        // late subscriber sees nothing of it. Advancing the enumerator once HERE closes that: the
        // method body runs synchronously as far as the registration (there is no await before it), so
        // by the time MoveNextAsync has returned — completed or not — this subscriber is in the
        // publish set. The pending advance is then handed to the loop below.
        using var watchCts = new CancellationTokenSource();
        var completedRuns = 0;
        var messages = loop!.SubscribeAsync(watchCts.Token).GetAsyncEnumerator(watchCts.Token);
        var pendingMove = messages.MoveNextAsync();
        var watcher = Task.Run(async () =>
        {
            try
            {
                for (var moved = await pendingMove; moved; moved = await messages.MoveNextAsync())
                {
                    if (messages.Current is RunCompletedMessage { HasPendingMessages: false })
                    {
                        _ = Interlocked.Increment(ref completedRuns);
                    }
                }
            }
            finally
            {
                await messages.DisposeAsync();
            }
        });

        var completedBeforeAnswer = Volatile.Read(ref completedRuns);

        // Act: answer the question exactly as a real client does.
        var outcome = await loop!.TryResolveToolCallAsync("tc_color", "Red");
        outcome.Should().Be(ResolveToolCallOutcome.Resolved);

        await Wait.UntilAsync(
            () => Volatile.Read(ref completedRuns) > completedBeforeAnswer,
            "the answer-triggered run completed without producing any assistant text",
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(25),
            observed: () =>
                $"completed runs: {Volatile.Read(ref completedRuns)} (before the answer: {completedBeforeAnswer}), "
                + $"provider calls: {Volatile.Read(ref providerCalls)}");

        // The text-free completion is now guaranteed to reach the monitor. It must NOT have been taken
        // for a finished run: the child stays Running, holding its loop, provider and permit, so the
        // work the answer actually set in motion can still produce a result.
        using (var midPeekDoc = JsonDocument.Parse(_manager.Peek(agentId)))
        {
            midPeekDoc.RootElement.GetProperty("status").GetString().Should().Be(
                "running",
                "a run that completed with no assistant text after the question was answered has "
                    + "nothing to hand the caller, so it is not the completion that ends this sub-agent");
        }

        // Drive the run that carries the real answer.
        _ = await _manager.SendMessageAsync(agentId, "continue", runInBackground: true);

        // Assert: the foreground caller settles with the REAL answer. Before the fix the placeholder
        // won the race to the one-shot latch, so this returned "(no text response)" and the answer here
        // was silently thrown away.
        var result = await foregroundTask.WaitAsync(TimeSpan.FromSeconds(10));
        result.Should().Be(
            RealAnswer,
            "the caller must receive the answer-derived text, never the '(no text response)' "
                + "placeholder produced by a completion that landed before the answer's own output");

        await watchCts.CancelAsync();
        try { await watcher; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task SendMessageAsync_AfterAnAnsweredQuestionSettled_StillCompletesATextFreeRun()
    {
        // Arrange (#262, the latch's other edge): the parked latch makes a text-free completion
        // non-terminal, which is only safe because it spans a bounded interval — parking until the real
        // answer-derived result arrives. If it outlived that result, a sub-agent that once asked a
        // question could never finish a text-free run again: the caller would block forever on a loop
        // with nothing left to say. This drives exactly that continuation.
        var askArgs = AskColorArgs();

        var providerCalls = 0;
        _subAgentMock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>((_, opts, _) =>
            {
                List<IMessage> reply = Interlocked.Increment(ref providerCalls) switch
                {
                    1 => [StreamingAskUserQuestionCall("tc_color", askArgs, opts.GenerationId)],
                    2 => [new TextMessage { Text = "Final answer: chose Red.", Role = Role.Assistant }],

                    // The continuation, deliberately shaped as ZERO model turns rather than a
                    // thinking-only reply. Thinking-only is rejected by the "did the model speak"
                    // discriminator no matter what the latch says, so it could not tell a cleared
                    // latch from a stale one — the sibling test at the dangerous edge covers that
                    // shape. A run that never reached the model is the one and only case the latch
                    // itself decides, so it is the case that can prove the latch was released.
                    _ => [],
                };

                return Task.FromResult(ToAsyncEnumerable(reply));
            });

        _manager = CreateManager(maxConcurrent: 1);

        var foregroundTask = _manager.SpawnAsync(
            "test-agent", "Pick a color", name: "color-agent", runInBackground: false);

        MultiTurnAgentLoop? loop = null;
        await Wait.UntilAsync(
            async () =>
            {
                if (_manager!.TryGetAgent("color-agent", out var agent) && agent is MultiTurnAgentLoop l)
                {
                    loop = l;
                    return (await l.GetDeferredToolCallsAsync()).Any(d => d.ToolCallId == "tc_color");
                }

                return false;
            },
            "the child parked tc_color",
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(50));

        var agentId = _manager!.ListAgents().Single(a => a.Name == "color-agent").AgentId;

        (await loop!.TryResolveToolCallAsync("tc_color", "Red"))
            .Should().Be(ResolveToolCallOutcome.Resolved);

        // The answer's own text settles the caller — and, with it, clears the parked latch.
        var answer = await foregroundTask.WaitAsync(TimeSpan.FromSeconds(10));
        answer.Should().Be("Final answer: chose Red.");

        // Act + Assert: the next run produces no assistant text and no question is outstanding, so it is
        // genuinely terminal and must settle — with the placeholder, which is what that string is
        // legitimately for. A latch left set by the answered question would instead hold this caller
        // open until the bound below fired.
        var continuation = _manager.SendMessageAsync(agentId, "anything else?", runInBackground: false);
        var continued = await continuation.WaitAsync(TimeSpan.FromSeconds(10));
        continued.Should().Be(
            "(no text response)",
            "the parked latch spans only the wait for an answered question's result; a later run with "
                + "nothing to say is genuinely terminal and must not leave the caller blocked");
    }

    [Fact]
    public async Task SpawnAsync_Foreground_WhenASecondZeroTurnRunFollows_StopsAbsorbingAndSettles()
    {
        // Arrange (#262, the latch's bound): absorbing a text-free completion is only safe if the latch
        // is CONSUMED by the completion that absorbs it. A latch that stayed armed would absorb the next
        // text-free run too, and the one after that — the caller never settles and the permit never comes
        // back, for as long as the agent keeps producing nothing. The sibling tests cannot see this: each
        // has only ONE run for the latch to swallow, so an unconsumed latch and a consumed one behave
        // identically. This is the shape that separates them: two zero-turn runs in a row, where only the
        // first may be absorbed.
        var askArgs = AskColorArgs();

        var providerCalls = 0;
        _subAgentMock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>((_, opts, _) =>
            {
                List<IMessage> reply = Interlocked.Increment(ref providerCalls) switch
                {
                    1 => [StreamingAskUserQuestionCall("tc_color", askArgs, opts.GenerationId)],

                    // Every run after the question is a zero-turn run: the resolution-triggered one the
                    // latch legitimately absorbs, and then a second one it must not.
                    _ => [],
                };

                return Task.FromResult(ToAsyncEnumerable(reply));
            });

        _manager = CreateManager(maxConcurrent: 1);

        var foregroundTask = _manager.SpawnAsync(
            "test-agent", "Pick a color", name: "color-agent", runInBackground: false);

        MultiTurnAgentLoop? loop = null;
        IReadOnlyList<DeferredToolCallInfo> deferred = [];
        await Wait.UntilAsync(
            async () =>
            {
                if (_manager!.TryGetAgent("color-agent", out var agent) && agent is MultiTurnAgentLoop l)
                {
                    loop = l;
                    deferred = await l.GetDeferredToolCallsAsync();
                    return deferred.Any(d => d.ToolCallId == "tc_color");
                }

                return false;
            },
            "the child parked tc_color",
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(50),
            observed: () => loop is null
                ? "no MultiTurnAgentLoop registered yet for 'color-agent'"
                : $"deferred tool calls: [{string.Join(", ", deferred.Select(d => d.ToolCallId))}], "
                    + $"provider calls: {Volatile.Read(ref providerCalls)}");

        var agentId = _manager!.ListAgents().Single(a => a.Name == "color-agent").AgentId;

        (await loop!.TryResolveToolCallAsync("tc_color", "Red"))
            .Should().Be(ResolveToolCallOutcome.Resolved);

        // The resolution-triggered run is the one the latch is for. Wait for it to have happened and be
        // absorbed — the caller still blocked is what "absorbed" looks like from here.
        await Wait.UntilAsync(
            () => Volatile.Read(ref providerCalls) >= 2,
            "the resolution-triggered run reached the provider",
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(50),
            observed: () => $"provider calls: {Volatile.Read(ref providerCalls)}");

        // Act: a second run with nothing to say. The latch is spent, so this one is terminal.
        // Both this caller and the still-outstanding spawn await the same pending completion.
        var continuation = _manager.SendMessageAsync(agentId, "anything else?", runInBackground: false);

        // Assert: it settles rather than being swallowed like its predecessor.
        var continued = await continuation.WaitAsync(TimeSpan.FromSeconds(10));
        continued.Should().Be(
            "(no text response)",
            "the latch entitles exactly one text-free completion to be absorbed; leaving it armed makes "
                + "every later text-free run non-terminal and blocks the caller indefinitely");

        var result = await foregroundTask.WaitAsync(TimeSpan.FromSeconds(10));
        result.Should().Be("(no text response)", "the original caller settles from that same completion");
    }

    [Fact]
    public async Task SpawnAsync_Foreground_WhenTheAnsweredRunOnlyThinks_SettlesAndReleasesThePermit()
    {
        // Arrange (#262, the dangerous edge): keeping a post-answer run non-terminal is what closes the
        // race, but applied to EVERY text-free completion it converts the bug into something worse. If the
        // answer-triggered run is the sub-agent's last word and it produces only thinking, there is no
        // later run to settle the caller: the foreground task never completes, the agent never leaves
        // Running, and its concurrency permit is never released — permanently shrinking
        // MaxConcurrentSubAgents. That failure is silent, because this branch deliberately raises no
        // descendant-question notification for anyone to notice.
        //
        // The discriminator is whether the run reached the model at all. A thinking-only turn DID, so it
        // is genuinely finished and must settle. Nothing nudges the agent here — production has no such
        // nudge either, which is exactly why the sibling test's manual continuation must not be what
        // rescues this shape.
        var askArgs = AskColorArgs();

        var providerCalls = 0;
        _subAgentMock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>((_, opts, _) =>
            {
                List<IMessage> reply = Interlocked.Increment(ref providerCalls) switch
                {
                    1 => [StreamingAskUserQuestionCall("tc_color", askArgs, opts.GenerationId)],

                    // The answered run, and the agent's last word: it called the model, which had nothing
                    // worth returning. Terminal — not something to wait on.
                    _ => [new TextMessage { Text = "mulling it over", Role = Role.Assistant, IsThinking = true }],
                };

                return Task.FromResult(ToAsyncEnumerable(reply));
            });

        // maxConcurrent: 1 so the permit assertion at the end is meaningful — a leaked permit makes the
        // second spawn impossible, which is the whole cost of getting this wrong.
        _manager = CreateManager(maxConcurrent: 1);

        var foregroundTask = _manager.SpawnAsync(
            "test-agent", "Pick a color", name: "color-agent", runInBackground: false);

        MultiTurnAgentLoop? loop = null;
        IReadOnlyList<DeferredToolCallInfo> deferred = [];
        await Wait.UntilAsync(
            async () =>
            {
                if (_manager!.TryGetAgent("color-agent", out var agent) && agent is MultiTurnAgentLoop l)
                {
                    loop = l;
                    deferred = await l.GetDeferredToolCallsAsync();
                    return deferred.Any(d => d.ToolCallId == "tc_color");
                }

                return false;
            },
            "the child registered as a MultiTurnAgentLoop and parked tc_color",
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(50),
            observed: () => loop is null
                ? "no MultiTurnAgentLoop registered yet for 'color-agent'"
                : $"deferred tool calls: [{string.Join(", ", deferred.Select(d => d.ToolCallId))}], "
                    + $"provider calls: {Volatile.Read(ref providerCalls)}");

        // Act: answer, then do nothing at all.
        var outcome = await loop!.TryResolveToolCallAsync("tc_color", "Red");
        outcome.Should().Be(ResolveToolCallOutcome.Resolved);

        // Assert: the caller settles on its own. Before the bound existed this timed out.
        var result = await foregroundTask.WaitAsync(TimeSpan.FromSeconds(10));
        result.Should().Be(
            "(no text response)",
            "a run that called the model and produced only thinking is genuinely finished, so it must "
                + "settle the caller rather than be mistaken for the gap before an answer's real text");

        // And the permit came back: with maxConcurrent 1 a leaked permit would make this spawn hang.
        var second = await _manager
            .SpawnAsync("test-agent", "second task", name: "second-agent", runInBackground: false)
            .WaitAsync(TimeSpan.FromSeconds(10));
        second.Should().NotBeNull(
            "the absorbed-run branch holds the concurrency permit, so a completion it wrongly swallowed "
                + "would starve every later sub-agent");
    }

    [Fact]
    public async Task SpawnAsync_Foreground_WhenAnsweredBeforeTheMonitorClassifies_StillSettlesWithTheRealAnswer()
    {
        // Arrange (#262, the production ordering): the sibling test waits for the descendant-question
        // notification before answering, which proves the monitor had already classified the parked run —
        // the SAFE ordering. The race the issue describes is the other one: the answer lands BEFORE the
        // monitor classifies the parked run's completion, so the resolution empties the deferred-call
        // registry before the probe reads it. The probe then reports "no question pending" for a run that
        // very much did park one, and the latch is the only thing that still knows better.
        //
        // This ordering is FORCED, not raced. Resolving as soon as the registry shows the call — the
        // obvious way to write this — loses the race nearly every time: the registry is populated before
        // the deferred placeholder is even published, so the monitor is free to classify first and the
        // test then exercises the safe path while appearing to prove the dangerous one. The seam below
        // fires inside the exact window the bug lives in, and the assertion at the end fails loudly if it
        // never fired, so this test cannot silently go vacuous.
        var askArgs = AskColorArgs();

        const string RealAnswer = "Final answer: chose Blue.";
        var providerCalls = 0;
        _subAgentMock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>((_, opts, _) =>
            {
                List<IMessage> reply = Interlocked.Increment(ref providerCalls) switch
                {
                    1 => [StreamingAskUserQuestionCall("tc_color", askArgs, opts.GenerationId)],
                    _ => [new TextMessage { Text = RealAnswer, Role = Role.Assistant }],
                };

                return Task.FromResult(ToAsyncEnumerable(reply));
            });

        _manager = CreateManager(maxConcurrent: 1);

        // Resolve the question INSIDE the classification window, exactly once, and only for a completion
        // that still has the call parked. Recording the outcome rather than asserting on it here keeps the
        // failure attributable: an exception thrown on the monitor's thread would surface as a timeout
        // somewhere else entirely.
        ResolveToolCallOutcome? resolvedInsideWindow = null;
        var hookFired = 0;
        _manager.BeforeClassifyingRunCompletionForTest = async (state, _) =>
        {
            if (state.Agent is not MultiTurnAgentLoop l
                || Interlocked.Exchange(ref hookFired, 1) != 0)
            {
                return;
            }

            resolvedInsideWindow = await l.TryResolveToolCallAsync("tc_color", "Blue");
        };

        var foregroundTask = _manager.SpawnAsync(
            "test-agent", "Pick a color", name: "color-agent", runInBackground: false);

        // Assert: the caller gets the answer, not the placeholder. Without the `latchedThisRun` arm this
        // fails here with #262's exact signature — the parked run emitted a tool call, so the "did the
        // model speak" veto calls its own completion terminal the moment the probe comes back empty.
        var result = await foregroundTask.WaitAsync(TimeSpan.FromSeconds(10));
        result.Should().Be(
            RealAnswer,
            "the latch survives the resolution, so the monitor still knows the run was parked even when "
                + "the answer emptied the deferred-call registry before it looked");

        // Non-vacuity: prove the dangerous ordering is what actually ran. A pass with the hook unfired, or
        // with nothing there to resolve, would mean the safe ordering carried the test instead.
        Volatile.Read(ref hookFired).Should().Be(
            1, "the classification seam must have fired, or this test proved nothing about the race");
        resolvedInsideWindow.Should().Be(
            ResolveToolCallOutcome.Resolved,
            "the answer must have been applied INSIDE the classification window — that is the whole "
                + "ordering under test; anything else means the call was already gone and the monitor "
                + "had classified the parked run first");
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
        await Wait.UntilAsync(
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
            "the sub-agent reported completed, so the restart acts on a finished run",
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
        await Wait.UntilAsync(
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
            "the sub-agent injected into reported completed",
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
        await Wait.UntilAsync(
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
            "the parent received the wrapped sub-agent error",
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
        await Wait.UntilAsync(
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
            "the parent received its relay before the recorded invocations are cleared",
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

        await Wait.UntilAsync(
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
            "the parent received its post-completion relay",
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

    [Fact]
    public async Task ARestartThatFailed_LeavesTheSubAgentRestartable_RatherThanWiredToItsDeadLoop()
    {
        // A failed restart deliberately keeps this sub-agent REGISTERED — it is a pre-existing agent whose
        // restart attempt failed, not a partially-spawned one to roll back. But that cleanup disposes both
        // the epoch CTS and the live loop, so unless each is re-armed the agent is registered and
        // advertised while being permanently unable to accept another message: the next restart cancels a
        // disposed CTS, and past that sends into a disposed loop. Both throw.
        SetupSubAgentResponse([new TextMessage { Text = "done", Role = Role.Assistant }]);

        var store = new RecoveryFaultingConversationStore();
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["test-agent"] = new SubAgentTemplate
                {
                    SystemPrompt = "You are a test agent.",
                    AgentFactory = () => _subAgentMock.Object,
                    // ONE store instance across rebuilds: a restart only disposes the previous store when
                    // the replacement is a different instance, so this keeps the fault switch observable.
                    ConversationStoreFactory = _ => store,
                },
            },
            MaxConcurrentSubAgents = 5,
        };
        _manager = new SubAgentManager(
            parentAgent: _parentMock.Object,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: options,
            source: new MutableSubAgentTemplateSource(options.Templates));

        var spawnJson = await _manager.SpawnAsync("test-agent", "first task", runInBackground: true);
        using var spawnDoc = JsonDocument.Parse(spawnJson);
        var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;
        _ = await _manager.ObserveCompletionAsync(agentId, CancellationToken.None);

        // Fail a restart at history recovery — the first step that runs AFTER the epoch CTS was disposed
        // and BEFORE the replacement run is armed, and the realistic way a real restart fails there.
        store.FailRecovery = true;
        var failing = () => _manager.SendMessageAsync(agentId, "second task", runInBackground: true);
        (await failing.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Be(RecoveryFaultingConversationStore.FaultMessage);

        // The next restart must rebuild the pipeline and run it, not drive the corpse of the last one.
        store.FailRecovery = false;
        var resumeJson = await _manager.SendMessageAsync(agentId, "third task", runInBackground: true);
        using var resumeDoc = JsonDocument.Parse(resumeJson);
        resumeDoc.RootElement.GetProperty("status").GetString().Should().Be("resumed");

        await Wait.UntilAsync(
            () =>
            {
                using var doc = JsonDocument.Parse(_manager!.Peek(agentId));
                return doc.RootElement.GetProperty("status").GetString() == "completed";
            },
            "the sub-agent reported completed",
            TimeSpan.FromSeconds(10));
    }

    #region Helpers

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

    /// <summary>
    /// An in-memory store whose history load can be armed to fail, standing in for a store/IO fault
    /// during a restart's <c>RecoverAsync</c>. Everything else forwards to a real in-memory store so the
    /// sub-agent's own runs behave normally either side of the injected failure.
    /// </summary>
    private sealed class RecoveryFaultingConversationStore : IConversationStore
    {
        public const string FaultMessage = "store unavailable during recovery";

        private readonly InMemoryConversationStore _inner = new();

        /// <summary>While true, <see cref="LoadMetadataAsync"/> throws instead of reading.</summary>
        public bool FailRecovery { get; set; }

        public Task<ThreadMetadata?> LoadMetadataAsync(string threadId, CancellationToken ct = default) =>
            FailRecovery
                ? Task.FromException<ThreadMetadata?>(new InvalidOperationException(FaultMessage))
                : _inner.LoadMetadataAsync(threadId, ct);

        public Task AppendMessagesAsync(
            string threadId,
            IReadOnlyList<PersistedMessage> messages,
            CancellationToken ct = default) => _inner.AppendMessagesAsync(threadId, messages, ct);

        public Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(
            string threadId,
            CancellationToken ct = default) => _inner.LoadMessagesAsync(threadId, ct);

        public Task ReplaceMessageAsync(
            string threadId,
            PersistedMessage replacement,
            CancellationToken ct = default) => _inner.ReplaceMessageAsync(threadId, replacement, ct);

        public Task SaveMetadataAsync(
            string threadId,
            ThreadMetadata metadata,
            CancellationToken ct = default) => _inner.SaveMetadataAsync(threadId, metadata, ct);

        public Task UpdateMetadataAsync(
            string threadId,
            Func<ThreadMetadata?, ThreadMetadata> update,
            CancellationToken ct = default) => _inner.UpdateMetadataAsync(threadId, update, ct);

        public Task DeleteThreadAsync(string threadId, CancellationToken ct = default) =>
            _inner.DeleteThreadAsync(threadId, ct);

        public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
            int limit = 50,
            int offset = 0,
            CancellationToken ct = default) => _inner.ListThreadsAsync(limit, offset, ct);
    }

    #endregion
}
