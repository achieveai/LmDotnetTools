using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmLifecycle.Payloads;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using FluentAssertions;
using LmMultiTurn.Tests.Lifecycle;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LmMultiTurn.Tests.Compaction;

/// <summary>
/// The just-in-time policy inside <see cref="MultiTurnAgentLoop"/> (#684, spec 679 §5): evaluated
/// immediately before each provider call and nowhere else, in every mode, on the reactive path and
/// under the kill switch. Sizes are chosen so the arithmetic is visible: every tool result is
/// <see cref="ResultTokens"/> tokens, the window is <see cref="Window"/>, the reserve is the loop's
/// <c>MaxToken</c> (100) with no margin, so the usable window is 2300, warn is 1610, compact is 1840
/// and the hard row fires at 2060 request tokens.
/// </summary>
public class CompactionLoopTests
{
    private const string Thread = "thread-compaction";
    private const string Model = "test-model";
    private const long Window = 2_400;
    private const long Usable = Window - 100;
    private const long ResultTokens = 312; // 1200 chars / 4 + 12 overhead
    private static readonly string Padding = new('x', 1_200);

    private sealed class ScriptedAgent(Func<int, IReadOnlyList<IMessage>> script) : IStreamingAgent
    {
        public List<IReadOnlyList<IMessage>> Requests { get; } = [];

        /// <summary>The tool names offered on each call, in call order.</summary>
        public List<IReadOnlyList<string>> FunctionNames { get; } = [];

        public Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            var request = messages.ToList();
            Requests.Add(request);
            FunctionNames.Add([.. (options?.Functions ?? []).Select(f => f.Name)]);
            var reply = script(Requests.Count).Select(m => m.WithIds(options)).ToList();
            return Task.FromResult(Stream(reply));
        }

        /// <summary>The requests made off the streaming path: the summary pass calls the agent directly.</summary>
        public List<IReadOnlyList<IMessage>> SummaryRequests { get; } = [];

        public Task<IEnumerable<IMessage>> GenerateReplyAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            SummaryRequests.Add([.. messages]);
            return Task.FromResult<IEnumerable<IMessage>>([
                new TextMessage { Text = SummaryJson, Role = Role.Assistant },
            ]);
        }

        private static async IAsyncEnumerable<IMessage> Stream(
            IEnumerable<IMessage> messages,
            [EnumeratorCancellation] CancellationToken ct = default
        )
        {
            foreach (var message in messages)
            {
                ct.ThrowIfCancellationRequested();
                yield return message;
                await Task.Yield();
            }
        }
    }

    /// <summary>What a real summarizer's provider answers: the smallest summary the validator accepts.</summary>
    private const string SummaryJson =
        """{"instructions":[],"goals":[],"decisions":[],"tasks":[],"artifacts":[],"headlines":{},"agent_outcomes":{},"narrative":"Summarised."}""";

    /// <summary>Quotes the current instruction whole and headlines every run: always passes V1–V9.</summary>
    private sealed class EchoSummarizer : ICheckpointSummarizer
    {
        public List<CheckpointSummaryRequest> Requests { get; } = [];

        public Func<CheckpointSummaryRequest, Exception?> Fail { get; set; } = _ => null;

        /// <summary>Answers with a narrative far over the V7 cap: a summary that fails validation.</summary>
        public bool Invalid { get; set; }

        /// <summary>Awaited before answering, with the summary call's token: a slow or stuck summary model.</summary>
        public Func<CancellationToken, Task>? Hold { get; set; }

        public async Task<CheckpointSummaryResponse> SummarizeAsync(
            CheckpointSummaryRequest request,
            CancellationToken ct = default
        )
        {
            Requests.Add(request);
            if (Hold is { } hold)
            {
                await hold(ct);
            }

            if (Fail(request) is { } failure)
            {
                throw failure;
            }

            return new CheckpointSummaryResponse(
                new CheckpointSummary
                {
                    Instructions =
                    [
                        .. request.CurrentInstruction.Select(r => new QuotedItem
                        {
                            Seq = r.Seq,
                            Quote = r.Text ?? string.Empty,
                        }),
                    ],
                    Headlines = request.RunIds.ToDictionary(id => id, _ => "ran the tool", StringComparer.Ordinal),
                    Narrative = Invalid ? new string('n', 20_000) : $"Summarised {request.Rows.Count} rows.",
                },
                new UsageMessage
                {
                    Usage = new Usage
                    {
                        PromptTokens = 50,
                        CompletionTokens = 10,
                        TotalTokens = 60,
                    },
                }
            );
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));
        private readonly Task _runTask;

        public Harness(
            Func<int, IReadOnlyList<IMessage>> script,
            CompactionOptions options,
            Func<string?, long?>? window = null,
            string? killSwitch = null,
            IConversationStore? store = null,
            SubAgentOptions? subAgentOptions = null,
            AgentCollaborationSetup? collaboration = null,
            Func<string, string>? echo = null,
            bool start = true,
            Func<Exception, bool>? overflowVerdict = null,
            ILogger<MultiTurnAgentLoop>? logger = null,
            string? extraTool = null,
            bool realSummarizer = false,
            Func<string?, long>? textTokens = null
        )
        {
            Agent = new ScriptedAgent(script);
            Store = store ?? new InMemoryConversationStore();
            Task<ToolHandlerResult> Handle(string args, ToolCallContext _, CancellationToken __) =>
                Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText(echo?.Invoke(args) ?? Padding));

            var registry = new FunctionRegistry().AddFunction(
                new FunctionContract
                {
                    Name = "Echo",
                    Description = "Returns padding",
                    Parameters = [],
                },
                Handle
            );
            // A second tool under a name the compaction tool-knowledge registry recognises (e.g. "Read").
            if (extraTool is not null)
            {
                registry = registry.AddFunction(
                    new FunctionContract
                    {
                        Name = extraTool,
                        Description = "Returns padding",
                        Parameters = [],
                    },
                    Handle
                );
            }
            Setup = new CompactionSetup
            {
                Options = options,
                // Null lets the runtime pick the summarizer its options name, which is what the prefix mode selects.
                Summarizer = realSummarizer ? null : Summarizer,
                // Each test's window is what the conversation may use: the tool definitions every request
                // carries come on top, so the arithmetic above holds while the estimate counts them.
                ResolveWindowTokens = window is null ? null : model => window(model) + (Loop?.ToolSchemaTokens ?? 0),
                Clock = Clock,
                ReadEnvironment = _ => KillSwitch,
                // Unset by default: the tests exercise the built-in verdict a host such as the sample gets.
                IsContextOverflow = overflowVerdict,
                TextTokens = textTokens,
            };
            KillSwitch = killSwitch;
            Loop = new MultiTurnAgentLoop(
                Agent,
                registry,
                Thread,
                includeAskUserQuestionTool: false,
                includeNotifyClientTool: false,
                defaultOptions: new GenerateReplyOptions { ModelId = Model, MaxToken = 100 },
                store: Store,
                lifecycleServices: new MultiTurnLifecycleServices { Publisher = Publisher },
                subAgentOptions: subAgentOptions,
                collaboration: collaboration,
                compaction: Setup,
                logger: logger
            );
            // An unstarted loop has not restored its thread: what a host holds between creating a loop and running it.
            _runTask = start ? Loop.RunAsync(_cts.Token) : Task.CompletedTask;
        }

        public ScriptedAgent Agent { get; }

        public IConversationStore Store { get; }

        public EchoSummarizer Summarizer { get; } = new();

        public RecordingLifecyclePublisher Publisher { get; } = new();

        public FixedClock Clock { get; } = new();

        public CompactionSetup Setup { get; }

        public MultiTurnAgentLoop Loop { get; }

        public string? KillSwitch { get; set; }

        public IReadOnlyList<CompactionPayload> Decisions =>
            Publisher.Payloads<CompactionPayload>(LifecycleEventTypes.CompactionDecided);

        public async Task<RunCompletedMessage> RunAsync(string text)
        {
            RunCompletedMessage? completed = null;
            await foreach (
                var message in Loop.ExecuteRunAsync(
                    new UserInput([new TextMessage { Text = text, Role = Role.User }]),
                    _cts.Token
                )
            )
            {
                if (message is RunCompletedMessage done)
                {
                    completed = done;
                }
            }

            return completed ?? throw new InvalidOperationException("run never completed");
        }

        public async Task<IReadOnlyList<IMessage>> StoredRowsAsync() =>
            MessagePersistenceConverter.FromPersistedMessagesResilient(await Store.LoadMessagesAsync(Thread));

        public async Task<CompactionState?> StateAsync() => await CompactionStateProjection.LoadAsync(Store, Thread);

        /// <summary>Cancels the loop and any run in progress, the way a stop or a disposal does.</summary>
        public Task CancelAsync() => _cts.CancelAsync();

        public async Task<IReadOnlyList<ContextObservation>> ObservationsAsync() =>
            ContextObservationProjection.HistoryFromMetadata(await Store.LoadMetadataAsync(Thread));

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try
            {
                await _runTask;
            }
            catch (OperationCanceledException) { }

            await Loop.DisposeAsync();
            _cts.Dispose();
        }
    }

    private static CompactionOptions Options(CompactionMode mode) =>
        new()
        {
            Mode = mode,
            ReserveMarginTokens = 0,
            MinTailTokens = 100,
            CooldownGenerations = 0,
            CooldownNewTokens = 0,
            CacheTtl = TimeSpan.Zero,
            // These fixtures pin the checkpoint path; clearing old tool results first is pinned by its own tests.
            ClearToolResultsKeepTurns = null,
        };

    /// <summary>Calls Echo for the first <paramref name="toolCalls"/> requests, then answers with text.</summary>
    private static Func<int, IReadOnlyList<IMessage>> EchoThenDone(int toolCalls, string done = "done") =>
        call =>
            call <= toolCalls
                ?
                [
                    new ToolCallMessage
                    {
                        ToolCallId = $"tc-{call}",
                        FunctionName = "Echo",
                        FunctionArgs = "{}",
                        Role = Role.Assistant,
                    },
                ]
                : [new TextMessage { Text = done, Role = Role.Assistant }];

    /// <summary>Anthropic's refusal of a request over the model's context window, as the provider surfaces it.</summary>
    private static HttpRequestException ProviderOverflow() =>
        new("prompt is too long: 213462 tokens > 200000 maximum", null, HttpStatusCode.BadRequest);

    /// <summary>
    /// The provider sees tool turns joined into <see cref="ToolsCallAggregateMessage"/> by the loop's
    /// middleware; the estimator measures the split rows the history holds, so expand them first.
    /// </summary>
    private static long Estimate(IReadOnlyList<IMessage> request) =>
        CompactionRuntime.EstimateTokens([
            .. request.SelectMany<IMessage, IMessage>(m =>
                m is ToolsCallAggregateMessage agg ? [agg.ToolsCallMessage, agg.ToolsCallResult] : [m]
            ),
        ]);

    /// <summary>What the model reads of each message: role, kind, text, call arguments and result text.</summary>
    private static IReadOnlyList<string> ContentWire(IReadOnlyList<IMessage> request) =>
        [
            .. request.Select(m =>
                m switch
                {
                    ToolsCallAggregateMessage agg => "tools|"
                        + string.Join("|", agg.ToolsCallMessage.ToolCalls.Select(c => c.ToolCallId + c.FunctionArgs))
                        + "|"
                        + string.Join("|", agg.ToolsCallResult.ToolCallResults.Select(r => r.ToolCallId + r.Result)),
                    ICanGetText text => $"{m.GetType().Name}|{m.Role}|{text.GetText()}",
                    _ => m.GetType().Name,
                }
            ),
        ];

    private static IEnumerable<ToolCallResult> ToolResults(IReadOnlyList<IMessage> request) =>
        request.OfType<ToolsCallAggregateMessage>().SelectMany(a => a.ToolsCallResult.ToolCallResults);

    /// <summary>Six parallel Echo calls in one generation, each asking for <paramref name="size"/> chars.</summary>
    private static IReadOnlyList<IMessage> ParallelEcho(int calls, int size) =>
        [
            .. Enumerable
                .Range(1, calls)
                .Select(i => new ToolCallMessage
                {
                    ToolCallId = $"wide-{i}",
                    FunctionName = "Echo",
                    FunctionArgs = $$"""{"size":{{size}}}""",
                    Role = Role.Assistant,
                }),
        ];

    private static string SizedEcho(string args) =>
        args.Contains("\"size\"", StringComparison.Ordinal)
            ? new string(
                'w',
                int.Parse(args.Split(':')[1].TrimEnd('}'), System.Globalization.CultureInfo.InvariantCulture)
            )
            : Padding;

    [Fact]
    public async Task CompactMode_A124kCharToolResultInA32kWindow_FitsTheRequest_AndTheStoredRowStaysWhole()
    {
        // The Terra failure: one Grep result of ~124k chars inside the current run, in a 32k window with a
        // 4,096-token reserve. No cut can move a single generation out of the tail, so only the view cap
        // can make the next request fit.
        var grep = string.Concat(Enumerable.Range(0, 12_400).Select(i => $"row {i:D5}\n"));
        const long window = 32_000;
        const long usable = window - 4_096;
        Harness h = null!;
        await using var _ = h = new Harness(
            EchoThenDone(1),
            Options(CompactionMode.Compact) with
            {
                ReserveMarginTokens = 3_996,
            },
            // The whole 32k, tool definitions included: undo the harness's schema allowance.
            _ => window - (h?.Loop?.ToolSchemaTokens ?? 0),
            echo: _ => grep
        );

        var completed = await h.RunAsync("search the repository");

        completed.IsError.Should().BeFalse(completed.ErrorMessage);
        h.Agent.Requests.Should().HaveCount(2);
        (Estimate(h.Agent.Requests[1]) + h.Loop.ToolSchemaTokens).Should().BeLessThanOrEqualTo(usable);
        var shown = ToolResults(h.Agent.Requests[1]).Single();
        shown.Result.Should().StartWith("row 00000").And.EndWith("row 12399\n").And.Contain("elided");
        shown.Result.Length.Should().BeLessThanOrEqualTo(new CompactionOptions().ToolResultViewCapChars(usable));
        h.Publisher.Payloads<CompactionPayload>(LifecycleEventTypes.CompactionFailed).Should().BeEmpty();
        (await h.StoredRowsAsync())
            .OfType<ToolCallResultMessage>()
            .Single()
            .Result.Should()
            .Be(grep, "the persisted row is never trimmed");
    }

    [Theory]
    [InlineData(CompactionMode.Off)]
    [InlineData(CompactionMode.Warn)]
    [InlineData(CompactionMode.Shadow)]
    public async Task OffWarnAndShadowModes_LeaveAnOversizedToolResultUntouchedInTheView(CompactionMode mode)
    {
        var grep = new string('g', 124_000);
        await using var h = new Harness(
            EchoThenDone(1),
            Options(mode) with
            {
                ReserveMarginTokens = 3_996,
            },
            _ => 32_000,
            echo: _ => grep
        );

        (await h.RunAsync("search the repository")).IsError.Should().BeFalse();

        ToolResults(h.Agent.Requests[1]).Single().Result.Should().Be(grep, $"{mode} never changes the provider input");
    }

    /// <summary>Reads the same file for the first <paramref name="calls"/> requests, then answers with text.</summary>
    private static Func<int, IReadOnlyList<IMessage>> ReadThenDone(int calls) =>
        call =>
            call <= calls
                ?
                [
                    new ToolCallMessage
                    {
                        ToolCallId = $"rd-{call}",
                        FunctionName = "Read",
                        FunctionArgs = """{"file_path":"a.md"}""",
                        Role = Role.Assistant,
                    },
                ]
                : [new TextMessage { Text = "done", Role = Role.Assistant }];

    /// <summary>
    /// Six reads of one file in an 8,000-token window, keeping one tool turn whole. Row seqs are only known
    /// after the clear pass reconciles them with the store, which is when RC1 can name a newest copy at all.
    /// </summary>
    private static Harness RepeatedReads(CompactionChecks checks) =>
        new(
            ReadThenDone(6),
            Options(CompactionMode.Compact) with
            {
                ClearToolResultsKeepTurns = 1,
                Checks = checks,
            },
            _ => 8_000,
            echo: _ => "padding " + new string('e', 4_000),
            extraTool: "Read"
        );

    [Fact]
    public async Task Rc1_ReplacesOlderReadsOfTheSameFile_InTheProviderRequest()
    {
        await using var h = RepeatedReads(new CompactionChecks { Rc1ResourceDedupe = true });

        var completed = await h.RunAsync("audit the file");

        completed.IsError.Should().BeFalse(completed.ErrorMessage);
        var reads = ToolResults(h.Agent.Requests[^1]).ToList();
        reads
            .Count(r => r.Result.Contains("padding", StringComparison.Ordinal))
            .Should()
            .Be(1, "only the newest read of a.md keeps its text");
        reads
            .Count(r => r.Result.StartsWith("[Tool result superseded", StringComparison.Ordinal))
            .Should()
            .BeGreaterThan(1, "every earlier read names the newest copy instead of the recall placeholder");
    }

    [Fact]
    public async Task Rc1_Off_ShowsTheRecallPlaceholderForEveryClearedRead()
    {
        await using var h = RepeatedReads(CompactionChecks.None);

        (await h.RunAsync("audit the file")).IsError.Should().BeFalse();

        ToolResults(h.Agent.Requests[^1])
            .Should()
            .NotContain(r => r.Result.StartsWith("[Tool result superseded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rc2_TrimsAClearedShellResult_InsteadOfReplacingIt()
    {
        // Echo is not in the tool-knowledge registry, so RC2 treats it as a shell tool: not reproducible.
        await using var h = new Harness(
            EchoThenDone(8),
            Options(CompactionMode.Compact) with
            {
                ClearToolResultsKeepTurns = 2,
                Checks = new CompactionChecks { Rc2ShellRetention = true },
                ShellTrimChars = 500,
            },
            _ => 8_000,
            echo: _ => new string('e', 4_000)
        );

        (await h.RunAsync("start")).IsError.Should().BeFalse();

        var cleared = h.Agent.Requests.FindIndex(r => ToolResults(r).Any(x => x.Result.Length < 4_000));
        cleared.Should().BePositive();
        var shown = ToolResults(h.Agent.Requests[cleared]).ToList();
        shown
            .Take(shown.Count - 2)
            .Should()
            .OnlyContain(r =>
                r.Result.Contains("elided from this tool result", StringComparison.Ordinal) && r.Result.Length <= 500
            );
        shown.TakeLast(2).Should().OnlyContain(r => r.Result.Length == 4_000, "the most recent turns stay whole");
    }

    [Fact]
    public async Task CompactMode_ClearsOldToolResultsBeforeSummarizing_AndSkipsTheSummaryWhenThatFits()
    {
        // Window 8,000, reserve 100: compact band 6,320, target 3,555. Six ~1,040-token tool turns cross the
        // band; clearing all but the last two turns reclaims ~3,800 tokens with no summary call.
        await using var h = new Harness(
            EchoThenDone(8),
            Options(CompactionMode.Compact) with
            {
                ClearToolResultsKeepTurns = 2,
            },
            _ => 8_000,
            echo: _ => new string('e', 4_000)
        );

        var completed = await h.RunAsync("start");

        completed.IsError.Should().BeFalse(completed.ErrorMessage);
        h.Summarizer.Requests.Should().BeEmpty("clearing alone reached the target");
        h.Decisions.Select(d => d.Reason).Should().Contain(CompactionReasons.ToolResultsCleared);
        var state = await h.StateAsync();
        state!.ToolResultsClearedThroughSeq.Should().BePositive();
        state.ActiveCheckpointId.Should().BeNull();

        var cleared = h.Agent.Requests.FindIndex(r => ToolResults(r).Any(x => x.Result.Length < 4_000));
        cleared.Should().BePositive();
        var shown = ToolResults(h.Agent.Requests[cleared]).ToList();
        shown.Take(shown.Count - 2).Should().OnlyContain(r => r.Result.Contains("RecallConversation"));
        shown.TakeLast(2).Should().OnlyContain(r => r.Result.Length == 4_000, "the most recent turns stay whole");
        (await h.StoredRowsAsync())
            .OfType<ToolCallResultMessage>()
            .Should()
            .OnlyContain(r => r.Result.Length == 4_000, "clearing edits the view, never the rows");

        // Prompt-cache stability: once cleared, the next request repeats this one byte for byte as its prefix.
        var before = ContentWire(h.Agent.Requests[cleared]);
        ContentWire(h.Agent.Requests[cleared + 1]).Take(before.Count).Should().Equal(before);
    }

    [Fact]
    public async Task ClearAnsweredToolResultsOnly_LeavesTheExchangeInProgressWhole_AndSummarizesInstead()
    {
        // The same window and turns as the test above, where clearing all but the last two turns alone reached
        // the target. Every one of those turns belongs to the single exchange the model is still working on, so
        // with the guard on there is nothing it may clear and the summariser has to make the room.
        await using var h = new Harness(
            EchoThenDone(8),
            Options(CompactionMode.Compact) with
            {
                ClearToolResultsKeepTurns = 2,
                ClearAnsweredToolResultsOnly = true,
            },
            _ => 8_000,
            echo: _ => new string('e', 4_000)
        );

        var completed = await h.RunAsync("start");

        completed.IsError.Should().BeFalse(completed.ErrorMessage);
        var state = await h.StateAsync();
        state!.ToolResultsClearedThroughSeq.Should().BeNull("the model has not answered on any of these results");
        h.Decisions.Select(d => d.Reason).Should().NotContain(CompactionReasons.ToolResultsCleared);
        h.Summarizer.Requests.Should().NotBeEmpty("the room has to come from a summary instead");
        ToolResults(h.Agent.Requests[^1])
            .Should()
            .OnlyContain(r => r.Result.Length == 4_000, "no result the model is still using was replaced");
    }

    [Fact]
    public async Task ClearedAndTrimmedView_IsRebuiltIdenticallyByALoopRestartedOverTheSameStore()
    {
        var options = Options(CompactionMode.Compact) with { ClearToolResultsKeepTurns = 2 };
        IReadOnlyList<IMessage> lastBeforeRestart;
        IConversationStore store;
        var first = new Harness(
            call => call == 8 ? ParallelEcho(1, 30_000) : EchoThenDone(8)(call),
            options,
            _ => 8_000,
            echo: args => args.Contains("size", StringComparison.Ordinal) ? SizedEcho(args) : new string('e', 4_000)
        );
        try
        {
            (await first.RunAsync("start")).IsError.Should().BeFalse();
            first.Summarizer.Requests.Should().BeEmpty();
            lastBeforeRestart = first.Agent.Requests[^1];
            ToolResults(lastBeforeRestart)
                .Should()
                .Contain(r => r.Result.Contains("elided"), "the 30k result is trimmed");
            ToolResults(lastBeforeRestart).Should().Contain(r => r.Result.Contains("cleared"));
            store = first.Store;
        }
        finally
        {
            await first.DisposeAsync();
        }

        await using var second = new Harness(EchoThenDone(0), options, _ => 8_000, store: store);
        (await second.RunAsync("continue")).IsError.Should().BeFalse();

        var expected = ContentWire(lastBeforeRestart);
        ContentWire(second.Agent.Requests[0]).Take(expected.Count).Should().Equal(expected);
    }

    [Fact]
    public async Task CompactMode_WhenNoLegalViewFitsTheWindow_RefusesWithViewExceedsWindow_InsteadOfSending()
    {
        // A finished run of two tool turns, then a pasted 36,000-char instruction (~9k tokens) in a 7,900-token
        // usable window. Compaction acts — the finished run is cut into a checkpoint — but the current
        // instruction is never cut or trimmed, so no legal view fits.
        await using var h = new Harness(EchoThenDone(2), Options(CompactionMode.Compact), _ => 8_000);
        (await h.RunAsync("start")).IsError.Should().BeFalse();

        var completed = await h.RunAsync(new string('p', 36_000));

        completed
            .IsError.Should()
            .BeTrue(
                "decisions {0}, {1} requests",
                string.Join(", ", h.Decisions.Select(d => $"{d.Decision}/{d.Reason}/{d.Tokens}")),
                h.Agent.Requests.Count
            );
        completed.ErrorMessage.Should().StartWith(CompactionReasons.ViewExceedsWindow);
        h.Agent.Requests.Should().HaveCount(3, "the over-window request is never sent");
        h.Publisher.Payloads<CompactionPayload>(LifecycleEventTypes.CompactionFailed)
            .Should()
            .Contain(p => p.Reason == CompactionReasons.ViewExceedsWindow);
        (await h.StateAsync())!
            .SizeRefusedRunIds.Should()
            .ContainSingle("R4 must not keep the retry after a size refusal whole");
    }

    [Fact]
    public async Task CompactMode_WhenNoCutIsAllowed_TheFitCheckClearsAllButTheLatestToolTurn_InsteadOfRefusing()
    {
        // The per-run budget is spent (0), so the policy may not cut; the ninth request carries eight ~1,012-token
        // results against a 7,900-token usable window. Only the fit check's cheapest escalation can send it.
        await using var h = new Harness(
            EchoThenDone(8),
            Options(CompactionMode.Compact) with
            {
                MaxCompactionsPerRun = 0,
                ClearToolResultsKeepTurns = 3,
            },
            _ => 8_000,
            echo: _ => new string('e', 4_000)
        );

        var completed = await h.RunAsync("start");

        completed.IsError.Should().BeFalse(completed.ErrorMessage);
        h.Agent.Requests.Should().HaveCount(9);
        h.Summarizer.Requests.Should().BeEmpty("clearing needs no summary call");
        var last = h.Agent.Requests[^1];
        Estimate(last).Should().BeLessThanOrEqualTo(7_900);
        var shown = ToolResults(last).ToList();
        shown.Should().HaveCount(8);
        shown.SkipLast(1).Should().OnlyContain(r => r.Result.Contains("RecallConversation"));
        shown[^1].Result.Length.Should().Be(4_000, "the latest tool turn is never cleared");
        (await h.StateAsync())!.ToolResultsClearedThroughSeq.Should().BePositive();
    }

    [Fact]
    public async Task PolicyEstimate_CountsToolSchemas_AndCalibratesWithTheMeasuredPromptTokens()
    {
        await using var h = new Harness(
            call =>
                call == 1
                    ?
                    [
                        .. EchoThenDone(1)(call),
                        new UsageMessage
                        {
                            Usage = new Usage
                            {
                                PromptTokens = 5_000,
                                CompletionTokens = 10,
                                TotalTokens = 5_010,
                            },
                        },
                    ]
                    : EchoThenDone(1)(call),
            Options(CompactionMode.Warn),
            _ => 100_000
        );

        (await h.RunAsync("start")).IsError.Should().BeFalse();

        h.Decisions[0]
            .Tokens.Should()
            .BeGreaterThan(Estimate(h.Agent.Requests[0]), "the tool schemas are part of every request");
        h.Decisions[1]
            .Tokens.Should()
            .BeGreaterThanOrEqualTo(
                5_000 + ResultTokens,
                "the provider's own count of the previous request, plus what was appended since"
            );
    }

    [Fact]
    public async Task PolicyEstimate_SizesTextWithTheSetupsTokenizer_NotTheCharacterHeuristic()
    {
        // The heuristic overcounts prose by ~40% (r1: an 85k estimate for a 60k request), which is what pushed a
        // fitting request into the fit escalation. A host with a real tokenizer hands it in through the setup and
        // the policy's estimate follows it: here a counter that charges one token per run of text, so the 1,200
        // character tool result the second request carries costs one token instead of 300.
        await using var h = new Harness(
            EchoThenDone(1),
            Options(CompactionMode.Warn),
            _ => 100_000,
            textTokens: _ => 1
        );

        (await h.RunAsync("start")).IsError.Should().BeFalse();

        var growth = h.Decisions[1].Tokens - h.Decisions[0].Tokens;
        growth.Should().BePositive("the tool call and its result were appended");
        growth.Should().BeLessThan(ResultTokens, "the result's text was sized by the tokenizer, not length / 4");
        h.Decisions[0]
            .Tokens.Should()
            .BeGreaterThanOrEqualTo(
                h.Agent.Requests[0].Count * CompactionTokenEstimate.PerMessageOverhead,
                "the per-message framing is charged whatever sizes the text"
            );
    }

    private static bool HasEnvelope(IReadOnlyList<IMessage> request) =>
        request.Any(m =>
            m is TextMessage { Role: Role.User } t && t.Text.Contains("RecallConversation", StringComparison.Ordinal)
        );

    [Fact]
    public async Task CompactMode_KeepsTheAgentsIdentityInTheEnvelopeView()
    {
        // The identity preamble is prepended to the system prompt at loop construction (ADR 0019).
        // The compaction view is built from the prompt the compaction host holds, so if the host were
        // handed the caller's raw prompt the agent would forget who it is on the very turn a
        // checkpoint activates — and every turn after, since the view stays in force.
        var collaboration = AgentCollaborationSetup.CreateRoot(new AgentCollaborationOptions());
        await using var h = new Harness(
            EchoThenDone(7),
            Options(CompactionMode.Compact),
            _ => Window,
            collaboration: collaboration
        );

        var completed = await h.RunAsync("start");

        completed.IsError.Should().BeFalse();
        var first = h.Agent.Requests.FindIndex(HasEnvelope);
        first.Should().BePositive("the first requests go out raw");

        var identity = AgentIdentityPreamble.Compose(collaboration)!;
        h.Agent.Requests[first - 1]
            .OfType<TextMessage>()
            .Should()
            .Contain(
                m => m.Role == Role.System && m.Text.StartsWith(identity, StringComparison.Ordinal),
                "the raw request carries the identity"
            );
        h.Agent.Requests.Skip(first)
            .Should()
            .OnlyContain(
                r =>
                    r.OfType<TextMessage>()
                        .Any(m => m.Role == Role.System && m.Text.StartsWith(identity, StringComparison.Ordinal)),
                "the envelope view must carry the same identity the raw request did"
            );
    }

    [Fact]
    public async Task WarnMode_RecordsDecisions_AndNeverChangesTheProviderInput()
    {
        await using var h = new Harness(EchoThenDone(7), Options(CompactionMode.Warn), _ => Window);

        var completed = await h.RunAsync("start");

        completed.IsError.Should().BeFalse();
        h.Summarizer.Requests.Should().BeEmpty();
        h.Agent.Requests.Should().HaveCount(8);
        h.Agent.Requests.Select(r => r.Count).Should().BeInAscendingOrder("the raw history is what the provider sees");
        h.Agent.Requests.Should().OnlyContain(r => !HasEnvelope(r));
        h.Decisions.Should().HaveCount(8, "one decision per provider call");
        h.Decisions.Select(d => d.Decision).Should().Contain(CompactionDecisionKinds.Warn);
        h.Decisions.Last().Utilization.Should().BeGreaterThan(0.8);
        h.Decisions.Last().Decision.Should().Be(CompactionDecisionKinds.Warn, "warn mode caps every row at a warning");
        (await h.StoredRowsAsync()).Should().NotContain(m => m is CompactionCheckpointMessage);
        (await ContextObservationProjection.LoadLatestAsync(h.Store, Thread))!.Decision!.Decision.Should().Be("warn");
    }

    [Fact]
    public async Task ShadowMode_BuildsAndValidates_ButAppendsNoRowAndSendsTheRawInput()
    {
        await using var h = new Harness(EchoThenDone(7), Options(CompactionMode.Shadow), _ => Window);

        var completed = await h.RunAsync("start");

        completed.IsError.Should().BeFalse();
        h.Summarizer.Requests.Should().NotBeEmpty("shadow runs the summary pass");
        h.Agent.Requests.Should().OnlyContain(r => !HasEnvelope(r));
        h.Agent.Requests.Select(r => r.Count).Should().BeInAscendingOrder();
        h.Decisions.Select(d => d.Decision).Should().Contain(CompactionDecisionKinds.Shadow);
        h.Publisher.EventTypes.Should().NotContain(LifecycleEventTypes.CompactionApplied);
        (await h.StoredRowsAsync()).Should().NotContain(m => m is CompactionCheckpointMessage);
        var state = await h.StateAsync();
        state!.ActiveCheckpointId.Should().BeNull();
        state
            .History.Should()
            .Contain(e => e.Trigger == CompactionTrigger.Shadow && e.Status == CheckpointStatus.Rejected);
    }

    [Fact]
    public async Task CompactMode_ActivatesACheckpoint_AndSendsTheEnvelopeView()
    {
        await using var h = new Harness(EchoThenDone(7), Options(CompactionMode.Compact), _ => Window);

        var completed = await h.RunAsync("start");

        completed.IsError.Should().BeFalse();
        h.Summarizer.Requests.Should().HaveCount(1);
        h.Summarizer.Requests[0].ModelId.Should().Be(Model, "the summary model defaults to the loop's model (Q2)");
        var applied = h.Publisher.Payloads<CompactionPayload>(LifecycleEventTypes.CompactionApplied).Single();
        applied.Decision.Should().Be(CompactionDecisionKinds.Compact);
        applied.Reason.Should().Be(CompactionPolicy.EconomicReason);
        applied.CheckpointId.Should().NotBeNullOrEmpty();
        applied.BoundarySeq.Should().BePositive();

        var first = h.Agent.Requests.FindIndex(HasEnvelope);
        first.Should().BePositive("the first requests go out raw");
        applied.Tokens.Should().BeGreaterThanOrEqualTo((long)(0.8 * Usable), "the policy measured the raw request");
        Estimate(h.Agent.Requests[first]).Should().BeLessThan(applied.Tokens, "the view replaced the raw request");
        h.Agent.Requests[first].Count.Should().BeLessThan(h.Agent.Requests[first - 1].Count);
        h.Agent.Requests.Skip(first)
            .Should()
            .OnlyContain(r => HasEnvelope(r), "the view stays in force once activated");

        var rows = await h.StoredRowsAsync();
        var checkpoint = rows.OfType<CompactionCheckpointMessage>().Single();
        checkpoint.CheckpointId.Should().Be(applied.CheckpointId);
        checkpoint.Trigger.Should().Be(CompactionTrigger.Preemptive);
        // Before and after are both whole-request estimates: the decision's, and the view the next call sent.
        checkpoint.Stats.EstimatedTokensBefore.Should().Be(applied.Tokens);
        checkpoint
            .Stats.EstimatedTokensAfter.Should()
            .BeCloseTo(Estimate(h.Agent.Requests[first]) + h.Loop.ToolSchemaTokens, 50, "the new view's request");
        var state = await h.StateAsync();
        state!.ActiveCheckpointId.Should().Be(applied.CheckpointId);
        state.ActiveBoundarySeq.Should().Be(applied.BoundarySeq);
    }

    [Fact]
    public void CachedPrefix_WithADifferentSummaryModel_IsRefusedWhenTheRuntimeIsBuilt()
    {
        var options = Options(CompactionMode.Compact) with
        {
            SummaryPrefixMode = SummaryPrefixMode.CachedPrefix,
            SummaryModelId = "other-model",
        };

        var act = () => new Harness(EchoThenDone(1), options, _ => Window, start: false);

        act.Should().Throw<ArgumentException>().WithMessage("*CachedPrefix needs the same model as the loop*");
    }

    [Fact]
    public async Task CachedPrefix_RunsTheSummaryPass_OnTheAgentsOwnRequestPrefix()
    {
        await using var h = new Harness(
            EchoThenDone(7),
            Options(CompactionMode.Compact) with
            {
                SummaryPrefixMode = SummaryPrefixMode.CachedPrefix,
            },
            _ => Window,
            realSummarizer: true
        );

        var completed = await h.RunAsync("start");

        completed.IsError.Should().BeFalse(completed.ErrorMessage);
        var summaryRequest = h.Agent.SummaryRequests.Should().ContainSingle().Subject;
        summaryRequest[0].Role.Should().Be(Role.System, "the prefix opens with the loop's own system prompt");
        summaryRequest[^1]
            .Should()
            .BeOfType<TextMessage>()
            .Which.Text.Should()
            .Contain("Rows being compacted (already in your context above; cite by seq):");
        h.Agent.Requests.Should().Contain(r => HasEnvelope(r), "the pass produced a checkpoint the view uses");
    }

    [Fact]
    public async Task CompactMode_PublishesTheActivatedCheckpointRowToLiveSubscribers()
    {
        // The divider a browser draws for a checkpoint must appear when it activates, not only after a
        // reload rehydrates the stored row: a live subscriber is sent the same row the store holds.
        await using var h = new Harness(EchoThenDone(7), Options(CompactionMode.Compact), _ => Window);
        using var subscription = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var received = new List<IMessage>();
        // Registration happens on the first MoveNextAsync; a late subscriber still gets the in-flight
        // run's buffered messages, so the reader cannot miss the row by starting after the run.
        var reader = Task.Run(async () =>
        {
            await foreach (var message in h.Loop.SubscribeAsync(subscription.Token))
            {
                received.Add(message);
                if (message is RunCompletedMessage)
                {
                    return;
                }
            }
        });

        var completed = await h.RunAsync("start");
        await reader;

        completed.IsError.Should().BeFalse();
        var stored = (await h.StoredRowsAsync()).OfType<CompactionCheckpointMessage>().Single();
        received
            .OfType<CompactionCheckpointMessage>()
            .Should()
            .ContainSingle("the activated row is published once")
            .Which.CheckpointId.Should()
            .Be(stored.CheckpointId);
    }

    [Fact]
    public async Task ReactiveOverflow_CompactsThenRetriesTheSameInputOnce()
    {
        var overflowed = 0;
        await using var h = new Harness(
            call =>
            {
                if (call == 7 && overflowed++ == 0)
                {
                    throw ProviderOverflow();
                }

                return EchoThenDone(6)(call);
            },
            Options(CompactionMode.Compact),
            window: null
        );

        var completed = await h.RunAsync("start");

        completed.IsError.Should().BeFalse();
        h.Summarizer.Requests.Should().HaveCount(1);
        h.Agent.Requests.Should().HaveCount(8, "seven raw requests, one retry on the view");
        HasEnvelope(h.Agent.Requests[6]).Should().BeFalse();
        HasEnvelope(h.Agent.Requests[7]).Should().BeTrue();
        h.Decisions.Take(7).Should().OnlyContain(d => d.Reason == CompactionSkipReasons.CapacityUnknown);
        var applied = h.Publisher.Payloads<CompactionPayload>(LifecycleEventTypes.CompactionApplied).Single();
        applied.Trigger.Should().Be("reactive");
        (await h.StoredRowsAsync())
            .OfType<CompactionCheckpointMessage>()
            .Single()
            .Trigger.Should()
            .Be(CompactionTrigger.Reactive);
    }

    [Fact]
    public async Task ReactiveOverflow_RunsAgainInTheSameRun_WhenThePreviousOneAdvancedTheBoundary()
    {
        // Two provider refusals in one run, three generations apart: the first reactive compaction advanced the boundary,
        // so the second overflow gets the same ladder instead of failing the run.
        var thrown = new HashSet<int>();
        await using var h = new Harness(
            call => call is 7 or 10 && thrown.Add(call) ? throw ProviderOverflow() : EchoThenDone(10)(call),
            Options(CompactionMode.Compact),
            window: null
        );

        var completed = await h.RunAsync("start");

        completed.IsError.Should().BeFalse(completed.ErrorMessage);
        h.Agent.Requests.Should().HaveCount(11, "two refused requests, each retried once");
        var checkpoints = (await h.StoredRowsAsync()).OfType<CompactionCheckpointMessage>().ToList();
        checkpoints.Should().HaveCount(2).And.OnlyContain(c => c.Trigger == CompactionTrigger.Reactive);
        checkpoints.Select(c => c.Boundary.Seq).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task ReactiveOverflow_AfterTheFitCheckTightenedTheTrim_WithAFailingSummarizer_RetriesWithASmallerView()
    {
        // The estimate said the tightened request fits, the provider disagreed: the estimate undercounts. The reactive
        // ladder (clear, re-cut with the fallback checkpoint, tighten) must still find a smaller view, with no summary
        // model to help, instead of failing the run with overflow_after_compaction.
        const long window = 64_000;
        const long usable = window - 4_096;
        var thrown = false;
        Harness h = null!;
        await using var _ = h = new Harness(
            call =>
            {
                if (call == 3 && !thrown)
                {
                    thrown = true;
                    throw ProviderOverflow();
                }

                return call switch
                {
                    1 => EchoThenDone(1)(call),
                    2 => ParallelEcho(6, 55_000),
                    _ => EchoThenDone(0)(call),
                };
            },
            Options(CompactionMode.Compact) with
            {
                ReserveMarginTokens = 3_996,
                ClearToolResultsKeepTurns = 1,
            },
            _ => window - (h?.Loop?.ToolSchemaTokens ?? 0),
            echo: args =>
                args.Contains("\"size\"", StringComparison.Ordinal)
                    ? Lines(
                        int.Parse(args.Split(':')[1].TrimEnd('}'), System.Globalization.CultureInfo.InvariantCulture)
                    )
                    : "ok"
        );
        h.Summarizer.Fail = _ => new InvalidOperationException("summary model down");

        var completed = await h.RunAsync("read the six files");

        completed.IsError.Should().BeFalse(Describe(h, completed));
        h.Agent.Requests.Should().HaveCount(4, "the refused request, then one retry");
        var refused = h.Agent.Requests[2];
        var retry = h.Agent.Requests[3];
        (Estimate(refused) + h.Loop.ToolSchemaTokens)
            .Should()
            .BeLessThanOrEqualTo(usable, "the fit check had tightened it");
        var normalCap = Options(CompactionMode.Compact).ToolResultViewCapChars(usable);
        ToolResults(refused)
            .Where(r => r.ToolCallId!.StartsWith("wide-", StringComparison.Ordinal))
            .Should()
            .HaveCount(6)
            .And.OnlyContain(r => r.Result.Length < normalCap, "the refused request was already tightened");
        (await h.StateAsync())!.ToolResultsTightened.Should().NotBeNull();
        Estimate(retry).Should().BeLessThan(Estimate(refused), "the retry is smaller than what the provider refused");
        var shown = ToolResults(retry).Where(r => r.ToolCallId!.StartsWith("wide-", StringComparison.Ordinal)).ToList();
        shown.Should().HaveCount(6).And.OnlyContain(r => r.Result.Contains("RecallConversation(tool_call_id="));
        h.Publisher.Payloads<CompactionPayload>(LifecycleEventTypes.CompactionFailed)
            .Select(p => p.Reason)
            .Should()
            .NotContain(CompactionFailureReasons.OverflowAfterCompaction);
        (await h.StoredRowsAsync())
            .OfType<CompactionCheckpointMessage>()
            .Should()
            .OnlyContain(c => c.Stats.SummaryFallback != null, "no summary model answered");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "prompt is too long: 213462 tokens > 200000 maximum")]
    [InlineData(
        HttpStatusCode.BadRequest,
        "This model's maximum context length is 128000 tokens. However, your messages resulted in 130211 tokens."
    )]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "Request Entity Too Large")]
    [InlineData(
        HttpStatusCode.BadRequest,
        """HTTP request failed with status BadRequest (Bad Request). Response body: {"error":{"message":"prompt token count of 168929 exceeds the limit of 168000","code":"model_max_prompt_tokens_exceeded"}}"""
    )]
    public async Task ReactiveOverflow_WithNoHostVerdict_AProviderOverflowOfARequestEstimatedToFit_RunsTheLadderAndRetries(
        HttpStatusCode status,
        string body
    )
    {
        // The sample host sets no verdict, and the fit check has just made the estimate fit the usable window: the
        // provider counting more than the estimate is exactly when the reactive ladder is needed.
        var overflowed = 0;
        await using var h = new Harness(
            call =>
                call == 7 && overflowed++ == 0
                    ? throw new HttpRequestException(body, null, status)
                    : EchoThenDone(6)(call),
            Options(CompactionMode.Compact),
            _ => 8_000
        );

        var completed = await h.RunAsync("start");

        completed.IsError.Should().BeFalse(Describe(h, completed));
        (Estimate(h.Agent.Requests[6]) + h.Loop.ToolSchemaTokens)
            .Should()
            .BeLessThan(8_000 - 100, "the refused request was estimated well inside the usable window");
        h.Agent.Requests.Should().HaveCount(8, "seven requests, one retry on the view");
        HasEnvelope(h.Agent.Requests[7]).Should().BeTrue();
        h.Publisher.Payloads<CompactionPayload>(LifecycleEventTypes.CompactionApplied)
            .Should()
            .ContainSingle()
            .Which.Trigger.Should()
            .Be("reactive");
    }

    [Fact]
    public async Task ReactiveOverflow_TheHostsVerdict_OverridesTheBuiltInOne()
    {
        await using var h = new Harness(
            call => call == 7 ? throw ProviderOverflow() : EchoThenDone(6)(call),
            Options(CompactionMode.Compact),
            _ => 8_000,
            overflowVerdict: _ => false
        );

        var completed = await h.RunAsync("start");

        completed.IsError.Should().BeTrue();
        completed.ErrorCode.Should().BeNull("the host said this failure is not an overflow");
        h.Summarizer.Requests.Should().BeEmpty();
        h.Agent.Requests.Should().HaveCount(7);
    }

    [Fact]
    public async Task ReactiveOverflow_TheRetryWarning_LogsTheFailureTypeAndStatus_NeverTheProviderBody()
    {
        // A provider's error message carries its response body, which can quote the conversation (#774 F-004).
        const string secret = "SECRET-CONVERSATION-TEXT";
        var logger = new CapturingLogger<MultiTurnAgentLoop>();
        var overflowed = 0;
        await using var h = new Harness(
            call =>
                call == 7 && overflowed++ == 0
                    ? throw new HttpRequestException(
                        $"prompt is too long: 213462 tokens > 200000 maximum. Body: {secret}",
                        new InvalidOperationException(secret),
                        HttpStatusCode.BadRequest
                    )
                    : EchoThenDone(6)(call),
            Options(CompactionMode.Compact),
            _ => 8_000,
            logger: logger
        );

        var completed = await h.RunAsync("start");

        completed.IsError.Should().BeFalse(Describe(h, completed));
        var retry = logger
            .Entries.Should()
            .ContainSingle(e => e.Message.Contains("overflowed the context window", StringComparison.Ordinal))
            .Subject;
        retry.Properties["ExceptionType"].Should().Be(nameof(HttpRequestException));
        retry.Properties["HttpStatus"].Should().Be(400);
        logger.Entries.Should().NotContain(e => e.Mentions(secret), "no log line carries the provider's body");
    }

    /// <summary>Every entry a loop logs: level, rendered message, template properties and the exception.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public System.Collections.Concurrent.ConcurrentQueue<LogEntry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) =>
            Entries.Enqueue(
                new LogEntry(
                    logLevel,
                    formatter(state, exception),
                    (state as IEnumerable<KeyValuePair<string, object?>> ?? []).ToDictionary(p => p.Key, p => p.Value),
                    exception
                )
            );
    }

    private sealed record LogEntry(
        LogLevel Level,
        string Message,
        IReadOnlyDictionary<string, object?> Properties,
        Exception? Exception
    )
    {
        /// <summary>Whether <paramref name="text"/> appears in the message, any property value, or the exception.</summary>
        public bool Mentions(string text) =>
            Message.Contains(text, StringComparison.Ordinal)
            || Properties.Values.Any(v => v?.ToString()?.Contains(text, StringComparison.Ordinal) == true)
            || Exception?.ToString().Contains(text, StringComparison.Ordinal) == true;
    }

    [Fact]
    public async Task ReactiveOverflow_WhenOnlyTheTrimFloorFits_BelowTheRefusedRequestButOverTheTarget_RetriesTightened()
    {
        // Thirty parallel 7,000-char results (~53k tokens) fit 59,904 usable, and the provider refuses them. Clearing keeps
        // the latest turn and R1 keeps it whole; the trim floor (~31k tokens) misses the reactive target of 0.45 x the
        // refused estimate, but it is still well under what was refused: that retry must be sent.
        const long window = 64_000;
        var thrown = false;
        Harness h = null!;
        await using var _ = h = new Harness(
            call =>
            {
                if (call == 2 && !thrown)
                {
                    thrown = true;
                    throw ProviderOverflow();
                }

                return call == 1 ? ParallelEcho(30, 7_000) : EchoThenDone(0)(call);
            },
            Options(CompactionMode.Compact) with
            {
                ReserveMarginTokens = 3_996,
                ClearToolResultsKeepTurns = 1,
            },
            _ => window - (h?.Loop?.ToolSchemaTokens ?? 0),
            echo: args =>
                Lines(int.Parse(args.Split(':')[1].TrimEnd('}'), System.Globalization.CultureInfo.InvariantCulture))
        );

        var completed = await h.RunAsync("read the thirty files");

        completed.IsError.Should().BeFalse(Describe(h, completed));
        h.Agent.Requests.Should().HaveCount(3, "the refused request, then one retry");
        var refused = Estimate(h.Agent.Requests[1]) + h.Loop.ToolSchemaTokens;
        var retry = Estimate(h.Agent.Requests[2]) + h.Loop.ToolSchemaTokens;
        retry.Should().BeLessThan(refused);
        retry
            .Should()
            .BeGreaterThan(
                (long)(new CompactionOptions().TargetRatio * refused),
                "the floor misses the reactive target"
            );
        ToolResults(h.Agent.Requests[2])
            .Should()
            .HaveCount(30)
            .And.OnlyContain(r => r.Result.Contains("RecallConversation(tool_call_id="), "the retry is tightened");
        h.Publisher.Payloads<CompactionPayload>(LifecycleEventTypes.CompactionFailed)
            .Select(p => p.Reason)
            .Should()
            .NotContain(CompactionFailureReasons.OverflowAfterCompaction);
    }

    [Fact]
    public async Task ReactiveOverflow_TwiceInOneRun_HelpedOnlyByClearing_RecoversBothTimes()
    {
        // No cut is needed either time: clearing all but the latest tool turn brings each refused request under the
        // reactive target. The boundary never moves, and the second overflow still gets the ladder.
        var thrown = new HashSet<int>();
        await using var h = new Harness(
            call => call is 5 or 9 && thrown.Add(call) ? throw ProviderOverflow() : EchoThenDone(10)(call),
            Options(CompactionMode.Compact) with
            {
                ClearToolResultsKeepTurns = 1,
            },
            _ => 32_000,
            echo: _ => new string('c', 6_000)
        );

        var completed = await h.RunAsync("start");

        completed.IsError.Should().BeFalse(Describe(h, completed));
        h.Agent.Requests.Should().HaveCount(11, "two refused requests, each retried once");
        static int Cleared(IReadOnlyList<IMessage> request) =>
            ToolResults(request).Count(r => r.Result.StartsWith("[Tool result cleared", StringComparison.Ordinal));
        Cleared(h.Agent.Requests[5]).Should().Be(3, "the first retry clears all but the latest of four tool turns");
        Cleared(h.Agent.Requests[9]).Should().Be(6, "the second retry clears all but the latest of seven");
        h.Summarizer.Requests.Should().BeEmpty("clearing alone was enough");
        (await h.StoredRowsAsync()).OfType<CompactionCheckpointMessage>().Should().BeEmpty();
        h.Decisions.Count(d => d.Reason == CompactionRuntime.ReactiveReason).Should().Be(2);
    }

    [Fact]
    public async Task UnderUnsafeState_AnInterruptedWideParallelTurnOverTheWindow_IsTightened_InsteadOfRefused()
    {
        // Six ~55k-char results fit the first route's window; its stream ends early and the retry resolves a 64k window
        // (a route change). The retry is an interrupted turn, so nothing may be cut and clearing keeps the latest turn;
        // the tighter trim is view-only and splits nothing, so it still sends the request.
        const long usable = 64_000 - 4_096;
        long window = 400_000;
        var interrupted = false;
        Harness h = null!;
        await using var _ = h = new Harness(
            call =>
            {
                if (call == 2 && !interrupted)
                {
                    interrupted = true;
                    window = 64_000;
                    throw new HttpIOException(HttpRequestError.ResponseEnded, "stream ended early");
                }

                return call == 1 ? ParallelEcho(6, 55_000) : EchoThenDone(0)(call);
            },
            Options(CompactionMode.Compact) with
            {
                ReserveMarginTokens = 3_996,
                ClearToolResultsKeepTurns = 1,
            },
            _ => window - (h?.Loop?.ToolSchemaTokens ?? 0),
            echo: args =>
                Lines(int.Parse(args.Split(':')[1].TrimEnd('}'), System.Globalization.CultureInfo.InvariantCulture))
        );

        var completed = await h.RunAsync("read the six files");

        completed.IsError.Should().BeFalse(Describe(h, completed));
        h.Decisions.Should().Contain(d => d.Reason == CompactionSkipReasons.UnsafeState);
        h.Agent.Requests.Should().HaveCount(3);
        var retry = h.Agent.Requests[2];
        (Estimate(retry) + h.Loop.ToolSchemaTokens).Should().BeLessThanOrEqualTo(usable);
        ToolResults(retry)
            .Should()
            .HaveCount(6)
            .And.OnlyContain(r => r.Result.Contains("RecallConversation(tool_call_id="));
        h.Summarizer.Requests.Should().BeEmpty("unsafe_state cuts nothing");
        (await h.StoredRowsAsync()).OfType<CompactionCheckpointMessage>().Should().BeEmpty();
    }

    [Fact]
    public async Task UnderUnsafeState_TheFitCheckStillClearsOldToolResults_WithoutCutting()
    {
        // The stream of the seventh call ends early and the window resolves smaller for the retry (a route change), so
        // the retry — an interrupted turn, unsafe_state — is over the window. Nothing may be cut, but clearing old tool
        // results is view-only, so it still brings the request under the window.
        var window = 16_000L;
        var interrupted = false;
        await using var h = new Harness(
            call =>
            {
                if (call == 7 && !interrupted)
                {
                    interrupted = true;
                    window = 8_000;
                    throw new HttpIOException(HttpRequestError.ResponseEnded, "stream ended early");
                }

                return EchoThenDone(6)(call);
            },
            Options(CompactionMode.Compact) with
            {
                ClearToolResultsKeepTurns = 3,
            },
            _ => window,
            // Under the 6,320-char view cap: only clearing shortens them.
            echo: _ => new string('u', 6_000)
        );

        var completed = await h.RunAsync("start");

        completed.IsError.Should().BeFalse(Describe(h, completed));
        h.Decisions.Should().Contain(d => d.Reason == CompactionSkipReasons.UnsafeState);
        var retry = h.Agent.Requests[^1];
        Estimate(retry).Should().BeLessThanOrEqualTo(7_900);
        ToolResults(retry)
            .Count(r => r.Result.StartsWith("[Tool result cleared", StringComparison.Ordinal))
            .Should()
            .Be(5, "all but the latest tool turn are cleared");
        h.Summarizer.Requests.Should().BeEmpty("unsafe_state cuts nothing");
        (await h.StoredRowsAsync()).OfType<CompactionCheckpointMessage>().Should().BeEmpty();
    }

    [Fact]
    public async Task SecondOverflowAfterCompaction_FailsTheRunWithTheTypedReason()
    {
        await using var h = new Harness(
            call => call >= 7 ? throw ProviderOverflow() : EchoThenDone(6)(call),
            Options(CompactionMode.Compact),
            window: null
        );

        var completed = await h.RunAsync("start");

        completed.IsError.Should().BeTrue();
        completed.ErrorMessage.Should().StartWith(CompactionFailureReasons.OverflowAfterCompaction);
        completed.ErrorCode.Should().Be(CompactionFailureReasons.OverflowAfterCompaction, "machines read the code");
        h.Summarizer.Requests.Should().HaveCount(1, "the second reactive pass has nothing left to cut");
        h.Agent.Requests.Should().HaveCount(8, "one retry, never a third attempt");
        h.Publisher.Payloads<CompactionPayload>(LifecycleEventTypes.CompactionFailed)
            .Should()
            .Contain(p => p.Reason == CompactionFailureReasons.OverflowAfterCompaction);
        var last = (await ConversationContextReport.BuildAsync(h.Store, Thread, [])).Agents[0].Compaction.LastDecision;
        last!.Decision.Decision.Should().Be(CompactionDecisionKinds.Failed, "/context shows how the run ended");
        last.Decision.Reason.Should().Be(CompactionFailureReasons.OverflowAfterCompaction);
    }

    [Fact]
    public async Task AProviderThatRefusesEveryRequest_InAKnownWindow_StopsWithinTheProgressBound()
    {
        // The window is known and clearing is on, so every rung of the ladder is available: the run may clear once,
        // re-cut up to the run's limit and tighten until the floor, but each retry must have moved the view forward.
        const long usable = 8_000;
        await using var h = new Harness(
            call => call >= 7 ? throw ProviderOverflow() : EchoThenDone(6)(call),
            Options(CompactionMode.Compact) with
            {
                ClearToolResultsKeepTurns = 1,
            },
            _ => usable + 100
        );

        var completed = await h.RunAsync("start");

        completed.IsError.Should().BeTrue();
        completed.ErrorCode.Should().Be(CompactionFailureReasons.OverflowAfterCompaction);
        // After the first refusal: one clear, at most MaxFitRecutsPerRun re-cuts, and tightenings that each halve the
        // estimate at least, so no more of them than the usable window has bits.
        var tightenings = (int)Math.Ceiling(Math.Log2(usable));
        h.Agent.Requests.Count.Should()
            .BeLessThanOrEqualTo(7 + 1 + CompactionRuntime.MaxFitRecutsPerRun + tightenings, Describe(h, completed));
        h.Summarizer.Requests.Count.Should().BeLessThanOrEqualTo(CompactionRuntime.MaxFitRecutsPerRun);
    }

    [Fact]
    public async Task ContextReport_AfterTheFitCheckRefusesARequest_TheLastDecisionIsTheRefusal()
    {
        // 14 parallel 20k-char results in 11,904 usable tokens: nothing on the ladder brings the request under the window.
        Harness h = null!;
        await using var _ = h = new Harness(
            call => call == 1 ? ParallelEcho(14, 20_000) : EchoThenDone(0)(call),
            Options(CompactionMode.Compact) with
            {
                ReserveMarginTokens = 3_996,
                ClearToolResultsKeepTurns = 1,
            },
            _ => 16_000 - (h?.Loop?.ToolSchemaTokens ?? 0),
            echo: SizedEcho
        );

        var completed = await h.RunAsync("read the fourteen files");

        completed.ErrorMessage.Should().StartWith(CompactionReasons.ViewExceedsWindow);
        var last = (await ConversationContextReport.BuildAsync(h.Store, Thread, [])).Agents[0].Compaction.LastDecision;
        last!.Decision.Decision.Should().Be(CompactionDecisionKinds.Failed);
        last.Decision.Reason.Should().Be(CompactionReasons.ViewExceedsWindow);
        last.GenerationOrdinal.Should()
            .Be((await h.ObservationsAsync()).Max(o => o.GenerationOrdinal), "it supersedes the refused generation's");
        var refusedTokens = long.Parse(
            System.Text.RegularExpressions.Regex.Match(completed.ErrorMessage!, @"~(\d+) tokens").Groups[1].Value,
            System.Globalization.CultureInfo.InvariantCulture
        );
        last.Decision.Tokens.Should().Be(refusedTokens, "the refusal records the request it refused, after the ladder");
        (await h.ObservationsAsync())[^1].EstimatedInputTokens.Should().Be(refusedTokens);
    }

    [Fact]
    public async Task TransportAbort_IsNotAnOverflow_AndNeverCompacts()
    {
        await using var h = new Harness(
            call => call == 7 ? throw new HttpRequestException("connection reset") : EchoThenDone(6)(call),
            Options(CompactionMode.Compact),
            window: null
        );

        var completed = await h.RunAsync("start");

        completed.IsError.Should().BeTrue();
        completed.ErrorMessage.Should().NotContain(CompactionFailureReasons.OverflowAfterCompaction);
        h.Summarizer.Requests.Should().BeEmpty("Q1: a transport abort is not evidence of overflow");
    }

    [Theory]
    [InlineData(false, CompactionReasons.SummaryCallFailed)]
    [InlineData(true, "validation_failed:V7")]
    public async Task WhenTheSummaryModelFails_TheFitCheckReCutsWithAFallbackCheckpoint_InsteadOfRefusingTheTurn(
        bool invalid,
        string failure
    )
    {
        // The policy's attempts fail and back the next generations off; the requests keep growing past the usable
        // window during that backoff. An unhealthy summarizer must not be why the turn is refused.
        await using var h = new Harness(EchoThenDone(9), Options(CompactionMode.Compact), _ => Window);
        if (invalid)
        {
            h.Summarizer.Invalid = true;
        }
        else
        {
            h.Summarizer.Fail = _ => new InvalidOperationException("summary model down");
        }

        var completed = await h.RunAsync("start");

        completed
            .IsError.Should()
            .BeFalse(
                "decisions {0}: {1}",
                string.Join(", ", h.Decisions.Select(d => $"{d.Decision}/{d.Reason}/{d.Tokens}")),
                completed.ErrorMessage
            );
        h.Agent.Requests.Should()
            .OnlyContain(r => Estimate(r) <= Usable, "AC 7: never knowingly send beyond the reserve")
            .And.HaveCount(10);
        h.Publisher.Payloads<CompactionPayload>(LifecycleEventTypes.CompactionFailed)
            .Select(p => p.Reason)
            .Should()
            .Contain(failure, "the policy's own attempts still fail honestly")
            .And.NotContain(CompactionFailureReasons.OverflowAfterCompaction);
        var checkpoint = (await h.StoredRowsAsync()).OfType<CompactionCheckpointMessage>().Last();
        checkpoint.Stats.SummaryFallback.Should().Be(failure);
        checkpoint.Narrative.Should().Be(CheckpointPipeline.FallbackNarrative);
        var state = await h.StateAsync();
        state!.Active!.SummaryFallback.Should().Be(failure);
        state.ConsecutiveFailures.Should().BePositive("a fallback is not a healthy summary: the backoff stays");
        h.Publisher.Payloads<CompactionPayload>(LifecycleEventTypes.CompactionApplied)
            .Should()
            .Contain(p => p.Reason == CompactionReasons.SummaryFallback && p.CheckpointId == checkpoint.CheckpointId);
        (await h.ObservationsAsync())
            .Should()
            .Contain(o =>
                o.Decision != null
                && o.Decision.Reason == CompactionReasons.SummaryFallback
                && o.ActiveCheckpointId == checkpoint.CheckpointId
            );
        var active = (await ConversationContextReport.BuildAsync(h.Store, Thread, []))
            .Agents[0]
            .Compaction
            .ActiveCheckpoint;
        active!.CheckpointId.Should().Be(checkpoint.CheckpointId);
        active.SummaryFallback.Should().Be(failure);
    }

    [Fact]
    public async Task WhenThePassesOwnSummaryJustFailed_TheFitCheckFallsBackWithoutCallingTheModelAgain()
    {
        // Generation two takes the request from ~4,600 to ~9,200 tokens against 7,900 usable: the policy's cut and
        // the fit check's re-cut meet in one pass, and the pass's summary has already failed.
        await using var h = new Harness(
            call => call <= 2 ? WideEcho(call) : EchoThenDone(0)(call),
            Options(CompactionMode.Compact),
            _ => 8_000,
            echo: SizedEcho
        );
        h.Summarizer.Fail = _ => new InvalidOperationException("summary model down");

        var completed = await h.RunAsync("start");

        completed
            .IsError.Should()
            .BeFalse(
                "decisions {0}: {1}",
                string.Join(", ", h.Decisions.Select(d => $"{d.Decision}/{d.Reason}/{d.Tokens}")),
                completed.ErrorMessage
            );
        h.Agent.Requests.Should().OnlyContain(r => Estimate(r) <= 7_900);
        h.Summarizer.Requests.Should()
            .HaveCount(new CompactionOptions().SummaryAttempts, "only the policy's attempt called the model");
        (await h.StoredRowsAsync())
            .OfType<CompactionCheckpointMessage>()
            .Should()
            .ContainSingle()
            .Which.Stats.SummaryFallback.Should()
            .Be(CompactionReasons.SummaryCallFailed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DuringAFailureBackoff_TheFitCheckStillReCuts_WithTheFallbackAndNoSummaryCall(
        bool summarizerHealthy
    )
    {
        // A backoff left by earlier failures outlasts this run, so the policy never attempts a cut: only the fit
        // check's re-cut can bring the request under the window. The backoff says the summary model is not to be
        // asked yet, so the re-cut builds the fallback at once instead of spending SummaryAttempts x SummaryTimeout.
        var store = new InMemoryConversationStore();
        _ = await CompactionStateProjection.UpdateAsync(
            store,
            Thread,
            s => s with { ConsecutiveFailures = 3, FailureBackoffUntilGenerationOrdinal = 1_000 }
        );
        await using var h = new Harness(EchoThenDone(9), Options(CompactionMode.Compact), _ => Window, store: store);
        if (!summarizerHealthy)
        {
            h.Summarizer.Fail = _ => new InvalidOperationException("summary model down");
        }

        var completed = await h.RunAsync("start");

        completed
            .IsError.Should()
            .BeFalse(
                "decisions {0}: {1}",
                string.Join(", ", h.Decisions.Select(d => $"{d.Decision}/{d.Reason}/{d.Tokens}")),
                completed.ErrorMessage
            );
        h.Decisions.Should().Contain(d => d.Reason == CompactionSkipReasons.FailureBackoff);
        h.Agent.Requests.Should().OnlyContain(r => Estimate(r) <= Usable).And.HaveCount(10);
        h.Summarizer.Requests.Should().BeEmpty("a backed-off summary model is not asked");
        (await h.StoredRowsAsync())
            .OfType<CompactionCheckpointMessage>()
            .Should()
            .NotBeEmpty()
            .And.OnlyContain(
                c => c.Stats.SummaryFallback == CompactionSkipReasons.FailureBackoff,
                "the thread recorded no failure reason, so the backoff is the reason"
            );
    }

    [Fact]
    public async Task APastedInstructionLargerThanTheEnvelope_IsTrimmedOnce_AndValidatesFirstTime()
    {
        // 40,000 chars is ~10,000 tokens against a 2,385-token envelope cap in this 16k window. The summarizer quotes it
        // whole as an instruction, as a model does; that copy of the row the current instruction already quotes trimmed
        // is dropped, so the envelope holds the one trim and every attempt validates without a V9 failure or a fallback.
        const long window = 16_000;
        const long usable = window - 100;
        var instruction = "spec:" + new string('s', 40_000) + ":end";
        await using var h = new Harness(EchoThenDone(24), Options(CompactionMode.Compact), _ => window);

        var completed = await h.RunAsync(instruction);

        completed
            .IsError.Should()
            .BeFalse(
                "decisions {0}: {1}",
                string.Join(", ", h.Decisions.Select(d => $"{d.Decision}/{d.Reason}/{d.Tokens}")),
                completed.ErrorMessage
            );
        h.Agent.Requests.Should().OnlyContain(r => Estimate(r) <= usable).And.HaveCount(25);
        h.Publisher.Payloads<CompactionPayload>(LifecycleEventTypes.CompactionFailed)
            .Should()
            .BeEmpty("the first eligible attempt applies its checkpoint");
        h.Decisions.Should().NotContain(d => d.Reason == "validation_failed:V9");
        var checkpoints = (await h.StoredRowsAsync()).OfType<CompactionCheckpointMessage>().ToList();
        checkpoints.Should().NotBeEmpty().And.OnlyContain(c => c.Stats.SummaryFallback == null);
        h.Summarizer.Requests.Should().HaveSameCount(checkpoints, "each attempt summarised once and validated");
        foreach (var manifest in checkpoints.Select(c => c.Manifest))
        {
            manifest.CurrentInstruction.Should().ContainSingle().Which.Seq.Should().Be(1);
            manifest
                .CurrentInstruction[0]
                .Quote.Should()
                .StartWith("spec:")
                .And.EndWith(":end")
                .And.Contain("chars omitted; full text: RecallConversation seq 1]");
            manifest
                .Instructions.Concat(manifest.Decisions)
                .Should()
                .NotContain(q => q.Seq == 1 || q.Quote.Contains("sssss", StringComparison.Ordinal));
        }

        var cap = Options(CompactionMode.Compact).EffectiveCheckpointTokenCap(usable);
        var envelopes = h
            .Agent.Requests.Where(HasEnvelope)
            .Select(r =>
                r.OfType<TextMessage>().First(m => m.Role == Role.User && m.Text.Contains("RecallConversation"))
            )
            .ToList();
        envelopes.Should().NotBeEmpty();
        envelopes.Should().OnlyContain(e => CompactionTokenEstimate.EstimateText(e.Text) <= cap);
        (await h.StateAsync())!.ConsecutiveFailures.Should().Be(0);
    }

    [Fact]
    public async Task CompactMode_ACutThatNewlyCoversTooLittle_IsSkippedInsufficientGain_WithoutASummaryCall()
    {
        // Control: the same run compacts under the default gate, so the skip below is the gate's doing.
        await using (var control = new Harness(EchoThenDone(6), Options(CompactionMode.Compact), _ => Window))
        {
            (await control.RunAsync("start")).IsError.Should().BeFalse();
            control.Summarizer.Requests.Should().NotBeEmpty();
        }

        await using var h = new Harness(
            EchoThenDone(6),
            Options(CompactionMode.Compact) with
            {
                MinCompactionGainRatio = 0.9,
            },
            _ => Window
        );

        _ = await h.RunAsync("start");

        h.Decisions.Should().Contain(d => d.Reason == CompactionSkipReasons.InsufficientGain);
        h.Decisions.TakeWhile(d => d.Reason != CompactionSkipReasons.InsufficientGain)
            .Should()
            .NotContain(d => d.Decision == CompactionDecisionKinds.Compact && d.CheckpointId != null);
        h.Summarizer.Requests.Should().BeEmpty("no cut frees 90 % of the usable window, and the run fits without one");
    }

    [Fact]
    public async Task CompactMode_AFailedSummary_CountsAgainstTheThread_AndBacksOffTheNextGenerations()
    {
        await using var h = new Harness(
            EchoThenDone(9),
            Options(CompactionMode.Compact) with
            {
                MaxCompactionsPerRun = 20,
            },
            _ => Window
        );
        h.Summarizer.Fail = _ => new InvalidOperationException("summary model down");

        _ = await h.RunAsync("start");

        var state = await h.StateAsync();
        state!.ConsecutiveFailures.Should().BePositive();
        state.FailureBackoffUntilGenerationOrdinal.Should().NotBeNull();
        h.Decisions.Select(d => d.Reason).Should().Contain(CompactionSkipReasons.FailureBackoff);
        h.Summarizer.Requests.Count.Should()
            .BeGreaterThanOrEqualTo(2, "each failed compaction retries its summary once");
    }

    [Fact]
    public async Task CompactMode_RowsReadBehindTheStore_ReloadAndRetryOnce_InsteadOfSkippingWatermarkDrift()
    {
        var store = new DriftOnceStore(new InMemoryConversationStore(), onWatermarkRead: 2);
        await using var h = new Harness(EchoThenDone(7), Options(CompactionMode.Compact), _ => Window, store: store);

        (await h.RunAsync("start")).IsError.Should().BeFalse();

        store.Injected.Should().BeTrue("the store moved between the row read and the pipeline's watermark check");
        h.Decisions.Should().NotContain(d => d.Reason == CompactionReasons.WatermarkDrift);
        (await h.StateAsync())!.ActiveCheckpointId.Should().NotBeNull();
    }

    /// <summary>
    ///     Appends a row from another writer on the <paramref name="onWatermarkRead"/>th watermark read. The loop
    ///     reads it once to seed activity, then the pipeline reads it right before it prepares: read 2 lands
    ///     between the runtime's row load and that check.
    /// </summary>
    private sealed class DriftOnceStore(IConversationStore inner, int onWatermarkRead) : IConversationStore
    {
        private int _reads;

        public bool Injected { get; private set; }

        public async Task<long> GetMessageWatermarkAsync(string threadId, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _reads) == onWatermarkRead)
            {
                Injected = true;
                await inner.AppendMessagesAsync(
                    threadId,
                    MessagePersistenceConverter.ToPersistedMessages(
                        [new TextMessage { Text = "from another writer", Role = Role.Assistant }],
                        threadId,
                        "run-other"
                    ),
                    ct
                );
            }

            return await inner.GetMessageWatermarkAsync(threadId, ct);
        }

        public Task<IReadOnlyList<PersistedMessage>> LoadMessageRangeAsync(
            string threadId,
            long fromSeq,
            long toSeq,
            int limit,
            CancellationToken ct = default
        ) => inner.LoadMessageRangeAsync(threadId, fromSeq, toSeq, limit, ct);

        public Task UpdateMetadataAsync(
            string threadId,
            Func<ThreadMetadata?, ThreadMetadata> update,
            CancellationToken ct = default
        ) => inner.UpdateMetadataAsync(threadId, update, ct);

        public Task SaveMetadataAsync(string threadId, ThreadMetadata metadata, CancellationToken ct = default) =>
            inner.SaveMetadataAsync(threadId, metadata, ct);

        public Task<ThreadMetadata?> LoadMetadataAsync(string threadId, CancellationToken ct = default) =>
            inner.LoadMetadataAsync(threadId, ct);

        public Task AppendMessagesAsync(
            string threadId,
            IReadOnlyList<PersistedMessage> messages,
            CancellationToken ct = default
        ) => inner.AppendMessagesAsync(threadId, messages, ct);

        public Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(
            string threadId,
            CancellationToken ct = default
        ) => inner.LoadMessagesAsync(threadId, ct);

        public Task ReplaceMessageAsync(
            string threadId,
            PersistedMessage replacement,
            CancellationToken ct = default
        ) => inner.ReplaceMessageAsync(threadId, replacement, ct);

        public Task DeleteThreadAsync(string threadId, CancellationToken ct = default) =>
            inner.DeleteThreadAsync(threadId, ct);

        public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
            int limit = 50,
            int offset = 0,
            ConversationListOptions? options = null,
            CancellationToken ct = default
        ) => inner.ListThreadsAsync(limit, offset, options, ct);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARowWrittenByANewerSchema_IsNotAdopted_WhileTheSameRowAtThisSchemaStillIs(bool fromTheFuture)
    {
        // The half of the rollback contract (§8.3) the $type cannot carry. "compaction_checkpoint" does
        // not change when the schema does, so a row a newer build wrote deserializes here without
        // complaint and every manifest section this build has no field for is dropped the way any
        // unknown JSON property is. Adopting it would put a view in front of the model that looks whole
        // and is not. The false case is the control: the same row, untouched, is still adopted.
        var options = Options(CompactionMode.Compact);
        IConversationStore store;
        string checkpointId;
        var first = new Harness(EchoThenDone(7), options, _ => Window);
        try
        {
            (await first.RunAsync("start")).IsError.Should().BeFalse();
            checkpointId = (await first.StateAsync())!.ActiveCheckpointId.Should().NotBeNull().And.Subject!.ToString()!;
            store = first.Store;
        }
        finally
        {
            await first.DisposeAsync();
        }

        if (fromTheFuture)
        {
            await RewriteCheckpointSchemaAsync(store, CompactionCheckpointMessage.CurrentSchemaVersion + 1);
        }

        await using var second = new Harness(EchoThenDone(0), options, _ => Window, store: store);
        (await second.RunAsync("continue")).IsError.Should().BeFalse();

        var history = (await second.StateAsync())!.History.Where(e => e.CheckpointId == checkpointId).ToList();
        if (fromTheFuture)
        {
            history
                .Should()
                .Contain(e => e.Status == CheckpointStatus.RolledBack && e.Reason == CheckpointReasons.SchemaTooNew);
        }
        else
        {
            history
                .Should()
                .NotContain(
                    e => e.Reason == CheckpointReasons.SchemaTooNew,
                    "a row this build can read in full is adopted, or the guard says nothing"
                );
        }
    }

    /// <summary>
    /// Rewrites the thread's checkpoint row in place so it claims <paramref name="schemaVersion"/>. The
    /// edit is textual on purpose: it is exactly what a newer writer's bytes look like to this build.
    /// </summary>
    private static async Task RewriteCheckpointSchemaAsync(IConversationStore store, int schemaVersion)
    {
        var rows = await store.LoadMessagesAsync(Thread);
        var row = rows.Last(r =>
            string.Equals(r.MessageType, nameof(CompactionCheckpointMessage), StringComparison.Ordinal)
        );
        var rewritten = row.MessageJson.Replace(
            $"\"schema_version\":{CompactionCheckpointMessage.CurrentSchemaVersion}",
            $"\"schema_version\":{schemaVersion}",
            StringComparison.Ordinal
        );
        rewritten.Should().NotBe(row.MessageJson, "the row must actually carry the version this rewrites");

        await store.ReplaceMessageAsync(Thread, row with { MessageJson = rewritten });
    }

    [Fact]
    public async Task KillSwitch_SkipsDisabled_AndRollsTheActiveCheckpointBackOnTheNextRequest()
    {
        await using var h = new Harness(EchoThenDone(7), Options(CompactionMode.Compact), _ => Window);
        (await h.RunAsync("start")).IsError.Should().BeFalse();
        (await h.StateAsync())!.ActiveCheckpointId.Should().NotBeNull();
        var requestsBefore = h.Agent.Requests.Count;

        h.KillSwitch = "1";
        (await h.RunAsync("again")).IsError.Should().BeFalse();

        var afterKill = h.Decisions.Skip(requestsBefore).ToList();
        afterKill.Should().NotBeEmpty();
        afterKill
            .Should()
            .OnlyContain(d =>
                d.Decision == CompactionDecisionKinds.Skipped && d.Reason == CompactionSkipReasons.Disabled
            );
        var state = await h.StateAsync();
        state!.ActiveCheckpointId.Should().BeNull("§8.4: Active → RolledBack on the next request");
        state
            .History.Should()
            .Contain(e => e.Status == CheckpointStatus.RolledBack && e.Reason == CompactionFailureReasons.Killed);
        h.Agent.Requests.Skip(requestsBefore)
            .Should()
            .OnlyContain(r => !HasEnvelope(r), "the raw history is sent again");
    }

    [Fact]
    public async Task KillSwitch_WithAnOlderCheckpoint_DeactivatesToRawHistory_AndReEnablingNeverRestoresIt()
    {
        // §8.4: re-enable does not re-activate automatically. A rollback that fell back to the older checkpoint would
        // leave it active, and clearing the switch would silently bring that view back (#774 F-003). Fourteen tool
        // turns cross the compact band twice.
        await using var h = new Harness(EchoThenDone(14), Options(CompactionMode.Compact), _ => Window);
        (await h.RunAsync("start")).IsError.Should().BeFalse();
        var compacted = (await h.StateAsync())!
            .History.Where(e => e.Status is CheckpointStatus.Active or CheckpointStatus.Superseded)
            .Select(e => e.CheckpointId)
            .ToList();
        compacted
            .Should()
            .HaveCountGreaterThanOrEqualTo(2, "the fixture needs a checkpoint a rollback could fall back to");

        h.KillSwitch = "1";
        (await h.RunAsync("again")).IsError.Should().BeFalse();

        var killed = await h.StateAsync();
        killed!.ActiveCheckpointId.Should().BeNull("the kill deactivates to raw history, not to the older checkpoint");
        killed.LastKnownGoodCheckpointId.Should().BeNull();
        killed
            .History.Where(e => compacted.Contains(e.CheckpointId))
            .Should()
            .OnlyContain(e => e.Status == CheckpointStatus.RolledBack && e.Reason == CompactionFailureReasons.Killed);

        h.KillSwitch = null;
        (await h.RunAsync("resume")).IsError.Should().BeFalse();

        var resumed = await h.StateAsync();
        compacted
            .Should()
            .NotContain(resumed!.ActiveCheckpointId ?? string.Empty, "only a fresh compaction activates a checkpoint");
        resumed
            .History.Where(e => compacted.Contains(e.CheckpointId))
            .Should()
            .OnlyContain(e => e.Status == CheckpointStatus.RolledBack);
    }

    [Fact]
    public async Task ProviderOwnedSession_IsSkipped_WithoutTouchingTheThread()
    {
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(
            Thread,
            new ThreadMetadata
            {
                ThreadId = Thread,
                LastUpdated = 0,
                SessionMappings = new Dictionary<string, string> { ["claude"] = "session-1" },
            }
        );
        await using var h = new Harness(EchoThenDone(7), Options(CompactionMode.Compact), _ => Window, store: store);

        var completed = await h.RunAsync("start");

        completed.IsError.Should().BeFalse();
        h.Decisions.Should().OnlyContain(d => d.Reason == CompactionSkipReasons.ProviderOwnedSession);
        h.Summarizer.Requests.Should().BeEmpty();
        h.Agent.Requests.Should().OnlyContain(r => !HasEnvelope(r));
    }

    [Fact]
    public async Task NoBackgroundJob_AThresholdCrossedAtTheEndOfARunWaitsForTheNextRequest()
    {
        // Five tool turns leave the history under the compact threshold; the closing reply pushes it
        // over. Nothing may happen until the next request arrives, however long the thread sits idle.
        await using var h = new Harness(
            EchoThenDone(5, done: new string('y', 4_000)),
            Options(CompactionMode.Compact),
            _ => Window
        );

        (await h.RunAsync("start")).IsError.Should().BeFalse();
        h.Summarizer.Requests.Should().BeEmpty();
        var decisionsAfterRunOne = h.Decisions.Count;

        h.Clock.Now += TimeSpan.FromHours(1);
        await Task.Delay(200);
        h.Summarizer.Requests.Should().BeEmpty("no timer, no inactivity job");
        h.Decisions.Should().HaveCount(decisionsAfterRunOne);
        (await h.StoredRowsAsync()).Should().NotContain(m => m is CompactionCheckpointMessage);

        (await h.RunAsync("continue")).IsError.Should().BeFalse();
        h.Summarizer.Requests.Should().HaveCount(1, "the first request of the next run is where the policy runs");
        HasEnvelope(h.Agent.Requests[decisionsAfterRunOne]).Should().BeTrue();
    }

    [Fact]
    public async Task ModeOff_LeavesTheLoopByteIdentical()
    {
        await using var h = new Harness(EchoThenDone(7), Options(CompactionMode.Off), _ => Window);

        (await h.RunAsync("start")).IsError.Should().BeFalse();

        h.Decisions.Should().BeEmpty();
        h.Publisher.EventTypes.Should().NotContain(t => t.StartsWith("compaction_", StringComparison.Ordinal));

        // #681 observes every generation whether or not compaction runs, so the absence to assert here is
        // the decision stamped on that observation (§5.5), not the observation itself.
        var observed = await ContextObservationProjection.LoadLatestAsync(h.Store, Thread);
        observed.Should().NotBeNull("#681 measures every generation regardless of compaction mode");
        observed!.Decision.Should().BeNull("Off evaluates no policy, so it stamps no decision");
        observed.ActiveCheckpointId.Should().BeNull("Off never builds a checkpoint to stamp");
        (await h.StateAsync()).Should().BeNull("no compaction state is ever written in Off");
    }

    [Fact]
    public async Task RecallTool_IsOfferedInWarnMode_NeverInOff_AndAnswersNothingCompactedWithoutACheckpoint()
    {
        await using var warn = new Harness(
            call =>
                call == 1
                    ?
                    [
                        new ToolCallMessage
                        {
                            ToolCallId = "recall-1",
                            FunctionName = RecallConversationToolProvider.ToolName,
                            FunctionArgs = """{"query":"anything"}""",
                            Role = Role.Assistant,
                        },
                    ]
                    : [new TextMessage { Text = "done", Role = Role.Assistant }],
            Options(CompactionMode.Warn),
            _ => Window
        );
        await using var off = new Harness(EchoThenDone(1), Options(CompactionMode.Off), _ => Window);

        (await warn.RunAsync("start")).IsError.Should().BeFalse();
        (await off.RunAsync("start")).IsError.Should().BeFalse();

        warn.Agent.FunctionNames.Should().OnlyContain(names => names.Contains(RecallConversationToolProvider.ToolName));
        off.Agent.FunctionNames.Should().OnlyContain(names => !names.Contains(RecallConversationToolProvider.ToolName));
        var answer = (await warn.StoredRowsAsync())
            .OfType<ToolCallResultMessage>()
            .Single(r => r.ToolCallId == "recall-1");
        answer.Result.Should().Contain(RecallConversationToolProvider.NothingCompacted);
    }

    [Fact]
    public async Task RecallRoundTrip_ReturnsACompactedRowVerbatim_AsAnOrdinaryTailRow()
    {
        // Corpus (j): after the checkpoint activates, the model recalls the compacted human row by
        // keyword; the answer is a normal tool result in the tail and the next request carries it.
        await using var h = new Harness(
            call =>
                call == 8
                    ?
                    [
                        new ToolCallMessage
                        {
                            ToolCallId = "recall-1",
                            FunctionName = RecallConversationToolProvider.ToolName,
                            FunctionArgs = """{"query":"start"}""",
                            Role = Role.Assistant,
                        },
                    ]
                    : EchoThenDone(7)(call),
            Options(CompactionMode.Compact),
            _ => Window
        );

        var completed = await h.RunAsync("start");

        completed.IsError.Should().BeFalse();
        var applied = h.Publisher.Payloads<CompactionPayload>(LifecycleEventTypes.CompactionApplied).Single();
        var persisted = await h.Store.LoadMessagesAsync(Thread);
        var answerRow = persisted.Single(p =>
            p.Id == MessagePersistenceConverter.BuildToolResultPersistedId(Thread, "recall-1")
        );
        answerRow.Seq.Should().BeGreaterThan(applied.BoundarySeq!.Value, "the answer is a tail row");
        var answer = (ToolCallResultMessage)MessagePersistenceConverter.FromPersistedMessage(answerRow);
        answer.Result.Should().Contain($"\"boundary_seq\":{applied.BoundarySeq}");
        answer.Result.Should().Contain("\"seq\":1").And.Contain("\"text\":\"start\"");

        var next = h.Agent.Requests[8];
        HasEnvelope(next).Should().BeTrue();
        next.OfType<ToolsCallAggregateMessage>()
            .Should()
            .Contain(a =>
                a.ToolsCallResult.ToolCallResults.Any(r => r.ToolCallId == "recall-1" && r.Result.Contains("\"seq\":1"))
            );
    }

    [Fact]
    public async Task ChildLoop_RegistersItsOwnRecallInstance_AndNeverInheritsTheParents()
    {
        var child = new ScriptedAgent(_ => [new TextMessage { Text = "child done", Role = Role.Assistant }]);
        var subAgents = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal)
            {
                ["echo"] = new SubAgentTemplate { SystemPrompt = "You echo.", AgentFactory = () => child },
            },
            DefaultConversationStoreFactory = _ => new InMemoryConversationStore(),
        };
        await using var h = new Harness(
            EchoThenDone(0),
            Options(CompactionMode.Warn),
            _ => Window,
            subAgentOptions: subAgents
        );

        var manager = h.Loop.SubAgentManager!;
        manager
            .GetInheritableToolSnapshot()
            .Contracts.Select(c => c.Name)
            .Should()
            .Contain("Echo")
            .And.NotContain(
                RecallConversationToolProvider.ToolName,
                "the parent's instance is bound to the parent's thread"
            );

        _ = await manager.SpawnAsync("echo", "say hi");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (child.FunctionNames.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        child.FunctionNames.Should().NotBeEmpty("the child ran a turn");
        child
            .FunctionNames[0]
            .Should()
            .Contain(RecallConversationToolProvider.ToolName, "the child registered its own instance");
        child.FunctionNames[0].Should().Contain("Echo", "domain tools are still inherited");
    }

    [Fact]
    public async Task SixSubAgentCompletions_WhenTheSummaryModelFails_FallBackWithinTheEnvelopeCap_AndEachOutcomeIsRecallable()
    {
        // Six background sub-agents report ~7k chars each through the real completion path, in a 20k usable window
        // whose envelope cap is 3,000 tokens. Whole, the roster alone is ~10k tokens: only bounding each task and
        // outcome around a RecallConversation marker lets the model-free fallback checkpoint fit.
        const long usable = 20_000;
        var spawned = 0;
        var subAgents = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal)
            {
                ["reviewer"] = new SubAgentTemplate
                {
                    SystemPrompt = "You review.",
                    AgentFactory = () =>
                    {
                        var n = Interlocked.Increment(ref spawned);
                        var result = $"RESULT-{n}-HEAD " + new string('r', 5_000) + $" RESULT-{n}-TAIL";
                        return new ScriptedAgent(_ => [new TextMessage { Text = result, Role = Role.Assistant }]);
                    },
                },
            },
            DefaultConversationStoreFactory = _ => new InMemoryConversationStore(),
        };
        var final = false;
        var finalCalls = 0;
        await using var h = new Harness(
            _ =>
                !final ? [new TextMessage { Text = "noted", Role = Role.Assistant }]
                : ++finalCalls <= 3
                    ?
                    [
                        new ToolCallMessage
                        {
                            ToolCallId = $"read-{finalCalls}",
                            FunctionName = "Echo",
                            FunctionArgs = """{"size":12000}""",
                            Role = Role.Assistant,
                        },
                    ]
                : [new TextMessage { Text = "compared", Role = Role.Assistant }],
            Options(CompactionMode.Compact),
            _ => usable + 100,
            subAgentOptions: subAgents,
            echo: SizedEcho
        );
        h.Summarizer.Fail = _ => new InvalidOperationException("summary model down");
        var manager = h.Loop.SubAgentManager!;
        for (var i = 1; i <= 6; i++)
        {
            _ = await manager.SpawnAsync("reviewer", $"TASK-{i} " + new string('t', 2_000), runInBackground: true);
            var reported = i;
            await WaitUntilAsync(
                async () =>
                {
                    var rows = await h.StoredRowsAsync();
                    var last = rows.ToList().FindLastIndex(m => m is NotifyMessage);
                    return rows.OfType<NotifyMessage>().Count() == reported
                        && rows.Skip(last + 1).Any(m => m is TextMessage { Text: "noted" });
                },
                $"completion {i} to reach the parent"
            );
        }

        final = true;
        var completed = await h.RunAsync("compare the six reviews");

        completed.IsError.Should().BeFalse(Describe(h, completed));
        h.Decisions.Should().Contain(d => d.Tokens > usable, "the last generation cannot fit without a checkpoint");
        HasEnvelope(h.Agent.Requests[^1]).Should().BeTrue("the fallback checkpoint is what the model reads");
        var failures = h.Decisions.Concat(
            h.Publisher.Payloads<CompactionPayload>(LifecycleEventTypes.CompactionFailed)
        );
        failures.Select(p => p.Reason).Should().NotContain(r => r != null && r.StartsWith("validation_failed:V9"));
        var stored = await h.StoredRowsAsync();
        var checkpoints = stored.OfType<CompactionCheckpointMessage>().ToList();
        checkpoints
            .Should()
            .NotBeEmpty(string.Join(", ", h.Decisions.Select(d => $"{d.Decision}/{d.Reason}/{d.Tokens}")))
            .And.OnlyContain(c => c.Stats.SummaryFallback != null);
        var cap = Options(CompactionMode.Compact).EffectiveCheckpointTokenCap(usable);
        cap.Should().Be(3_000);
        var persisted = (await h.Store.LoadMessagesAsync(Thread)).ToList();
        var recall = new RecallConversationToolProvider(Thread, h.Store, () => null).GetFunctions().Single().Handler;
        foreach (var checkpoint in checkpoints)
        {
            CompactionTokenEstimate
                .EstimateText(checkpoint.RenderEnvelope(CheckpointRenderOptions.Default))
                .Should()
                .BeLessThanOrEqualTo(cap);
            checkpoint.Manifest.Agents.Should().HaveCount(6);
            foreach (var agent in checkpoint.Manifest.Agents)
            {
                var notify = persisted.Single(r =>
                    MessagePersistenceConverter.FromPersistedMessage(r) is NotifyMessage n
                    && n.SourceToolCallId == agent.AgentId
                );
                var detail = ((NotifyMessage)MessagePersistenceConverter.FromPersistedMessage(notify)).Detail!;
                var result = detail[
                    (detail.IndexOf("Result: ", StringComparison.Ordinal) + 8)..detail.LastIndexOf(
                        "\n</sub-agent>",
                        StringComparison.Ordinal
                    )
                ];
                agent.Outcome.Should().Contain($"full text: RecallConversation seq {notify.Seq}]");
                var read = await recall(
                    $$"""{"seq":{{notify.Seq}},"max_chars":16000}""",
                    new ToolCallContext { ToolCallId = "tc-recall" },
                    CancellationToken.None
                );
                read.Should().BeOfType<ToolHandlerResult.Resolved>().Which.Payload.Text.Should().Contain(result);
            }
        }
    }

    /// <summary>Collects every live message until cancelled; <c>RunCompletedMessage</c> proves the reader is registered.</summary>
    private static Task CollectLive(
        Harness h,
        System.Collections.Concurrent.ConcurrentQueue<IMessage> into,
        CancellationToken ct
    ) =>
        Task.Run(async () =>
        {
            try
            {
                await foreach (var message in h.Loop.SubscribeAsync(ct))
                {
                    into.Enqueue(message);
                }
            }
            catch (OperationCanceledException) { }
        });

    /// <summary>Polls until <paramref name="done"/> holds; throws, never returns quietly, when it does not.</summary>
    private static async Task WaitUntilAsync(Func<Task<bool>> done, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await done())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"timed out waiting for {what}");
            }

            await Task.Delay(20);
        }
    }

    private static IReadOnlyList<CompactionStatusMessage> Statuses(
        System.Collections.Concurrent.ConcurrentQueue<IMessage> live,
        string? requestId
    ) => [.. live.OfType<CompactionStatusMessage>().Where(s => s.RequestId == requestId)];

    /// <summary>
    /// A manual compaction runs between turns, so no run id marks the loop busy while its summary call is in
    /// flight. A reconfigure there would move, and for an owned provider dispose, the provider under that
    /// call. It is refused as busy until the compaction ends, then applies.
    /// </summary>
    [Fact]
    public async Task Reconfigure_WhileAManualCompactionIsInFlight_IsRefusedBusy_ThenApplies()
    {
        await using var h = new Harness(EchoThenDone(3), Options(CompactionMode.Compact), _ => Window);
        (await h.RunAsync("start")).IsError.Should().BeFalse();
        (await h.RunAsync("again")).IsError.Should().BeFalse();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Summarizer.Hold = ct => release.Task.WaitAsync(ct);
        AgentReconfiguration Spec() =>
            new(
                h.Agent,
                new FunctionRegistry(),
                SystemPrompt: null,
                new GenerateReplyOptions { ModelId = Model, MaxToken = 100 },
                IncludeAskUserQuestionTool: false,
                IncludeNotifyClientTool: false,
                SubAgentOptions: null
            );

        (await h.Loop.RequestCompactionAsync()).Accepted.Should().BeTrue();
        await WaitUntilAsync(() => Task.FromResult(h.Summarizer.Requests.Count > 0), "the summary call to start");

        h.Loop.Reconfigure(Spec())
            .Should()
            .Be(ReconfigureOutcome.RefusedBusy, "the manual compaction's summary call is still in flight");

        release.SetResult();
        await WaitUntilAsync(
            () => Task.FromResult(h.Loop.Reconfigure(Spec()) == ReconfigureOutcome.Applied),
            "the reconfigure to apply once the compaction ended"
        );
    }

    [Fact]
    public async Task ManualCompaction_OnAnIdleLoop_CompactsWithoutAModelTurn_AndCarriesTheFocus()
    {
        await using var h = new Harness(EchoThenDone(3), Options(CompactionMode.Compact), _ => Window);
        using var subscription = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var live = new System.Collections.Concurrent.ConcurrentQueue<IMessage>();
        var reader = CollectLive(h, live, subscription.Token);
        (await h.RunAsync("start")).IsError.Should().BeFalse();
        (await h.RunAsync("again")).IsError.Should().BeFalse();
        await WaitUntilAsync(() => Task.FromResult(live.OfType<RunCompletedMessage>().Any()), "the subscriber");
        h.Summarizer.Requests.Should().BeEmpty("nothing crossed a threshold");
        var providerCalls = h.Agent.Requests.Count;

        var result = await h.Loop.RequestCompactionAsync("  keep the Echo outputs  ");

        result.Accepted.Should().BeTrue();
        result.Status.Should().Be(ManualCompaction.StatusQueued, "no run is active");
        result.RequestId.Should().StartWith("cmp-");
        await WaitUntilAsync(
            () =>
                Task.FromResult(
                    Statuses(live, result.RequestId).Any(s => s.Phase != "requested" && s.Phase != "running")
                ),
            "the manual compaction to finish"
        );
        await subscription.CancelAsync();
        await reader;

        Statuses(live, result.RequestId)
            .Select(s => s.Phase)
            .Should()
            .Equal(["requested", "running", "applied"], "a request is announced, claimed once and applied");
        var checkpoint = (await h.StoredRowsAsync()).OfType<CompactionCheckpointMessage>().Single();
        checkpoint.Trigger.Should().Be(CompactionTrigger.Manual);
        checkpoint.Focus.Should().Be("keep the Echo outputs");
        Statuses(live, result.RequestId)[^1].CheckpointId.Should().Be(checkpoint.CheckpointId);
        Statuses(live, result.RequestId)
            .Should()
            .OnlyContain(s => s.Trigger == "manual" && s.Focus == checkpoint.Focus);
        h.Summarizer.Requests.Should().ContainSingle().Which.Focus.Should().Be("keep the Echo outputs");
        h.Agent.Requests.Should().HaveCount(providerCalls, "an idle compaction makes no provider turn");
        var state = await h.StateAsync();
        state!.PendingManual.Should().BeNull("the request is consumed");
        state.ActiveCheckpointId.Should().Be(checkpoint.CheckpointId);
        (await h.ObservationsAsync())
            .Last(o => o.Decision is not null)
            .Decision!.Reason.Should()
            .Be(CompactionRuntime.ManualReason, "the panel's last decision is the compaction that just ran");

        // The kept tail is still several rows, but it is all the tail the rules keep and nothing blocks: there is
        // nothing to compact yet. Refused now, never queued.
        (await h.StoredRowsAsync())
            .Count(m => m is not CompactionCheckpointMessage)
            .Should()
            .BeGreaterThan((int)checkpoint.Stats.RowsCovered + 1, "the row count alone would accept the request");
        var again = await h.Loop.RequestCompactionAsync();
        again
            .RefusalReason.Should()
            .Be(ManualCompactionRefusals.NothingToCompact, "every row since the checkpoint is the kept tail");
        (await h.StateAsync())!.PendingManual.Should().BeNull();

        (await h.RunAsync("continue")).IsError.Should().BeFalse();
        HasEnvelope(h.Agent.Requests[providerCalls]).Should().BeTrue("the next turn is sent on the view");
    }

    [Fact]
    public async Task ManualCompaction_OnAnIdleLoop_WhoseOnlyRunIsProtected_IsRefusedNoSafeBoundary_NotNothingToCompact()
    {
        var store = new InMemoryConversationStore();
        // One run with a mid-run correction: R4 protects all of it, and R3 keeps its end, so no cut is legal.
        var thread = new ThreadFixture()
            .Human("start")
            .ToolTurn(result: Padding)
            .ToolTurn(result: Padding)
            .Human("also keep the logs")
            .ToolTurn(result: Padding)
            .ToolTurn(result: Padding);
        await store.AppendMessagesAsync(
            Thread,
            MessagePersistenceConverter.ToPersistedMessages(thread.Messages, Thread, "run-1")
        );
        await using var h = new Harness(EchoThenDone(0), Options(CompactionMode.Compact), _ => Window, store: store);

        var result = await h.Loop.RequestCompactionAsync();

        result.RefusalReason.Should().Be(ManualCompactionRefusals.NoSafeBoundary);
        (await h.StateAsync())?.PendingManual.Should().BeNull("a refused request is not queued");
        h.Summarizer.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ManualCompaction_OnAnIdleLoop_WithAnOpenToolCall_IsRefusedNoSafeBoundary_NotNothingToCompact()
    {
        var store = new InMemoryConversationStore();
        // A short run the tail floor keeps whole, ending in a call that never got its result: the call blocks.
        var thread = new ThreadFixture().Human("start").ToolTurn().ToolCall();
        await store.AppendMessagesAsync(
            Thread,
            MessagePersistenceConverter.ToPersistedMessages(thread.Messages, Thread, "run-1")
        );
        await using var h = new Harness(EchoThenDone(0), Options(CompactionMode.Compact), _ => Window, store: store);

        var result = await h.Loop.RequestCompactionAsync();

        result.RefusalReason.Should().Be(ManualCompactionRefusals.NoSafeBoundary);
        (await h.StateAsync())?.PendingManual.Should().BeNull("a refused request is not queued");
        h.Summarizer.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ManualCompaction_DuringARun_AppliesAtTheNextStep_OnceOnly()
    {
        Harness h = null!;
        ManualCompactionResult? accepted = null;
        ManualCompactionResult? second = null;
        await using var _ = h = new Harness(
            call =>
            {
                if (call == 5)
                {
                    accepted = h.Loop.RequestCompactionAsync("the decisions").GetAwaiter().GetResult();
                    second = h.Loop.RequestCompactionAsync("again").GetAwaiter().GetResult();
                }

                return call is 5 or 6 or <= 3 ? EchoThenDone(6)(call) : EchoThenDone(0)(call);
            },
            Options(CompactionMode.Compact),
            _ => Window
        );
        using var subscription = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var live = new System.Collections.Concurrent.ConcurrentQueue<IMessage>();
        var reader = CollectLive(h, live, subscription.Token);
        (await h.RunAsync("start")).IsError.Should().BeFalse();
        await WaitUntilAsync(() => Task.FromResult(live.OfType<RunCompletedMessage>().Any()), "the subscriber");

        (await h.RunAsync("second run")).IsError.Should().BeFalse();
        (await h.RunAsync("third run")).IsError.Should().BeFalse();
        await subscription.CancelAsync();
        await reader;

        accepted!.Status.Should().Be(ManualCompaction.StatusRunning, "a run is active");
        second!.RefusalReason.Should().Be(ManualCompactionRefusals.AlreadyPending);
        HasEnvelope(h.Agent.Requests[4]).Should().BeFalse("the request arrived during this call");
        HasEnvelope(h.Agent.Requests[5]).Should().BeTrue("the run applies it before its next provider call");
        (await h.StoredRowsAsync())
            .OfType<CompactionCheckpointMessage>()
            .Should()
            .ContainSingle(c => c.Trigger == CompactionTrigger.Manual, "a request runs once");
        Statuses(live, accepted.RequestId)
            .Select(s => s.Phase)
            .Should()
            .Equal(["requested", "running", "applied"], "consumed by the first step that saw it, never again");
        (await h.StateAsync())!.PendingManual.Should().BeNull();
        h.Decisions.Should().Contain(d => d.Reason == CompactionRuntime.ManualReason);
    }

    [Fact]
    public async Task ManualCompaction_PersistedBeforeARestart_StillRunsOnce()
    {
        var store = new InMemoryConversationStore();
        await using (
            var first = new Harness(EchoThenDone(3), Options(CompactionMode.Compact), _ => Window, store: store)
        )
        {
            (await first.RunAsync("start")).IsError.Should().BeFalse();
            (await first.RunAsync("again")).IsError.Should().BeFalse();
        }

        // Accepted by a host that went away before any loop claimed it.
        _ = await CompactionStateProjection.UpdateAsync(
            store,
            Thread,
            s =>
                s with
                {
                    PendingManual = new PendingManualCompaction
                    {
                        RequestId = "cmp-restart",
                        Focus = "after restart",
                        RequestedAt = DateTimeOffset.UnixEpoch,
                    },
                }
        );

        await using var second = new Harness(
            EchoThenDone(0),
            Options(CompactionMode.Compact),
            _ => Window,
            store: store
        );
        (await second.RunAsync("continue")).IsError.Should().BeFalse();
        (await second.RunAsync("and again")).IsError.Should().BeFalse();

        (await second.StoredRowsAsync())
            .OfType<CompactionCheckpointMessage>()
            .Should()
            .ContainSingle(c => c.Trigger == CompactionTrigger.Manual)
            .Which.Focus.Should()
            .Be("after restart");
        (await second.StateAsync())!.PendingManual.Should().BeNull();
    }

    [Fact]
    public async Task ManualCompaction_IsRefused_WhenOffEmptyProviderOwnedOrAlreadyRunning()
    {
        await using (var off = new Harness(EchoThenDone(1), Options(CompactionMode.Off), _ => Window))
        {
            (await off.Loop.RequestCompactionAsync()).RefusalReason.Should().Be(ManualCompactionRefusals.CompactionOff);
            off.Loop.SupportsManualCompaction.Should().BeFalse();
        }

        await using (var warn = new Harness(EchoThenDone(1), Options(CompactionMode.Warn), _ => Window))
        {
            (await warn.Loop.RequestCompactionAsync())
                .RefusalReason.Should()
                .Be(ManualCompactionRefusals.CompactionOff);
        }

        await using (var empty = new Harness(EchoThenDone(1), Options(CompactionMode.Compact), _ => Window))
        {
            empty.Loop.SupportsManualCompaction.Should().BeTrue();
            (await empty.Loop.RequestCompactionAsync())
                .RefusalReason.Should()
                .Be(ManualCompactionRefusals.NothingToCompact);
        }

        var owned = new InMemoryConversationStore();
        await owned.SaveMetadataAsync(
            Thread,
            new ThreadMetadata
            {
                ThreadId = Thread,
                LastUpdated = 0,
                SessionMappings = new Dictionary<string, string> { ["claude"] = "session-1" },
            }
        );
        await using (
            var provider = new Harness(EchoThenDone(1), Options(CompactionMode.Compact), _ => Window, store: owned)
        )
        {
            (await provider.Loop.RequestCompactionAsync())
                .RefusalReason.Should()
                .Be(ManualCompactionRefusals.ProviderOwnedSession);
        }

        await using var busy = new Harness(EchoThenDone(3), Options(CompactionMode.Compact), _ => Window);
        (await busy.RunAsync("start")).IsError.Should().BeFalse();
        _ = await CompactionStateProjection.PrepareAsync(
            busy.Store,
            Thread,
            "cp-in-flight",
            1,
            0,
            CompactionTrigger.Preemptive,
            DateTimeOffset.UnixEpoch
        );
        (await busy.Loop.RequestCompactionAsync()).RefusalReason.Should().Be(ManualCompactionRefusals.InProgress);
        (await busy.StateAsync())!.PendingManual.Should().BeNull("a refused request is not queued");
    }

    [Fact]
    public async Task ManualCompaction_BeforeTheLoopRestores_IsNotRefusedByAnAttemptADeadProcessLeftInFlight()
    {
        var store = new InMemoryConversationStore();
        await using (
            var first = new Harness(EchoThenDone(3), Options(CompactionMode.Compact), _ => Window, store: store)
        )
        {
            (await first.RunAsync("start")).IsError.Should().BeFalse();
            (await first.RunAsync("again")).IsError.Should().BeFalse();
        }

        // The host died mid-summary: nothing reconciles the entry until a loop restores the thread.
        _ = await CompactionStateProjection.PrepareAsync(
            store,
            Thread,
            "cp-dead",
            1,
            0,
            CompactionTrigger.Preemptive,
            DateTimeOffset.UnixEpoch
        );

        await using var second = new Harness(
            EchoThenDone(0),
            Options(CompactionMode.Compact),
            _ => Window,
            store: store,
            start: false
        );
        var result = await second.Loop.RequestCompactionAsync();

        result.RefusalReason.Should().BeNull("no attempt of this process is running");
        var state = await second.StateAsync();
        state!
            .Find("cp-dead")
            .Should()
            .BeEquivalentTo(new { Status = CheckpointStatus.Rejected, Reason = CheckpointReasons.Abandoned });
        state.PendingManual!.RequestId.Should().Be(result.RequestId);
    }

    [Theory]
    [InlineData(CompactionMode.Off, false)]
    [InlineData(CompactionMode.Warn, true)]
    [InlineData(CompactionMode.Compact, true)]
    public async Task TheSystemPrompt_TellsTheModelItCannotCompact_WheneverCompactionIsOn(
        CompactionMode mode,
        bool told
    )
    {
        await using var h = new Harness(EchoThenDone(0), Options(mode), _ => Window);

        (await h.RunAsync("hi")).IsError.Should().BeFalse();

        var system = h.Agent.Requests[0].OfType<TextMessage>().Where(m => m.Role == Role.System).Select(m => m.Text);
        if (told)
        {
            system
                .Should()
                .ContainSingle()
                .Which.Should()
                .StartWith(
                    CompactionRuntime.SystemNote,
                    "host and caller instructions must retain their later, stronger prompt position"
                );
            CompactionRuntime
                .WithSystemNote("CALLER PROMPT", h.Setup, Model)
                .Should()
                .EndWith("CALLER PROMPT", "enabling compaction must retain the supplied prompt after its disclosure");
        }
        else
        {
            system.Should().NotContain(t => t.Contains("compact", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void TheRecallTool_SaysTheModelCannotCompact()
    {
        new RecallConversationToolProvider("t", new InMemoryConversationStore(), () => null)
            .GetFunctions()
            .Single()
            .Contract.Description.Should()
            .Contain("You cannot compact the conversation");
    }

    [Fact]
    public async Task AutomaticCompaction_PublishesRunningThenApplied_WithoutARequestId()
    {
        // Run one answers in text so the subscriber is provably registered before run two's seven tool turns.
        await using var h = new Harness(
            call => call == 1 ? EchoThenDone(0)(call) : EchoThenDone(8)(call),
            Options(CompactionMode.Compact),
            _ => Window
        );
        using var subscription = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var live = new System.Collections.Concurrent.ConcurrentQueue<IMessage>();
        var reader = CollectLive(h, live, subscription.Token);
        (await h.RunAsync("hello")).IsError.Should().BeFalse();
        await WaitUntilAsync(() => Task.FromResult(live.OfType<RunCompletedMessage>().Any()), "the subscriber");

        (await h.RunAsync("start")).IsError.Should().BeFalse();
        await subscription.CancelAsync();
        await reader;

        var checkpoint = (await h.StoredRowsAsync()).OfType<CompactionCheckpointMessage>().Single();
        var statuses = Statuses(live, null);
        statuses.Select(s => s.Phase).Should().Equal("running", "applied");
        statuses.Should().OnlyContain(s => s.Trigger == "preemptive" && s.Focus == null && s.ThreadId == Thread);
        statuses[^1].CheckpointId.Should().Be(checkpoint.CheckpointId);
    }

    /// <summary>Three 6,000-char Echo results per generation (~4,600 tokens): two generations never fit 7,900 usable.</summary>
    private static IReadOnlyList<IMessage> WideEcho(int call) =>
        [
            .. Enumerable
                .Range(1, 3)
                .Select(i => new ToolCallMessage
                {
                    ToolCallId = $"wide-{call}-{i}",
                    FunctionName = "Echo",
                    FunctionArgs = """{"size":6000}""",
                    Role = Role.Assistant,
                }),
        ];

    /// <summary>Six 6,000-char Echo results in one generation (~9,100 tokens): alone, more than 7,900 usable.</summary>
    private static IReadOnlyList<IMessage> WiderEcho(int call) => [.. WideEcho(call), .. WideEcho(call + 1_000)];

    /// <summary>No tighter trim can rescue a 6,000-char result: the floor is its length.</summary>
    private static CompactionOptions ReCutOptions() =>
        Options(CompactionMode.Compact) with
        {
            MaxCompactionsPerRun = 1,
            ToolResultViewCapMinChars = 6_000,
        };

    private static string Describe(Harness h, RunCompletedMessage completed) =>
        $"{completed.ErrorMessage}; decisions {string.Join(", ", h.Decisions.Select(d => $"{d.Decision}/{d.Reason}/{d.Tokens}"))}; {h.Summarizer.Requests.Count} summary calls, {h.Agent.Requests.Count} requests";

    [Fact]
    public async Task FitCheckReCut_WhileEveryReCutAdvancesTheBoundary_IsNotBoundedByTheRunBudget()
    {
        // One policy cut per run is allowed. Each generation adds more than the post-cut view has room for, so every
        // pass after the first cut needs the fit check's re-cut, and each has the previous generation to cut.
        await using var h = new Harness(
            call => call <= 8 ? WideEcho(call) : EchoThenDone(0)(call),
            ReCutOptions(),
            _ => 8_000,
            echo: SizedEcho
        );

        var completed = await h.RunAsync("start");

        completed.IsError.Should().BeFalse(Describe(h, completed));
        h.Agent.Requests.Should().HaveCount(9).And.OnlyContain(r => Estimate(r) <= 7_900);
        var boundaries = (await h.StoredRowsAsync())
            .OfType<CompactionCheckpointMessage>()
            .Select(c => c.Boundary.Seq)
            .ToList();
        boundaries
            .Should()
            .HaveCountGreaterThan(2, "more than the run budget plus the old reserve")
            .And.BeInAscendingOrder();
        boundaries.Should().OnlyHaveUniqueItems("each re-cut advanced the boundary");
        h.Summarizer.Requests.Should().HaveSameCount(boundaries);
    }

    [Fact]
    public async Task FitCheckReCut_WhenNoCutPastTheBoundaryIsLegal_RefusesTheTurn_WithoutASummaryCall()
    {
        // Generation 2 alone outgrows the window. The policy cuts generation 1; past that boundary the only rows are the
        // latest generation, which R3 keeps (a tail of at least one token), so the re-cut selects no cut. No trim can
        // shorten the 6,000-char results either.
        await using var h = new Harness(
            call =>
                call switch
                {
                    1 => WideEcho(call),
                    2 => WiderEcho(call),
                    _ => EchoThenDone(0)(call),
                },
            ReCutOptions(),
            _ => 8_000,
            echo: SizedEcho
        );

        var completed = await h.RunAsync("start");

        completed.IsError.Should().BeTrue(Describe(h, completed));
        completed.ErrorMessage.Should().StartWith(CompactionReasons.ViewExceedsWindow);
        h.Agent.Requests.Should().HaveCount(2, "the over-window request is never sent");
        (await h.StoredRowsAsync()).OfType<CompactionCheckpointMessage>().Should().ContainSingle("only the policy cut");
        h.Summarizer.Requests.Should().ContainSingle("the re-cut that selected no cut made no summary call");
    }

    [Fact]
    public async Task FitCheckReCut_StopsAtTheRunawayGuard_ThenRefuses()
    {
        await using var h = new Harness(
            call => call <= 30 ? WideEcho(call) : EchoThenDone(0)(call),
            ReCutOptions(),
            _ => 8_000,
            echo: SizedEcho
        );

        var completed = await h.RunAsync("start");

        completed.IsError.Should().BeTrue(Describe(h, completed));
        completed.ErrorMessage.Should().StartWith(CompactionReasons.ViewExceedsWindow);
        h.Agent.Requests.Should().OnlyContain(r => Estimate(r) <= 7_900, "an over-window request is never sent");
        (await h.StoredRowsAsync())
            .OfType<CompactionCheckpointMessage>()
            .Should()
            .HaveCount(1 + CompactionRuntime.MaxFitRecutsPerRun, "one policy cut, then the guard's worth of re-cuts");
        (await h.ObservationsAsync())
            .Count(o => o.Decision is { Decision: CompactionDecisionKinds.Compact, CutSeq: not null })
            .Should()
            .Be(1 + CompactionRuntime.MaxFitRecutsPerRun, "each re-cut's decision is recorded for its generation");
    }

    /// <summary><paramref name="size"/> chars of distinct ten-char lines, so any slice of the text names where it came from.</summary>
    private static string Lines(int size) => string.Concat(Enumerable.Range(0, size / 10).Select(i => $"r{i:D8}\n"));

    [Fact]
    public async Task FitCheck_AParallelToolTurnOverTheWindow_IsTrimmedHarderInTheView_SoTheRequestFits()
    {
        // Run C: six parallel results of ~55k chars, each trimmed to the ~47.9k-char view cap, are ~72k tokens against
        // 59,904 usable. R1 keeps the turn whole and clearing keeps the latest turn, so only a tighter trim can send it.
        const long window = 64_000;
        const long usable = window - 4_096;
        Harness h = null!;
        await using var _ = h = new Harness(
            call => call == 1 ? ParallelEcho(6, 55_000) : EchoThenDone(0)(call),
            Options(CompactionMode.Compact) with
            {
                ReserveMarginTokens = 3_996,
                ClearToolResultsKeepTurns = 1,
            },
            // The whole window, tool definitions included: undo the harness's schema allowance.
            _ => window - (h?.Loop?.ToolSchemaTokens ?? 0),
            echo: args =>
                Lines(int.Parse(args.Split(':')[1].TrimEnd('}'), System.Globalization.CultureInfo.InvariantCulture))
        );

        var completed = await h.RunAsync("read the six files");

        completed
            .IsError.Should()
            .BeFalse(
                "{0}; decisions {1}",
                completed.ErrorMessage,
                string.Join(", ", h.Decisions.Select(d => $"{d.Decision}/{d.Reason}/{d.Tokens}"))
            );
        h.Agent.Requests.Should().HaveCount(2);
        var last = h.Agent.Requests[^1];
        (Estimate(last) + h.Loop.ToolSchemaTokens).Should().BeLessThanOrEqualTo(usable);
        var shown = ToolResults(last).ToList();
        shown.Should().HaveCount(6);
        var normalCap = Options(CompactionMode.Compact).ToolResultViewCapChars(usable);
        shown
            .Should()
            .OnlyContain(r =>
                r.Result.Length < normalCap
                && r.Result.Contains($"RecallConversation(tool_call_id=\"{r.ToolCallId}\", offset=")
            );

        // The stored rows are untouched: the trim is the view's.
        var stored = Lines(55_000);
        (await h.StoredRowsAsync())
            .OfType<ToolCallResultMessage>()
            .Should()
            .HaveCount(6)
            .And.OnlyContain(r => r.Result == stored);
        var tightened = (await h.StateAsync())!.ToolResultsTightened;
        tightened.Should().NotBeNull();

        // The marker's offset reads back exactly what the view left out.
        var first = shown.Single(r => r.ToolCallId == "wide-1").Result;
        var offset = int.Parse(
            System.Text.RegularExpressions.Regex.Match(first, @"offset=(\d+)\)").Groups[1].Value,
            System.Globalization.CultureInfo.InvariantCulture
        );
        var omitted = stored.Substring(offset, 40);
        first.Should().NotContain(omitted);
        var recall = new RecallConversationToolProvider(Thread, h.Store, () => null).GetFunctions().Single().Handler;
        var read = await recall(
            $$"""{"tool_call_id":"wide-1","offset":{{offset}},"max_chars":2000}""",
            new ToolCallContext { ToolCallId = "tc-recall" },
            CancellationToken.None
        );
        read.Should()
            .BeOfType<ToolHandlerResult.Resolved>()
            .Which.Payload.Text.Should()
            .Contain(omitted.Replace("\n", "\\n", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FitCheck_ATightenedTrim_IsByteIdenticalOnTheNextRequest_AndAfterARestart()
    {
        // The tightened share is persisted so the prompt cache survives: the next request and a restarted loop rebuild
        // the same bytes instead of re-deriving a trim from whatever budget that request has. From the second call the
        // window grows by 1,000 tokens, so a re-derived trim would come out longer; the 40k max pins the normal cap.
        const long window = 64_000;
        var extra = 0L;
        var store = new InMemoryConversationStore();
        var options = Options(CompactionMode.Compact) with
        {
            ReserveMarginTokens = 3_996,
            ToolResultViewCapMaxChars = 40_000,
            // No automatic cut may move the wide turn out of the view between the requests compared.
            MaxCompactionsPerRun = 0,
        };
        static string Echo(string args) =>
            args.Contains("\"size\"", StringComparison.Ordinal)
                ? Lines(int.Parse(args.Split(':')[1].TrimEnd('}'), System.Globalization.CultureInfo.InvariantCulture))
                : "ok";
        static IReadOnlyList<string> Wide(IReadOnlyList<IMessage> request) =>
            [
                .. ToolResults(request)
                    .Where(r => r.ToolCallId!.StartsWith("wide-", StringComparison.Ordinal))
                    .Select(r => r.Result),
            ];

        IReadOnlyList<string> tightenedView;
        ToolResultTightening? persisted;
        Harness first = null!;
        await using (
            first = new Harness(
                call =>
                {
                    extra = call >= 2 ? 1_000 : 0;
                    return call switch
                    {
                        1 => ParallelEcho(6, 55_000),
                        2 => EchoThenDone(2)(call),
                        _ => EchoThenDone(0)(call),
                    };
                },
                options,
                _ => window + extra - (first?.Loop?.ToolSchemaTokens ?? 0),
                store: store,
                echo: Echo
            )
        )
        {
            var completed = await first.RunAsync("read the six files");
            completed.IsError.Should().BeFalse(completed.ErrorMessage);
            first.Agent.Requests.Should().HaveCount(3);
            tightenedView = Wide(first.Agent.Requests[1]);
            tightenedView.Should().HaveCount(6).And.OnlyContain(r => r.Length < 40_000, "the trim was tightened");
            Wide(first.Agent.Requests[2]).Should().Equal(tightenedView, "the next request rebuilds the same bytes");
            persisted = (await first.StateAsync())!.ToolResultsTightened;
            persisted.Should().NotBeNull();
        }

        Harness second = null!;
        await using (
            second = new Harness(
                EchoThenDone(0),
                options,
                _ => window + 1_000 - (second?.Loop?.ToolSchemaTokens ?? 0),
                store: store,
                echo: Echo
            )
        )
        {
            (await second.RunAsync("continue")).IsError.Should().BeFalse();
            Wide(second.Agent.Requests[0]).Should().Equal(tightenedView, "a restarted loop rebuilds them from state");
            (await second.StateAsync())!.ToolResultsTightened.Should().Be(persisted);
        }
    }

    [Fact]
    public async Task FitCheck_ATightenedTrim_LeavesHeadroom_SoTheNextRowsNeitherReTightenNorReCut()
    {
        // A trim tightened to fill the usable window leaves no room: the next row re-tightens (new bytes, a cache miss,
        // a smaller view every turn) or re-cuts. Aimed at 85% of the usable window, two ~1k-token rows later the view
        // still fits as it was.
        const long window = 64_000;
        const long usable = window - 4_096;
        Harness h = null!;
        await using var _ = h = new Harness(
            call =>
                call switch
                {
                    1 => ParallelEcho(6, 55_000),
                    2 or 3 =>
                    [
                        new ToolCallMessage
                        {
                            ToolCallId = $"row-{call}",
                            FunctionName = "Echo",
                            FunctionArgs = """{"size":4000}""",
                            Role = Role.Assistant,
                        },
                    ],
                    _ => EchoThenDone(0)(call),
                },
            Options(CompactionMode.Compact) with
            {
                ReserveMarginTokens = 3_996,
            },
            _ => window - (h?.Loop?.ToolSchemaTokens ?? 0),
            echo: args =>
                Lines(int.Parse(args.Split(':')[1].TrimEnd('}'), System.Globalization.CultureInfo.InvariantCulture))
        );

        var completed = await h.RunAsync("read the six files, then two more");

        completed.IsError.Should().BeFalse(Describe(h, completed));
        h.Agent.Requests.Should().HaveCount(4);
        var tightened = ContentWire(h.Agent.Requests[1]);
        ToolResults(h.Agent.Requests[1])
            .Should()
            .HaveCount(6)
            .And.OnlyContain(r => r.Result.Length < Options(CompactionMode.Compact).ToolResultViewCapChars(usable));
        foreach (var later in h.Agent.Requests.Skip(2))
        {
            ContentWire(later).Take(tightened.Count).Should().Equal(tightened, "no row re-trims or cuts the prefix");
        }

        // The wide generation's own fit check re-cut once before it tightened; the rows after it cut nothing.
        (await h.StoredRowsAsync())
            .OfType<CompactionCheckpointMessage>()
            .Should()
            .ContainSingle("only the wide generation's re-cut: {0}", Describe(h, completed));
        h.Summarizer.Requests.Should().ContainSingle();
        (Estimate(h.Agent.Requests[1]) + h.Loop.ToolSchemaTokens)
            .Should()
            .BeLessThanOrEqualTo((long)(0.85 * usable), "the trim leaves headroom");
    }

    [Fact]
    public async Task FitCheck_WhenEvenTheMinimumTrimOfEveryShownResultDoesNotFit_RefusesWithViewExceedsWindow()
    {
        // 14 parallel 20k-char results in 11,904 usable tokens: at the 4,000-char floor each is still ~1,012 tokens.
        Harness h = null!;
        await using var _ = h = new Harness(
            call => call == 1 ? ParallelEcho(14, 20_000) : EchoThenDone(0)(call),
            Options(CompactionMode.Compact) with
            {
                ReserveMarginTokens = 3_996,
                ClearToolResultsKeepTurns = 1,
            },
            _ => 16_000 - (h?.Loop?.ToolSchemaTokens ?? 0),
            echo: SizedEcho
        );

        var completed = await h.RunAsync("read the fourteen files");

        completed.IsError.Should().BeTrue();
        completed.ErrorMessage.Should().StartWith(CompactionReasons.ViewExceedsWindow);
        h.Agent.Requests.Should().HaveCount(1, "the over-window request is never sent");
        (await h.StateAsync())!.ToolResultsTightened.Should().BeNull("a trim that cannot fit is not kept");
    }

    [Fact]
    public async Task ManualCompaction_LoopTornDownWhileSummarising_IsAnnouncedAsRequestedAgain_NeverAsFailed()
    {
        // Every cancellation of a loop is teardown (the pool and the sub-agent manager cancel, stop and dispose; a UI Stop
        // cancels nothing). The request is queued again (R7), so it has not ended: a failed/cancelled frame would tell the client
        // it is gone, and a later running/applied for the same request id would contradict it.
        await using var h = new Harness(EchoThenDone(3), Options(CompactionMode.Compact), _ => Window);
        using var subscription = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var live = new System.Collections.Concurrent.ConcurrentQueue<IMessage>();
        var reader = CollectLive(h, live, subscription.Token);
        (await h.RunAsync("start")).IsError.Should().BeFalse();
        (await h.RunAsync("again")).IsError.Should().BeFalse();
        await WaitUntilAsync(() => Task.FromResult(live.OfType<RunCompletedMessage>().Any()), "the subscriber");
        var summarising = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Summarizer.Hold = async ct =>
        {
            _ = summarising.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };

        var result = await h.Loop.RequestCompactionAsync("keep it");
        await summarising.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await h.CancelAsync();

        await WaitUntilAsync(
            async () => (await h.StateAsync())?.PendingManual?.RequestId == result.RequestId,
            "the request to be queued again"
        );
        await WaitUntilAsync(
            () => Task.FromResult(Statuses(live, result.RequestId).Count(s => s.Phase == "requested") == 2),
            "the requested frame"
        );
        await subscription.CancelAsync();
        await reader;
        Statuses(live, result.RequestId).Select(s => s.Phase).Should().Equal("requested", "running", "requested");
        Statuses(live, result.RequestId).Should().OnlyContain(s => s.Reason != CompactionReasons.Cancelled);
        h.Publisher.Payloads<CompactionPayload>(LifecycleEventTypes.CompactionFailed)
            .Should()
            .NotContain(p => p.Reason == CompactionReasons.Cancelled);
    }

    [Fact]
    public async Task ManualCompaction_DisposedWhileSummarising_IsRequeued_AndTheNextLoopRunsIt()
    {
        var store = new InMemoryConversationStore();
        ManualCompactionResult result;
        var live = new System.Collections.Concurrent.ConcurrentQueue<IMessage>();
        using var subscription = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task reader;
        var first = new Harness(EchoThenDone(3), Options(CompactionMode.Compact), _ => Window, store: store);
        await using (first)
        {
            reader = CollectLive(first, live, subscription.Token);
            (await first.RunAsync("start")).IsError.Should().BeFalse();
            (await first.RunAsync("again")).IsError.Should().BeFalse();
            await WaitUntilAsync(() => Task.FromResult(live.OfType<RunCompletedMessage>().Any()), "the subscriber");
            var summarising = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            first.Summarizer.Hold = async ct =>
            {
                _ = summarising.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            };

            result = await first.Loop.RequestCompactionAsync("keep it");
            result.RefusalReason.Should().BeNull();
            await summarising.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        await subscription.CancelAsync();
        await reader;
        // Queued again, the request has not ended: the client is told it is requested, never that it failed.
        Statuses(live, result.RequestId).Select(s => s.Phase).Should().Equal("requested", "running", "requested");
        first
            .Publisher.Payloads<CompactionPayload>(LifecycleEventTypes.CompactionFailed)
            .Should()
            .NotContain(p => p.Reason == CompactionReasons.Cancelled);
        var requeued = (await CompactionStateProjection.LoadAsync(store, Thread))!.PendingManual;
        requeued.Should().NotBeNull("a claimed request that never activated is not lost with its loop");
        requeued!.RequestId.Should().Be(result.RequestId);
        requeued.Focus.Should().Be("keep it");
        (await store.LoadMessagesAsync(Thread))
            .Should()
            .NotContain(m => m.MessageType == nameof(CompactionCheckpointMessage));

        await using var second = new Harness(
            EchoThenDone(0),
            Options(CompactionMode.Compact),
            _ => Window,
            store: store
        );
        (await second.RunAsync("continue")).IsError.Should().BeFalse();
        (await second.RunAsync("and again")).IsError.Should().BeFalse();

        (await second.StoredRowsAsync())
            .OfType<CompactionCheckpointMessage>()
            .Should()
            .ContainSingle(c => c.Trigger == CompactionTrigger.Manual)
            .Which.Focus.Should()
            .Be("keep it");
        (await second.StateAsync())!.PendingManual.Should().BeNull();
        second.Publisher.Payloads<CompactionPayload>(LifecycleEventTypes.CompactionFailed).Should().BeEmpty();
    }

    [Fact]
    public async Task ManualCompaction_AStoreFaultAfterTheClaim_EndsFailed_AndNeverStaysRunning()
    {
        // The claim clears the request in its own write, so a pass that faults before its checkpoint activates leaves
        // nothing to run it again: the client must see it end, not watch "running" forever (#774 F-002).
        var store = new FaultAfterManualClaimStore(new InMemoryConversationStore());
        await using var h = new Harness(EchoThenDone(3), Options(CompactionMode.Compact), _ => Window, store: store);
        using var subscription = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var live = new System.Collections.Concurrent.ConcurrentQueue<IMessage>();
        var reader = CollectLive(h, live, subscription.Token);
        (await h.RunAsync("start")).IsError.Should().BeFalse();
        (await h.RunAsync("again")).IsError.Should().BeFalse();
        await WaitUntilAsync(() => Task.FromResult(live.OfType<RunCompletedMessage>().Any()), "the subscriber");

        var result = await h.Loop.RequestCompactionAsync("keep it");
        result.Accepted.Should().BeTrue();
        await WaitUntilAsync(
            () =>
                Task.FromResult(
                    Statuses(live, result.RequestId).Any(s => s.Phase is "applied" or "refused" or "failed")
                ),
            "the manual compaction to end"
        );
        await subscription.CancelAsync();
        await reader;

        store.Faulted.Should().BeTrue("the store failed after the request was claimed");
        Statuses(live, result.RequestId).Select(s => s.Phase).Should().Equal("requested", "running", "failed");
        Statuses(live, result.RequestId)[^1].Reason.Should().Be(CompactionReasons.PersistFailed);
        (await h.StateAsync())!.PendingManual.Should().BeNull("an ended request is not queued again");
        (await h.StoredRowsAsync()).OfType<CompactionCheckpointMessage>().Should().BeEmpty();
        (await h.Loop.RequestCompactionAsync())
            .Accepted.Should()
            .BeTrue("the faulted pass released the one-compaction-at-a-time guard");
    }

    /// <summary>Throws once from the first row load after a metadata write claims (clears) a pending manual request.</summary>
    private sealed class FaultAfterManualClaimStore(IConversationStore inner) : IConversationStore
    {
        private int _state; // 0 unarmed, 1 armed, 2 fired

        public bool Faulted => Volatile.Read(ref _state) == 2;

        public Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(
            string threadId,
            CancellationToken ct = default
        ) =>
            Interlocked.CompareExchange(ref _state, 2, 1) == 1
                ? throw new IOException("the store went away")
                : inner.LoadMessagesAsync(threadId, ct);

        public Task UpdateMetadataAsync(
            string threadId,
            Func<ThreadMetadata?, ThreadMetadata> update,
            CancellationToken ct = default
        ) =>
            inner.UpdateMetadataAsync(
                threadId,
                existing =>
                {
                    var next = update(existing);
                    if (
                        CompactionStateProjection.FromMetadata(existing)?.PendingManual is not null
                        && CompactionStateProjection.FromMetadata(next)?.PendingManual is null
                    )
                    {
                        _ = Interlocked.CompareExchange(ref _state, 1, 0);
                    }

                    return next;
                },
                ct
            );

        public Task<long> GetMessageWatermarkAsync(string threadId, CancellationToken ct = default) =>
            inner.GetMessageWatermarkAsync(threadId, ct);

        public Task<IReadOnlyList<PersistedMessage>> LoadMessageRangeAsync(
            string threadId,
            long fromSeq,
            long toSeq,
            int limit,
            CancellationToken ct = default
        ) => inner.LoadMessageRangeAsync(threadId, fromSeq, toSeq, limit, ct);

        public Task SaveMetadataAsync(string threadId, ThreadMetadata metadata, CancellationToken ct = default) =>
            inner.SaveMetadataAsync(threadId, metadata, ct);

        public Task<ThreadMetadata?> LoadMetadataAsync(string threadId, CancellationToken ct = default) =>
            inner.LoadMetadataAsync(threadId, ct);

        public Task AppendMessagesAsync(
            string threadId,
            IReadOnlyList<PersistedMessage> messages,
            CancellationToken ct = default
        ) => inner.AppendMessagesAsync(threadId, messages, ct);

        public Task ReplaceMessageAsync(
            string threadId,
            PersistedMessage replacement,
            CancellationToken ct = default
        ) => inner.ReplaceMessageAsync(threadId, replacement, ct);

        public Task DeleteThreadAsync(string threadId, CancellationToken ct = default) =>
            inner.DeleteThreadAsync(threadId, ct);

        public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
            int limit = 50,
            int offset = 0,
            ConversationListOptions? options = null,
            CancellationToken ct = default
        ) => inner.ListThreadsAsync(limit, offset, options, ct);
    }

    [Fact]
    public async Task AutomaticCompaction_CancelledWhileSummarising_EndsWithAFailedCancelledFrame()
    {
        await using var h = new Harness(
            call => call == 1 ? EchoThenDone(0)(call) : EchoThenDone(8)(call),
            Options(CompactionMode.Compact),
            _ => Window
        );
        using var subscription = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var live = new System.Collections.Concurrent.ConcurrentQueue<IMessage>();
        var reader = CollectLive(h, live, subscription.Token);
        (await h.RunAsync("hello")).IsError.Should().BeFalse();
        await WaitUntilAsync(() => Task.FromResult(live.OfType<RunCompletedMessage>().Any()), "the subscriber");
        var summarising = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Summarizer.Hold = async ct =>
        {
            _ = summarising.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };

        var run = h.RunAsync("start");
        await summarising.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await h.CancelAsync();
        try
        {
            _ = await run;
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException) { }

        await WaitUntilAsync(
            () => Task.FromResult(Statuses(live, null).Any(s => s.Phase == "failed")),
            "the terminal frame"
        );
        await subscription.CancelAsync();
        await reader;
        var statuses = Statuses(live, null);
        statuses.Select(s => s.Phase).Should().Equal("running", "failed");
        statuses[^1].Reason.Should().Be(CompactionReasons.Cancelled);
        statuses.Should().OnlyContain(s => s.Trigger == "preemptive");
    }

    [Fact]
    public async Task ManualCompaction_ThatFailsDuringARun_DoesNotFallThroughToAnAutomaticSummaryInTheSamePass()
    {
        // Control: with a failing summary model, the automatic policy first summarises after provider call K.
        int automaticAt;
        await using (var control = new Harness(EchoThenDone(7), Options(CompactionMode.Compact), _ => Window))
        {
            var counts = new List<int>();
            control.Summarizer.Fail = _ =>
            {
                counts.Add(control.Agent.Requests.Count);
                return new InvalidOperationException("summary model down");
            };
            _ = await control.RunAsync("start");
            counts.Should().NotBeEmpty();
            automaticAt = counts[0];
        }

        // The operator asks during call K, so the manual request and the automatic decision meet in one pass.
        Harness h = null!;
        var summaryCalls = new List<(int AtCall, string? Focus)>();
        await using var harness = h = new Harness(
            call =>
            {
                if (call == automaticAt)
                {
                    h.Loop.RequestCompactionAsync("keep it").GetAwaiter().GetResult().Accepted.Should().BeTrue();
                }

                return EchoThenDone(7)(call);
            },
            Options(CompactionMode.Compact),
            _ => Window
        );
        h.Summarizer.Fail = r =>
        {
            summaryCalls.Add((h.Agent.Requests.Count, r.Focus));
            return new InvalidOperationException("summary model down");
        };

        _ = await h.RunAsync("start");

        var thatPass = summaryCalls.Where(c => c.AtCall == automaticAt).ToList();
        thatPass
            .Should()
            .HaveCount(new CompactionOptions().SummaryAttempts, "only the manual attempt summarises in that pass")
            .And.OnlyContain(c => c.Focus == "keep it");
    }

    [Fact]
    public async Task CheckpointStats_BeforeAndAfter_AreTheSameKindOfEstimate_EvenWhenTheProviderMeasuredMore()
    {
        // The provider counts 3,000 prompt tokens for call 3, far above the character estimate. The policy acts on
        // that number, but the divider's "saved" must not include the heuristic's undercount.
        const long measured = 3_000;
        await using var h = new Harness(
            call =>
                call == 3
                    ?
                    [
                        .. EchoThenDone(6)(call),
                        new UsageMessage
                        {
                            Usage = new Usage
                            {
                                PromptTokens = (int)measured,
                                CompletionTokens = 10,
                                TotalTokens = (int)measured + 10,
                            },
                        },
                    ]
                    : EchoThenDone(6)(call),
            Options(CompactionMode.Compact),
            _ => Window
        );

        _ = await h.RunAsync("start");

        var applied = h.Decisions.First(d => d.CheckpointId is not null);
        applied.Tokens.Should().BeGreaterThanOrEqualTo(measured, "the decision was calibrated by the measurement");
        var checkpoint = (await h.StoredRowsAsync())
            .OfType<CompactionCheckpointMessage>()
            .First(c => c.CheckpointId == applied.CheckpointId);
        checkpoint
            .Stats.EstimatedTokensBefore.Should()
            .BeLessThan(measured, "before is the character estimate, like after");
    }

    [Fact]
    public async Task ContextReport_AfterAnAutomaticCut_ShowsTheActiveCheckpointsStats_AtTheOrdinalItsDecisionWasRecordedAt()
    {
        // The panel shows the post-cut size while the last decision is no newer than the active checkpoint, so the
        // two ordinals must name the same generation.
        await using var h = new Harness(EchoThenDone(7), Options(CompactionMode.Compact), _ => Window);
        var before = await ConversationContextReport.BuildAsync(h.Store, Thread, []);
        before.Agents[0].Compaction.ActiveCheckpoint.Should().BeNull("nothing is active yet");

        (await h.RunAsync("start")).IsError.Should().BeFalse();

        var checkpoint = (await h.StoredRowsAsync()).OfType<CompactionCheckpointMessage>().Single();
        var active = (await ConversationContextReport.BuildAsync(h.Store, Thread, []))
            .Agents[0]
            .Compaction
            .ActiveCheckpoint;
        active.Should().NotBeNull();
        active!.CheckpointId.Should().Be(checkpoint.CheckpointId);
        active.EstimatedTokensBefore.Should().Be(checkpoint.Stats.EstimatedTokensBefore);
        active.EstimatedTokensAfter.Should().Be(checkpoint.Stats.EstimatedTokensAfter);
        var cut = (await h.ObservationsAsync()).Single(o =>
            o.Decision is { Decision: CompactionDecisionKinds.Compact }
            && o.ActiveCheckpointId == checkpoint.CheckpointId
        );
        active
            .GenerationOrdinal.Should()
            .Be(cut.GenerationOrdinal, "the cut's own decision is recorded at that ordinal");
    }

    [Fact]
    public async Task ContextReport_AfterAnIdleManualCut_TheLastDecisionAndTheActiveCheckpointNameTheSameOrdinal()
    {
        await using var h = new Harness(EchoThenDone(3), Options(CompactionMode.Compact), _ => Window);
        (await h.RunAsync("start")).IsError.Should().BeFalse();
        (await h.RunAsync("again")).IsError.Should().BeFalse();

        (await h.Loop.RequestCompactionAsync()).Accepted.Should().BeTrue();
        // The decision is recorded after the checkpoint activates, so it is the last thing to land.
        await WaitUntilAsync(
            async () =>
                (await ConversationContextReport.BuildAsync(h.Store, Thread, []))
                    .Agents[0]
                    .Compaction
                    .LastDecision
                    ?.Decision
                    .Reason == CompactionRuntime.ManualReason,
            "the manual decision to be recorded"
        );

        var checkpoint = (await h.StoredRowsAsync()).OfType<CompactionCheckpointMessage>().Single();
        var compaction = (await ConversationContextReport.BuildAsync(h.Store, Thread, [])).Agents[0].Compaction;
        compaction.LastDecision!.Decision.Reason.Should().Be(CompactionRuntime.ManualReason);
        compaction.ActiveCheckpoint!.CheckpointId.Should().Be(checkpoint.CheckpointId);
        compaction.ActiveCheckpoint.EstimatedTokensAfter.Should().Be(checkpoint.Stats.EstimatedTokensAfter);
        compaction.ActiveCheckpoint.GenerationOrdinal.Should().Be(compaction.LastDecision.GenerationOrdinal);
    }

    [Theory]
    [InlineData(typeof(ClaudeAgentLoop))]
    [InlineData(typeof(CodexAgentLoop))]
    [InlineData(typeof(CopilotAgentLoop))]
    public void ProviderOwnedLoops_HaveNoCompactionParameter(Type loop)
    {
        var parameters = loop.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType);

        parameters
            .Should()
            .NotContain(typeof(CompactionSetup), "a provider-owned session is never compacted by the harness");
    }
}
