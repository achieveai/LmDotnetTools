using System.Collections.Concurrent;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using FluentAssertions;
using LmMultiTurn.Tests.DualLayer;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// <see cref="IInputTap"/> sits ahead of both send paths. A taken input is never run by the agent it
/// was offered to, and the caller still gets a receipt.
/// </summary>
public class InputTapTests
{
    private sealed class TakeWhen(Func<UserInput, bool> take) : IInputTap
    {
        public ConcurrentQueue<UserInput> Seen { get; } = new();

        public ValueTask<InputTapDecision> OnInputAsync(UserInput input, CancellationToken cancellationToken)
        {
            Seen.Enqueue(input);
            return ValueTask.FromResult(take(input) ? InputTapDecision.Taken : InputTapDecision.Queue);
        }
    }

    private static TextMessage User(string text) => new() { Role = Role.User, Text = text };

    private static string? LastUserText(IReadOnlyList<IMessage> request) =>
        request.LastOrDefault(m => m is TextMessage { Role: Role.User }) is TextMessage t ? t.Text : null;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_taken_input_is_never_run_and_a_queued_one_still_is(bool viaTrySend)
    {
        var model = new DualLayerAgentTests.ScriptedAgent(_ =>
            [new TextMessage { Role = Role.Assistant, Text = "ok" }]
        );
        var tap = new TakeWhen(input => LastUserText(input.Messages) == "take me");
        await using var loop = new MultiTurnAgentLoop(
            model,
            new FunctionRegistry(),
            "tap-thread",
            includeAskUserQuestionTool: false,
            includeNotifyClientTool: false
        )
        {
            InputTap = tap,
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _ = loop.RunAsync(cts.Token);

        // The taken one first, then a queued one; the run that follows proves the order was honoured.
        var taken = viaTrySend
            ? await loop.TrySendAsync([User("take me")], ct: cts.Token)
            : await loop.SendAsync([User("take me")], ct: cts.Token);
        var queued = viaTrySend
            ? await loop.TrySendAsync([User("run me")], ct: cts.Token)
            : await loop.SendAsync([User("run me")], ct: cts.Token);

        taken.Should().NotBeNull();
        taken!.ReceiptId.Should().NotBeNullOrEmpty();
        queued.Should().NotBeNull();
        while (model.Requests.IsEmpty)
        {
            await Task.Delay(20, cts.Token);
        }

        tap.Seen.Select(i => LastUserText(i.Messages)).Should().Equal("take me", "run me");
        model.Requests.Should().HaveCount(1);
        model
            .Requests.Single()
            .OfType<TextMessage>()
            .Where(m => m.Role == Role.User)
            .Select(m => m.Text)
            .Should()
            .Equal(["run me"], "the taken input must not reach the model, before or with the queued one");
    }

    [Fact]
    public async Task A_throwing_tap_fails_the_send_with_nothing_queued()
    {
        var model = new DualLayerAgentTests.ScriptedAgent(_ =>
            [new TextMessage { Role = Role.Assistant, Text = "ok" }]
        );
        var tap = new TakeWhen(_ => throw new InvalidOperationException("tap broke"));
        await using var loop = new MultiTurnAgentLoop(
            model,
            new FunctionRegistry(),
            "tap-thread-2",
            includeAskUserQuestionTool: false,
            includeNotifyClientTool: false
        )
        {
            InputTap = tap,
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _ = loop.RunAsync(cts.Token);

        var send = async () => await loop.SendAsync([User("hello")], ct: cts.Token);

        await send.Should().ThrowAsync<InvalidOperationException>().WithMessage("tap broke");
        loop.InputTap = null;
        _ = await loop.SendAsync([User("after")], ct: cts.Token);
        while (model.Requests.IsEmpty)
        {
            await Task.Delay(20, cts.Token);
        }

        model
            .Requests.Single()
            .OfType<TextMessage>()
            .Where(m => m.Role == Role.User)
            .Select(m => m.Text)
            .Should()
            .Equal("after");
    }
}
