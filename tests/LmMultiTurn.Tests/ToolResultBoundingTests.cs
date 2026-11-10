using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
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
/// (where the loop publishes it) the published copy are within the cap, carry the marker and are
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
        resolved.OriginalBytes.Should().Be(Encoding.UTF8.GetByteCount(oversized));

        var persisted = await WaitForPersistedToolResultAsync(store, "tc_deferred", cts.Token, m => !m.IsDeferred);
        AssertBounded(persisted, "persisted");
        persisted.OriginalBytes.Should().Be(resolved.OriginalBytes);

        requests.Count.Should().Be(2);
        FindResultText(requests[1], "tc_deferred").Should().Be(resolved.Result);
        FindOriginalBytes(requests[1], "tc_deferred").Should().Be(resolved.OriginalBytes);

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
        persisted.OriginalBytes.Should().Be(Encoding.UTF8.GetByteCount(oversized));

        await foreach (
            var _ in loop.ExecuteRunAsync(
                new UserInput([new TextMessage { Text = "again", Role = Role.User }]),
                cts.Token
            )
        ) { }

        requests.Count.Should().Be(2);
        FindResultText(requests[1], "srvtoolu_1").Should().Be(persisted.Result);
        FindOriginalBytes(requests[1], "srvtoolu_1").Should().Be(persisted.OriginalBytes);
    }

    [Fact]
    public async Task LargeToolOutput_IsSavedAndOnlyThePreviewReachesHistoryAndTheProvider()
    {
        var oversized = new string('w', 1_000_000);
        var toolCall = new ToolCallMessage
        {
            FunctionName = "fetch",
            FunctionArgs = "{}",
            ToolCallId = "tc_fetch",
            Role = Role.Assistant,
        };
        SetupTwoTurnResponse(toolCall, new TextMessage { Text = "done", Role = Role.Assistant }, out var requests);

        ToolCallContext? seen = null;
        var registry = new FunctionRegistry();
        registry.AddFunction(
            BuildContract("fetch"),
            (_, context, _) =>
            {
                seen = context;
                return Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText(oversized));
            }
        );

        var saved = new CapturingOutputStore();
        var store = new InMemoryConversationStore();
        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            registry,
            ThreadId,
            store: store,
            logger: _loggerMock.Object
        )
        {
            ToolOutputSpill = new ToolOutputSpillOptions { Store = saved, MaxInlineBytes = 4_000 },
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _ = loop.RunAsync(cts.Token);
        await foreach (
            var _ in loop.ExecuteRunAsync(new UserInput([new TextMessage { Text = "go", Role = Role.User }]), cts.Token)
        ) { }

        seen!.CanSaveLargeOutput.Should().BeTrue();
        saved.Saved.Should().ContainSingle().Which.Should().Be(oversized);
        var persisted = await WaitForPersistedToolResultAsync(store, "tc_fetch", cts.Token);
        Encoding.UTF8.GetByteCount(persisted.Result).Should().BeLessThanOrEqualTo(4_000);
        persisted.Result.Should().Contain(CapturingOutputStore.Path);
        persisted.IsTruncated.Should().BeTrue();
        persisted.OriginalBytes.Should().Be(1_000_000);
        FindResultText(requests[1], "tc_fetch").Should().Be(persisted.Result);
    }

    [Fact]
    public async Task SandboxReadResults_AreNotSavedSoTheModelCanActuallyReadTheFile()
    {
        var content = new string('r', 50_000);
        var toolCall = new ToolCallMessage
        {
            FunctionName = "Read",
            FunctionArgs = "{}",
            ToolCallId = "tc_read",
            Role = Role.Assistant,
        };
        SetupTwoTurnResponse(toolCall, new TextMessage { Text = "done", Role = Role.Assistant }, out _);

        ToolCallContext? seen = null;
        var registry = new FunctionRegistry();
        registry.AddFunction(
            BuildContract("Read"),
            (_, context, _) =>
            {
                seen = context;
                return Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText(content));
            }
        );

        var saved = new CapturingOutputStore();
        var store = new InMemoryConversationStore();
        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            registry,
            ThreadId,
            store: store,
            logger: _loggerMock.Object
        )
        {
            ToolOutputSpill = new ToolOutputSpillOptions { Store = saved, MaxInlineBytes = 4_000 },
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _ = loop.RunAsync(cts.Token);
        await foreach (
            var _ in loop.ExecuteRunAsync(new UserInput([new TextMessage { Text = "go", Role = Role.User }]), cts.Token)
        ) { }

        seen!.CanSaveLargeOutput.Should().BeFalse();
        saved.Saved.Should().BeEmpty();
        var persisted = await WaitForPersistedToolResultAsync(store, "tc_read", cts.Token);
        persisted.Result.Should().Be(content);
        persisted.IsTruncated.Should().BeFalse();
    }

    [Fact]
    public async Task OutputOverASmallerNormalLimit_IsSavedWhole_AndItsPreviewKeepsThePath()
    {
        var oversized = new string('w', 20_000) + "UNIQUE-TAIL";
        var toolCall = new ToolCallMessage
        {
            FunctionName = "fetch",
            FunctionArgs = "{}",
            ToolCallId = "tc_fetch",
            Role = Role.Assistant,
        };
        SetupTwoTurnResponse(toolCall, new TextMessage { Text = "done", Role = Role.Assistant }, out _);

        var registry = new FunctionRegistry();
        registry.AddFunction(
            BuildContract("fetch"),
            (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText(oversized))
        );
        registry.AddFunction(
            BuildContract("Read"),
            (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("unused"))
        );

        var saved = new CapturingOutputStore();
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
            ToolOutputSpill = new ToolOutputSpillOptions { Store = saved, MaxInlineBytes = 4_000 },
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _ = loop.RunAsync(cts.Token);
        await foreach (
            var _ in loop.ExecuteRunAsync(new UserInput([new TextMessage { Text = "go", Role = Role.User }]), cts.Token)
        ) { }

        saved.Saved.Should().ContainSingle().Which.Should().Be(oversized);
        var persisted = await WaitForPersistedToolResultAsync(store, "tc_fetch", cts.Token);
        Encoding.UTF8.GetByteCount(persisted.Result).Should().BeLessThanOrEqualTo(Cap);
        persisted.Result.Should().Contain(CapturingOutputStore.Path);
        persisted.IsTruncated.Should().BeTrue();
    }

    [Fact]
    public async Task RedeliveringOversizedJson_ResolvedBeforeItBecameAnObject_IsADuplicate()
    {
        const string RestoredThread = "bounding-legacy-thread";
        const string RunId = "run-legacy";
        var json = JsonSerializer.Serialize(Enumerable.Range(0, 2_000).ToArray());
        var store = new InMemoryConversationStore();
        await store.AppendMessagesAsync(
            RestoredThread,
            [
                MessagePersistenceConverter.ToPersistedMessage(
                    new ToolCallMessage
                    {
                        ToolCallId = "tc_legacy",
                        FunctionName = "wait",
                        FunctionArgs = "{}",
                        Role = Role.Assistant,
                        GenerationId = "gen-legacy",
                        RunId = RunId,
                    },
                    RestoredThread,
                    RunId
                ),
                MessagePersistenceConverter.ToPersistedMessage(
                    new ToolCallResultMessage
                    {
                        ToolCallId = "tc_legacy",
                        ToolName = "wait",
                        Result = Limits.BoundAsPlainText(json),
                        Role = Role.User,
                        GenerationId = "gen-legacy",
                        RunId = RunId,
                    },
                    RestoredThread,
                    RunId
                ),
            ]
        );
        await store.SaveMetadataAsync(
            RestoredThread,
            new ThreadMetadata
            {
                ThreadId = RestoredThread,
                LatestRunId = RunId,
                LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }
        );
        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            new FunctionRegistry(),
            RestoredThread,
            store: store,
            logger: _loggerMock.Object
        )
        {
            ToolResultLimits = Limits,
        };
        (await loop.RecoverAsync()).Should().BeTrue();

        var same = await loop.TryResolveToolCallAsync("tc_legacy", json);
        var different = await loop.TryResolveToolCallAsync("tc_legacy", json.Replace('1', '2'));

        same.Should().Be(ResolveToolCallOutcome.Duplicate);
        different.Should().Be(ResolveToolCallOutcome.Conflict);
    }

    [Fact]
    public async Task DeliveringOversizedJson_WhoseRecordHoldsTheOldFingerprint_IsNotAConflict()
    {
        var json = JsonSerializer.Serialize(Enumerable.Range(0, 2_000).ToArray());
        await using var loop = await RestoreLegacyDeferralAsync("bounding-legacy-claimed", json);

        var different = await loop.TryResolveToolCallAsync(LegacyCallId, json.Replace('1', '2'));
        var same = await loop.TryResolveToolCallAsync(LegacyCallId, json);

        different.Should().Be(ResolveToolCallOutcome.Conflict);
        same.Should().Be(ResolveToolCallOutcome.Resolved);
    }

    private const string LegacyCallId = "tc_legacy_claimed";

    /// <summary>
    /// A deferred call left by a build from before cut JSON became an object. Its durable record
    /// holds the fingerprint of the old plain cut, and its history row is still the placeholder.
    /// </summary>
    private async Task<MultiTurnAgentLoop> RestoreLegacyDeferralAsync(string threadId, string json)
    {
        var runId = threadId + "-run";
        var store = new InMemoryConversationStore();
        await store.RecordRunStartedAsync(new RunLifecycleState { ThreadId = threadId, RunId = runId });
        _ = await store.RecordDeferredToolCallAsync(
            runId,
            new DeferredToolCallRecord
            {
                ToolCallId = LegacyCallId,
                ToolName = "wait",
                DeferredAt = DateTimeOffset.UtcNow,
            }
        );
        // The fingerprint form is stored, so it outlives upgrades.
        var oldFingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes("0\n" + Limits.BoundAsPlainText(json)))
        );
        _ = await store.TryResolveDeferredToolCallAsync(
            threadId,
            LegacyCallId,
            oldFingerprint,
            childRunId: null,
            DateTimeOffset.UtcNow
        );

        await store.AppendMessagesAsync(
            threadId,
            [
                MessagePersistenceConverter.ToPersistedMessage(
                    new ToolCallMessage
                    {
                        ToolCallId = LegacyCallId,
                        FunctionName = "wait",
                        FunctionArgs = "{}",
                        Role = Role.Assistant,
                        GenerationId = "gen-legacy",
                        RunId = runId,
                    },
                    threadId,
                    runId
                ),
                MessagePersistenceConverter.ToPersistedMessage(
                    new ToolCallResultMessage
                    {
                        ToolCallId = LegacyCallId,
                        ToolName = "wait",
                        Result = string.Empty,
                        IsDeferred = true,
                        DeferredAt = 1_700_000_000_000,
                        Role = Role.User,
                        GenerationId = "gen-legacy",
                        RunId = runId,
                    },
                    threadId,
                    runId
                ),
            ]
        );
        await store.SaveMetadataAsync(
            threadId,
            new ThreadMetadata
            {
                ThreadId = threadId,
                LatestRunId = runId,
                LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }
        );
        var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            new FunctionRegistry(),
            threadId,
            store: store,
            logger: _loggerMock.Object,
            lifecycleServices: new MultiTurnLifecycleServices
            {
                Publisher = NullLifecyclePublisher.Instance,
                LifecycleStore = store,
            }
        )
        {
            ToolResultLimits = Limits,
        };
        (await loop.RecoverAsync()).Should().BeTrue();
        return loop;
    }

    [Fact]
    public async Task SpawnedSubAgent_SavesToItsParentsStore()
    {
        const string TemplateName = "worker";
        var spill = new ToolOutputSpillOptions { Store = new CapturingOutputStore() };
        var subAgentOptions = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                [TemplateName] = new()
                {
                    SystemPrompt = "You are a test agent.",
                    AgentFactory = () => _mockAgent.Object,
                },
            },
        };
        _mockAgent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(ToAsyncEnumerable([new TextMessage { Text = "done", Role = Role.Assistant }]));

        await using var parent = new MultiTurnAgentLoop(
            _mockAgent.Object,
            new FunctionRegistry(),
            ThreadId,
            logger: _loggerMock.Object,
            subAgentOptions: subAgentOptions
        )
        {
            ToolOutputSpill = spill,
        };

        _ = await parent.SubAgentManager!.SpawnAsync(TemplateName, "work", name: "worker");

        parent.SubAgentManager.TryGetAgent("worker", out var child).Should().BeTrue();
        child.Should().BeAssignableTo<MultiTurnAgentBase>().Which.ToolOutputSpill.Should().BeSameAs(spill);
    }

    private sealed class CapturingOutputStore : IToolOutputStore
    {
        public const string Path = "/workspace/.tool-output/abc/output.txt";

        public List<string> Saved { get; } = [];

        public Task<string?> SaveAsync(string content, CancellationToken ct = default)
        {
            Saved.Add(content);
            return Task.FromResult<string?>(Path);
        }
    }

    private static void AssertBounded(ToolCallResultMessage message, string where)
    {
        message.IsTruncated.Should().BeTrue($"the {where} copy must be flagged");
        message
            .OriginalBytes.Should()
            .BeGreaterThanOrEqualTo(20_000, $"the {where} copy must retain the original count");
        Encoding.UTF8.GetByteCount(message.Result).Should().BeLessThanOrEqualTo(Cap, $"the {where} copy is capped");
        message.Result.Should().Contain(ToolResultLimits.TruncationMarkerPrefix);
        if (message.Result.StartsWith('{'))
        {
            // Cut JSON stays parseable (#713), so its marker sits inside the object.
            using var document = JsonDocument.Parse(message.Result);
            document.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
        }
        else
        {
            message.Result.Should().EndWith(" bytes]");
        }
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

    private static int? FindOriginalBytes(IReadOnlyList<IMessage> request, string toolCallId)
    {
        var singular = request.OfType<ToolCallResultMessage>().FirstOrDefault(m => m.ToolCallId == toolCallId);
        return singular != null
            ? singular.OriginalBytes
            : request
                .OfType<ToolsCallResultMessage>()
                .SelectMany(m => m.ToolCallResults)
                .FirstOrDefault(r => r.ToolCallId == toolCallId)
                .OriginalBytes;
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
