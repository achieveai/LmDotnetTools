using System.Runtime.CompilerServices;
using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Every path by which a tool result enters <see cref="MultiTurnAgentLoop"/> history is bounded by
/// the loop's <see cref="MultiTurnAgentBase.ToolResultLimits"/> (#694): the handler-exception
/// result, a deferred resolution, and a result the provider streams back for a server-side tool it
/// executed itself. In each case the stored copy, the copy the next provider request carries, and
/// (where the loop publishes it) the published copy are within the cap, end with the marker and are
/// flagged <see cref="ToolCallResultMessage.IsTruncated"/>.
/// </summary>
public class ToolResultBoundingTests
{
    private const int Cap = 512;
    private const string ThreadId = "bounding-thread";

    private readonly Mock<IStreamingAgent> _mockAgent = new();
    private readonly Mock<ILogger<MultiTurnAgentLoop>> _loggerMock = new();

    private static readonly ToolResultLimits Limits = new() { MaxResultBytes = Cap };

    [Fact]
    public async Task HandlerException_ResultIsBoundedInPublishedAndPersistedHistory()
    {
        var toolCall = new ToolCallMessage
        {
            FunctionName = "explode",
            FunctionArgs = "{}",
            ToolCallId = "tc_explode",
            Role = Role.Assistant,
        };
        var oversizedMessage = new string('e', 20_000);

        SetupTwoTurnResponse(toolCall, new TextMessage { Text = "sorry", Role = Role.Assistant }, out var requests);

        var registry = new FunctionRegistry();
        registry.AddFunction(
            BuildContract("explode"),
            (_, _, _) => throw new InvalidOperationException(oversizedMessage)
        );

        var store = new InMemoryConversationStore();
        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            registry,
            ThreadId,
            store: store,
            logger: _loggerMock.Object
        )
        {
            ToolResultLimits = Limits,
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _ = loop.RunAsync(cts.Token);

        var published = new List<IMessage>();
        await foreach (
            var msg in loop.ExecuteRunAsync(
                new UserInput([new TextMessage { Text = "go", Role = Role.User }]),
                cts.Token
            )
        )
        {
            published.Add(msg);
        }

        var publishedResult = published.OfType<ToolCallResultMessage>().Should().ContainSingle().Subject;
        AssertBounded(publishedResult, "published");
        publishedResult.IsError.Should().BeTrue();

        var persisted = await WaitForPersistedToolResultAsync(store, "tc_explode", cts.Token);
        AssertBounded(persisted, "persisted");

        // The second provider request replays history: it carries the bounded text, not the original.
        requests.Count.Should().Be(2);
        FindResultText(requests[1], "tc_explode").Should().Be(publishedResult.Result);
    }

    [Fact]
    public async Task DeferredResolution_IsBoundedBeforeFingerprintHistoryAndPublish_AndStaysIdempotent()
    {
        var toolCall = new ToolCallMessage
        {
            FunctionName = "long_op",
            FunctionArgs = "{}",
            ToolCallId = "tc_deferred",
            Role = Role.Assistant,
        };
        var oversized = new string('d', 20_000);

        SetupTwoTurnResponse(toolCall, new TextMessage { Text = "done", Role = Role.Assistant }, out var requests);

        var registry = new FunctionRegistry();
        registry.AddFunction(
            BuildContract("long_op"),
            (_, _, _) => Task.FromResult<ToolHandlerResult>(new ToolHandlerResult.Deferred())
        );

        var store = new InMemoryConversationStore();
        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            registry,
            ThreadId,
            store: store,
            logger: _loggerMock.Object
        )
        {
            ToolResultLimits = Limits,
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _ = loop.RunAsync(cts.Token);

        var published = new List<IMessage>();
        var runCompleted = new[]
        {
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var completedRuns = 0;
        var drain = LoopSubscription.StartDraining(
            loop,
            msg =>
            {
                lock (published)
                {
                    published.Add(msg);
                }

                if (msg is RunCompletedMessage && completedRuns < runCompleted.Length)
                {
                    _ = runCompleted[completedRuns++].TrySetResult(true);
                }
            },
            cts.Token
        );

        await loop.SendAsync([new TextMessage { Text = "start", Role = Role.User }]);
        await drain.WaitAsync(runCompleted[0].Task, TimeSpan.FromSeconds(10));

        await loop.ResolveToolCallAsync("tc_deferred", oversized, ct: cts.Token);
        await drain.WaitAsync(runCompleted[1].Task, TimeSpan.FromSeconds(10));

        List<IMessage> snapshot;
        lock (published)
        {
            snapshot = [.. published];
        }

        var resolved = snapshot
            .OfType<ToolCallResultMessage>()
            .Where(m => m.ToolCallId == "tc_deferred" && !m.IsDeferred)
            .Should()
            .ContainSingle()
            .Subject;
        AssertBounded(resolved, "published");

        var persisted = await WaitForPersistedToolResultAsync(store, "tc_deferred", cts.Token, m => !m.IsDeferred);
        AssertBounded(persisted, "persisted");

        requests.Count.Should().Be(2);
        FindResultText(requests[1], "tc_deferred").Should().Be(resolved.Result);

        // A byte-equal redelivery of the same oversized payload bounds to the same canonical text,
        // so it is a duplicate — no conflict, no exception, no third run.
        var redelivery = await loop.TryResolveToolCallAsync("tc_deferred", oversized, ct: cts.Token);
        redelivery.Should().Be(ResolveToolCallOutcome.Duplicate);
        var act = () => loop.ResolveToolCallAsync("tc_deferred", oversized, ct: cts.Token);
        await act.Should().NotThrowAsync();
        requests.Count.Should().Be(2);
    }

    [Fact]
    public async Task ProviderServerToolResult_IsBoundedAtHistoryIngress()
    {
        // A result the provider produced for a tool it ran itself (Anthropic web_search, code
        // execution, ...) has no executor on our side to bound it. It reaches history through the
        // same AddToHistory ingress whether the provider agent streamed it or mapped a non-streaming
        // response — the loop only ever sees an IMessage sequence — so this covers both.
        var oversized = "{\"results\":\"" + new string('w', 20_000) + "\"}";
        var serverResult = new ToolCallResultMessage
        {
            ToolCallId = "srvtoolu_1",
            ToolName = "web_search",
            Result = oversized,
            ExecutionTarget = ExecutionTarget.ProviderServer,
            Role = Role.Assistant,
        };

        var requests = new List<IReadOnlyList<IMessage>>();
        _mockAgent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (msgs, _, _) =>
                {
                    requests.Add([.. msgs]);
                    return Task.FromResult(
                        requests.Count == 1
                            ? ToAsyncEnumerable([
                                serverResult,
                                new TextMessage { Text = "searched", Role = Role.Assistant },
                            ])
                            : ToAsyncEnumerable([new TextMessage { Text = "again", Role = Role.Assistant }])
                    );
                }
            );

        var store = new InMemoryConversationStore();
        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            new FunctionRegistry(),
            ThreadId,
            store: store,
            logger: _loggerMock.Object
        )
        {
            ToolResultLimits = Limits,
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _ = loop.RunAsync(cts.Token);

        await foreach (
            var _ in loop.ExecuteRunAsync(
                new UserInput([new TextMessage { Text = "search", Role = Role.User }]),
                cts.Token
            )
        ) { }

        var persisted = await WaitForPersistedToolResultAsync(store, "srvtoolu_1", cts.Token);
        AssertBounded(persisted, "persisted");
        persisted.ExecutionTarget.Should().Be(ExecutionTarget.ProviderServer);

        await foreach (
            var _ in loop.ExecuteRunAsync(
                new UserInput([new TextMessage { Text = "again", Role = Role.User }]),
                cts.Token
            )
        ) { }

        requests.Count.Should().Be(2);
        FindResultText(requests[1], "srvtoolu_1").Should().Be(persisted.Result);
    }

    private static void AssertBounded(ToolCallResultMessage message, string where)
    {
        message.IsTruncated.Should().BeTrue($"the {where} copy must be flagged");
        Encoding.UTF8.GetByteCount(message.Result).Should().BeLessThanOrEqualTo(Cap, $"the {where} copy is capped");
        message.Result.Should().Contain(ToolResultLimits.TruncationMarkerPrefix);
        message.Result.Should().EndWith(" bytes]");
    }

    /// <summary>
    /// The upstream MessageTransformationMiddleware folds singular results into a plural
    /// ToolsCallResultMessage before the provider sees them, so look in both shapes.
    /// </summary>
    private static string? FindResultText(IReadOnlyList<IMessage> request, string toolCallId)
    {
        var singular = request.OfType<ToolCallResultMessage>().FirstOrDefault(m => m.ToolCallId == toolCallId);
        if (singular != null)
        {
            return singular.Result;
        }

        return request
            .OfType<ToolsCallResultMessage>()
            .SelectMany(m => m.ToolCallResults)
            .FirstOrDefault(r => r.ToolCallId == toolCallId)
            .Result;
    }

    /// <summary>Persistence is fire-and-forget, so poll the store for the row.</summary>
    private static async Task<ToolCallResultMessage> WaitForPersistedToolResultAsync(
        InMemoryConversationStore store,
        string toolCallId,
        CancellationToken ct,
        Func<ToolCallResultMessage, bool>? accept = null
    )
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            var persisted = await store.LoadMessagesAsync(ThreadId, ct);
            var match = MessagePersistenceConverter
                .FromPersistedMessages(persisted)
                .OfType<ToolCallResultMessage>()
                .LastOrDefault(m => m.ToolCallId == toolCallId && (accept == null || accept(m)));
            if (match != null)
            {
                return match;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Tool result '{toolCallId}' was never persisted.");
            }

            await Task.Delay(20, ct);
        }
    }

    private void SetupTwoTurnResponse(
        IMessage firstTurn,
        IMessage secondTurn,
        out List<IReadOnlyList<IMessage>> requests
    )
    {
        var captured = new List<IReadOnlyList<IMessage>>();
        requests = captured;
        _mockAgent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (msgs, _, _) =>
                {
                    captured.Add([.. msgs]);
                    return Task.FromResult(ToAsyncEnumerable([captured.Count == 1 ? firstTurn : secondTurn]));
                }
            );
    }

    private static FunctionContract BuildContract(string name) =>
        new()
        {
            Name = name,
            Description = $"Test contract for {name}",
            Parameters = [],
        };

    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
        IEnumerable<IMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        foreach (var msg in messages)
        {
            ct.ThrowIfCancellationRequested();
            yield return msg;
            await Task.Yield();
        }
    }
}
