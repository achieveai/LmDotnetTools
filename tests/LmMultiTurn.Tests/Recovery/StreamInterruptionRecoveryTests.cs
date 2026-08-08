using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.Recovery;

/// <summary>
/// Behaviour-level tests for automatic recovery from a provider stream that ends mid-reply.
/// </summary>
/// <remarks>
/// These drive the real loop end to end rather than the recovery helpers in isolation, because every
/// property that matters here is a property of the whole turn: what the SECOND request contains, how
/// many times a tool actually ran, and which generation the client is told to discard.
/// </remarks>
public class StreamInterruptionRecoveryTests
{
    private readonly Mock<IStreamingAgent> _mockAgent = new();

    /// <summary>The message list handed to the provider on each attempt, in order.</summary>
    private readonly List<List<IMessage>> _requests = [];

    /// <summary>The generation id each attempt streamed under, in order.</summary>
    private readonly List<string?> _generations = [];

    [Fact]
    public async Task FragmentOnlyInterruption_RetriesOnceUnderANewGenerationAndLeavesNoPartialBehind()
    {
        // Attempt 1 streams two text fragments and then the connection dies. Nothing was ever
        // finalized, so there is nothing to keep and nothing to continue from.
        ScriptProvider((attempt, ct) => attempt == 1
            ? Emit(
                [
                    new TextUpdateMessage { Text = "The answer ", Role = Role.Assistant },
                    new TextUpdateMessage { Text = "is ", Role = Role.Assistant },
                ],
                ResponseEnded(),
                ct)
            : Emit([new TextMessage { Text = "The answer is 42.", Role = Role.Assistant }], null, ct));

        var messages = await RunAsync("fragment-only-thread");

        _requests.Should().HaveCount(2, "a fragment-only interruption is retried exactly once");
        _generations[1].Should().NotBe(_generations[0], "the replacement attempt must not collide with the abandoned one on the client merge key");

        // The retry is the ORIGINAL request: the abandoned attempt contributed nothing to it.
        _requests[1].Should().BeEquivalentTo(_requests[0], "a retry of an empty attempt reissues the original request");
        _requests[1].OfType<TextUpdateMessage>().Should().BeEmpty();
        _requests[1].OfType<TextMessage>().Where(m => m.Role == Role.Assistant).Should().BeEmpty(
            "no partial assistant output may be persisted from an attempt that never finished");

        messages.OfType<GenerationAbandonedMessage>().Should().ContainSingle()
            .Which.GenerationId.Should().Be(_generations[0], "the client is told which generation's unfinalized blocks to drop");

        messages.OfType<TextMessage>().Where(m => m.Role == Role.Assistant)
            .Should().ContainSingle().Which.Text.Should().Be("The answer is 42.");
        messages.OfType<RunCompletedMessage>().Should().ContainSingle().Which.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task InterruptionAfterCompletedOutput_ContinuesInternallyWithoutAVisibleNewInput()
    {
        ScriptProvider((attempt, ct) => attempt == 1
            ? Emit(
                [
                    new TextMessage { Text = "Step one is done.", Role = Role.Assistant },
                    new TextUpdateMessage { Text = "Step two ", Role = Role.Assistant },
                ],
                ResponseEnded(),
                ct)
            : Emit([new TextMessage { Text = "Step two is done.", Role = Role.Assistant }], null, ct));

        var messages = await RunAsync("continue-thread");

        _requests.Should().HaveCount(2);

        // The completed message survives into the continuation request; the abandoned fragment does not.
        _requests[1].OfType<TextMessage>().Should().Contain(m => m.Text == "Step one is done.");
        _requests[1].OfType<TextUpdateMessage>().Should().BeEmpty();

        // The provider is told to continue — but only in this request.
        _requests[1].OfType<TextMessage>()
            .Where(m => m.Text == MultiTurnAgentLoop.InterruptedTurnContinuationInstruction)
            .Should().ContainSingle("the continuation is instructed internally");
        _requests[0].OfType<TextMessage>()
            .Where(m => m.Text == MultiTurnAgentLoop.InterruptedTurnContinuationInstruction)
            .Should().BeEmpty();
        messages.OfType<TextMessage>()
            .Where(m => m.Text == MultiTurnAgentLoop.InterruptedTurnContinuationInstruction)
            .Should().BeEmpty("the instruction is never rendered to the client as a user bubble");

        messages.OfType<RunCompletedMessage>().Should().ContainSingle().Which.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task InterruptionAfterACompletedToolCall_SettlesItOnceAndNeverReExecutesIt()
    {
        var toolCall = new ToolCallMessage
        {
            FunctionName = "get_weather",
            FunctionArgs = "{\"location\":\"Seattle\"}",
            ToolCallId = "call_recovered",
            Role = Role.Assistant,
        };

        // The stream dies AFTER emitting a complete tool call, so its execution is already in flight
        // when recovery begins — the exact shape that can double a side effect if mishandled.
        ScriptProvider((attempt, ct) => attempt == 1
            ? Emit([toolCall], ResponseEnded(), ct)
            : Emit([new TextMessage { Text = "It is 72F in Seattle.", Role = Role.Assistant }], null, ct));

        var executions = 0;
        var registry = new FunctionRegistry();
        registry.AddFunction(
            WeatherContract(),
            (_, _, _) =>
            {
                Interlocked.Increment(ref executions);
                return Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("{\"temperature\":\"72F\"}"));
            });

        var messages = await RunAsync("tool-recovery-thread", registry);

        executions.Should().Be(1, "a completed tool effect must never be executed a second time");
        messages.OfType<ToolCallResultMessage>().Where(m => m.ToolCallId == "call_recovered")
            .Should().ContainSingle("the visible effect is delivered exactly once");

        // The settled result is carried into the continuation, which is what makes re-issuing the
        // call unnecessary rather than merely discouraged. History stores tool results in aggregate
        // form, so read both shapes rather than assuming one.
        ToolResultIds(_requests[1]).Should().Contain("call_recovered");
        messages.OfType<RunCompletedMessage>().Should().ContainSingle().Which.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task AccountingOnlyInterruption_IsAFullRetryRatherThanAContinuation()
    {
        // Usage rides alongside the fragments and is canonical for history purposes, but it delivered
        // no content and ran no effect. Treating it as "completed output" would instruct the model to
        // continue from a reply the user never saw a word of, stranding the turn.
        //
        // Usage comes FIRST and the fragments trail it: the joiner flushes a fragment run into a real
        // TextMessage as soon as a different message type follows it, so fragments-then-usage would
        // deliver genuine content and would not test this at all. Trailing fragments are still inside
        // the builder when the stream dies, so they are never built and never delivered.
        ScriptProvider((attempt, ct) => attempt == 1
            ? Emit(
                [
                    new UsageMessage { Usage = new Usage() },
                    new TextUpdateMessage { Text = "The answer ", Role = Role.Assistant },
                ],
                ResponseEnded(),
                ct)
            : Emit([new TextMessage { Text = "The answer is 42.", Role = Role.Assistant }], null, ct));

        var messages = await RunAsync("accounting-only-thread");

        _requests.Should().HaveCount(2);
        _requests[1].OfType<TextMessage>()
            .Where(m => m.Text == MultiTurnAgentLoop.InterruptedTurnContinuationInstruction)
            .Should().BeEmpty("an attempt that delivered only accounting is retried, not continued");

        messages.OfType<TextMessage>().Where(m => m.Role == Role.Assistant)
            .Should().ContainSingle().Which.Text.Should().Be("The answer is 42.");
        messages.OfType<RunCompletedMessage>().Should().ContainSingle().Which.IsError.Should().BeFalse();
    }

    /// <summary>Every tool call id answered by a result in <paramref name="request"/>, either shape.</summary>
    private static IEnumerable<string?> ToolResultIds(IEnumerable<IMessage> request) =>
        request.SelectMany(m => m switch
        {
            ToolCallResultMessage single => [single.ToolCallId],
            ToolsCallResultMessage aggregate => aggregate.ToolCallResults.Select(r => r.ToolCallId),
            _ => [],
        });

    [Fact]
    public async Task SecondInterruption_FailsTheRunWithAStableClassificationInsteadOfRetryingAgain()
    {
        ScriptProvider((_, ct) => Emit(
            [new TextUpdateMessage { Text = "never finishes", Role = Role.Assistant }],
            ResponseEnded(),
            ct));

        var messages = await RunAsync("double-interrupt-thread");

        _requests.Should().HaveCount(2, "one logical input buys exactly one automatic recovery");

        var completed = messages.OfType<RunCompletedMessage>().Should().ContainSingle().Subject;
        completed.IsError.Should().BeTrue();
        completed.ErrorMessage.Should().Contain("stream_interrupted_after_recovery");
    }

    [Fact]
    public async Task InterruptionWhileCancellationIsRequested_IsNeverRetried()
    {
        using var cts = new CancellationTokenSource();

        // A tool that never finishes. Recovery's first act is an UNCANCELLABLE wait for every tool the
        // attempt dispatched, so entering recovery here would wedge the loop forever — which is what
        // makes "the drain returns at all" a decisive test of the cancellation guard rather than a
        // test that merely happens to pass because everything downstream throws anyway.
        var registry = new FunctionRegistry();
        registry.AddFunction(
            WeatherContract(),
            (_, _, _) => new TaskCompletionSource<ToolHandlerResult>().Task);

        // Byte for byte the same transport failure as the recovered cases — the only difference is
        // that the user asked the run to stop first.
        ScriptProvider((_, ct) => EmitThenCancelAndFail(
            [
                new ToolCallMessage
                {
                    FunctionName = "get_weather",
                    FunctionArgs = "{\"location\":\"Seattle\"}",
                    ToolCallId = "call_hangs",
                    Role = Role.Assistant,
                },
            ],
            cts,
            ct));

        var drain = RunAsync("cancelled-thread", registry, cts);
        var finished = await Task.WhenAny(drain, Task.Delay(TimeSpan.FromSeconds(30)));

        finished.Should().BeSameAs(drain, "a cancelled run must not enter recovery and wait on its tools");
        _requests.Should().HaveCount(1, "cancellation must never be answered with a second provider request");
    }

    [Fact]
    public async Task UninterruptedRun_IsUnaffected()
    {
        ScriptProvider((_, ct) => Emit([new TextMessage { Text = "Hello.", Role = Role.Assistant }], null, ct));

        var messages = await RunAsync("normal-thread");

        _requests.Should().HaveCount(1);
        messages.OfType<GenerationAbandonedMessage>().Should().BeEmpty();
        messages.OfType<TextMessage>().Where(m => m.Role == Role.Assistant)
            .Should().ContainSingle().Which.Text.Should().Be("Hello.");
        messages.OfType<RunCompletedMessage>().Should().ContainSingle().Which.IsError.Should().BeFalse();
    }

    private static HttpIOException ResponseEnded() =>
        new(HttpRequestError.ResponseEnded, "The response ended prematurely.");

    private static FunctionContract WeatherContract() =>
        new()
        {
            Name = "get_weather",
            Description = "Get weather for a location",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "location",
                    ParameterType = JsonSchemaObject.String(),
                    IsRequired = true,
                },
            ],
        };

    /// <summary>
    /// Scripts the provider per attempt, recording the request and generation id of each call.
    /// </summary>
    private void ScriptProvider(Func<int, CancellationToken, IAsyncEnumerable<IMessage>> attemptScript)
    {
        _mockAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>((sent, options, ct) =>
            {
                _requests.Add([.. sent]);
                _generations.Add(options.GenerationId);
                return Task.FromResult(attemptScript(_requests.Count, ct));
            });
    }

    /// <summary>Runs one input to completion and returns everything the subscriber saw.</summary>
    private async Task<List<IMessage>> RunAsync(
        string threadId,
        FunctionRegistry? registry = null,
        CancellationTokenSource? cancellationSource = null)
    {
        using var owned = cancellationSource is null ? new CancellationTokenSource() : null;
        var cts = cancellationSource ?? owned!;

        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            registry ?? new FunctionRegistry(),
            threadId);
        _ = loop.RunAsync(cts.Token);

        var messages = new List<IMessage>();
        try
        {
            await foreach (var msg in loop.ExecuteRunAsync(
                new UserInput([new TextMessage { Text = "Go", Role = Role.User }]),
                cts.Token))
            {
                messages.Add(msg);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected only in the cancellation case; the assertions there are on the call count.
        }

        if (cancellationSource is null)
        {
            await cts.CancelAsync();
        }

        return messages;
    }

    private static async IAsyncEnumerable<IMessage> Emit(
        IEnumerable<IMessage> messages,
        Exception? endWith,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var message in messages)
        {
            ct.ThrowIfCancellationRequested();
            yield return message;
            await Task.Yield();
        }

        if (endWith is not null)
        {
            throw endWith;
        }
    }

    private static async IAsyncEnumerable<IMessage> EmitThenCancelAndFail(
        IEnumerable<IMessage> messages,
        CancellationTokenSource cts,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var message in messages)
        {
            yield return message;
            await Task.Yield();
        }

        await cts.CancelAsync();
        _ = ct;
        throw ResponseEnded();
    }
}
