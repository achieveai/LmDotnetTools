using System.Diagnostics;
using System.Net;
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
using AchieveAi.LmDotnetTools.LmMultiTurn.TodoBoard;
using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;
using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;
using LmMultiTurn.Tests.Lifecycle;
using LmMultiTurn.Tests.Persistence;

namespace LmMultiTurn.Tests.Compaction.Corpus;

/// <summary>The fixed public price list the corpus runs under (D4): Sonnet-like rates.</summary>
internal sealed class CorpusPricingResolver(CorpusPricing pricing) : IPricingResolver
{
    public ModelPricing? Resolve(string modelId) =>
        pricing switch
        {
            CorpusPricing.None => null,
            CorpusPricing.NoCacheRates => new ModelPricing
            {
                ModelId = modelId,
                PromptPerMillion = 3m,
                CompletionPerMillion = 15m,
            },
            _ => new ModelPricing
            {
                ModelId = modelId,
                PromptPerMillion = 3m,
                CompletionPerMillion = 15m,
                CacheReadPerMillion = 0.3m,
                CacheWrite5mPerMillion = 3.75m,
            },
        };
}

/// <summary>
/// The corpus's summariser: quotes the current instruction whole, quotes the first assistant reply it
/// covers as a decision, records one artifact, headlines every run and reports every completed agent -
/// so every protected-state class (spec §2.6) has something on the manifest for the evaluator to check.
/// Passes V1-V9 unless a fault is injected.
/// </summary>
internal sealed class CorpusSummarizer : ICheckpointSummarizer
{
    /// <summary>Every summary handed back, by thread: the evaluator checks each one reached a manifest.</summary>
    public List<(string ThreadId, CheckpointSummary Summary)> Summaries { get; } = [];

    public const string ArtifactPath = "notes/plan.md";

    public List<CheckpointSummaryRequest> Requests { get; } = [];

    public Func<CheckpointSummaryRequest, Exception?> Fail { get; set; } = _ => null;

    /// <summary>Runs while the summary is "in flight" - the seam for a concurrent append (spec §3.4 V2).</summary>
    public Func<CheckpointSummaryRequest, Task>? WhileSummarizing { get; set; }

    public async Task<CheckpointSummaryResponse> SummarizeAsync(
        CheckpointSummaryRequest request,
        CancellationToken ct = default
    )
    {
        Requests.Add(request);
        if (Fail(request) is { } failure)
        {
            throw failure;
        }

        if (WhileSummarizing is { } hook)
        {
            await hook(request);
        }

        var decision = request.Rows.FirstOrDefault(r => r.Message is TextMessage { Role: Role.Assistant });
        var summary = new CheckpointSummary
        {
            Instructions =
            [
                .. request.CurrentInstruction.Select(r => new QuotedItem
                {
                    Seq = r.Seq,
                    Quote = (r.Message as TextMessage)?.Text ?? string.Empty,
                }),
            ],
            Decisions = decision is null
                ? []
                : [new QuotedItem { Seq = decision.Seq, Quote = ((TextMessage)decision.Message).Text }],
            Artifacts =
                request.Rows.Count == 0
                    ? []
                    : [new ArtifactRef { Path = ArtifactPath, OriginSeq = request.Rows[0].Seq }],
            Headlines = request.RunIds.ToDictionary(id => id, _ => "ran the tools", StringComparer.Ordinal),
            AgentOutcomes = request
                .Roster.Where(a => string.Equals(a.Status, "Completed", StringComparison.Ordinal))
                .ToDictionary(a => a.AgentId, _ => "finished", StringComparer.Ordinal),
            Narrative = $"Summarised {request.Rows.Count} rows across {request.RunIds.Count} runs.",
        };
        Summaries.Add((request.ThreadId, summary));
        return new CheckpointSummaryResponse(
            summary,
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

internal sealed class CorpusClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Everything one corpus run left behind, for the evaluator and for tests that look closer.</summary>
internal sealed record CorpusRunData
{
    public required CorpusScenario Scenario { get; init; }

    public required CompactionMode Mode { get; init; }

    public required IReadOnlyList<RunOutcome> Runs { get; init; }

    /// <summary>Root provider calls made by the end of each scenario step, in step order (D7 park proof).</summary>
    public required IReadOnlyList<int> CallsAtStep { get; init; }

    /// <summary>What the summariser returned for the root thread, in order.</summary>
    public required IReadOnlyList<CheckpointSummary> RootSummaries { get; init; }

    public required ScriptedProvider Root { get; init; }

    public required IReadOnlyList<ScriptedProvider> Children { get; init; }

    public required IReadOnlyList<PersistedMessage> RootRows { get; init; }

    public required IReadOnlyList<IMessage> RootMessages { get; init; }

    public required CompactionState? RootState { get; init; }

    public required IReadOnlyDictionary<
        string,
        (IReadOnlyList<PersistedMessage> Rows, CompactionState? State)
    > ChildThreads { get; init; }

    public required IReadOnlyList<SubAgentSnapshot> Roster { get; init; }

    public required TodoBoardSnapshot? Board { get; init; }

    public required ConversationUsageAggregate? Usage { get; init; }

    public required IReadOnlyList<UsageRecord> UsageRecords { get; init; }

    public required IReadOnlyList<CompactionPayload> Decided { get; init; }

    public required IReadOnlyList<CompactionPayload> Applied { get; init; }

    public required IReadOnlyList<StoreCall> CrossThread { get; init; }

    public required ThreadMetadata? Metadata { get; init; }

    public required IReadOnlyList<RunLedgerEntry> Ledger { get; init; }

    public required long LatencyMs { get; init; }
}

/// <summary>
/// Runs one <see cref="CorpusScenario"/> through a real <see cref="MultiTurnAgentLoop"/> in one mode: the
/// scripted provider, the summariser, the shared store behind per-thread handles, the sub-agent
/// templates, the wait/ask tools and the lifecycle recorder are all wired the way a host would wire
/// them, and nothing about the loop is stubbed.
/// </summary>
internal sealed class CorpusRunner : IAsyncDisposable
{
    public const string RootThread = "corpus-root";
    private const string Padding = "x";

    private static readonly string EchoPadding = new(Padding[0], 1_200);

    private readonly CorpusScenario _scenario;
    private readonly ConversationStoreHarness _harness;
    private readonly List<RunCompletedMessage> _completions = [];
    private readonly List<RunOutcome> _runs = [];
    private readonly List<int> _callsAtStep = [];
    private readonly Dictionary<string, SubAgentSnapshot> _roster = new(StringComparer.Ordinal);
    private readonly List<ScriptedProvider> _children = [];
    private readonly Func<CompactionSetup, CompactionSetup>? _configureSetup;
    private int _expectedRuns;
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private Drain? _drain;
    private bool _initialized;
    private bool _featureOff;

    public CorpusRunner(
        CorpusScenario scenario,
        CompactionMode mode,
        ConversationStoreHarness harness,
        CompactionOptions? options = null,
        Func<CompactionSetup, CompactionSetup>? configureSetup = null,
        bool stripSeqOnLoad = false
    )
    {
        _scenario = scenario;
        _harness = harness;
        _configureSetup = configureSetup;
        Mode = mode;
        Options = options ?? DefaultOptions(mode);
        Inner = scenario.Store == "file-legacy" ? harness.Open("file") : new InMemoryConversationStore();
        RootStore = new ThreadScopedStore(Inner, RootThread, Log, stripSeqOnLoad);
        Root = new ScriptedProvider("root", scenario.Root, Gates, scenario.WindowTokens);
    }

    public CompactionMode Mode { get; }

    public CompactionOptions Options { get; }

    public IConversationStore Inner { get; private set; }

    public ThreadScopedStore RootStore { get; private set; }

    public ScriptedProvider Root { get; }

    public IReadOnlyList<ScriptedProvider> Children => _children;

    public CorpusSummarizer Summarizer { get; } = new();

    public RecordingLifecyclePublisher Publisher { get; } = new();

    public CorpusClock Clock { get; } = new();

    public CorpusGates Gates { get; } = new();

    public StoreCallLog Log { get; } = new();

    public MultiTurnAgentLoop? Loop { get; private set; }

    public CompactionSetup? Setup { get; private set; }

    /// <summary>What the loop reads for <c>LMMULTITURN_COMPACTION_DISABLED</c>; null = unset.</summary>
    public string? KillSwitchEnv { get; set; }

    public IReadOnlyList<RunOutcome> Runs => _runs;

    public static CompactionOptions DefaultOptions(CompactionMode mode) =>
        new()
        {
            Mode = mode,
            ReserveMarginTokens = 0,
            MinTailTokens = 100,
            CooldownGenerations = 0,
            CooldownNewTokens = 0,
            CacheTtl = TimeSpan.Zero,
            MaxCompactionsPerRun = 20,
        };

    /// <summary>Seeds the store (k) and starts the loop. Idempotent.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!_initialized)
        {
            _initialized = true;
            if (_scenario.LegacyRows.Count > 0)
            {
                var rows = _scenario
                    .LegacyRows.Select(
                        (text, i) =>
                            MessagePersistenceConverter.ToPersistedMessage(
                                new TextMessage
                                {
                                    Text = text,
                                    Role = i % 2 == 0 ? Role.User : Role.Assistant,
                                    RunId = $"legacy-run-{(i / 2) + 1}",
                                    ThreadId = RootThread,
                                },
                                RootThread,
                                $"legacy-run-{(i / 2) + 1}"
                            ) with
                            {
                                Id = $"legacy-{i + 1}",
                                MessageOrderIdx = i,
                                Timestamp = 1_000 + i,
                            }
                    )
                    .ToList();
                await _harness.SeedLegacyRowsAsync("file", RootThread, rows);
                Inner = _harness.Reopen("file");
                // A thread the old binary wrote has its metadata row; without one the loop recovers nothing.
                await Inner.SaveMetadataAsync(
                    RootThread,
                    new ThreadMetadata
                    {
                        ThreadId = RootThread,
                        LatestRunId = $"legacy-run-{((rows.Count - 1) / 2) + 1}",
                        LastUpdated = 1_000 + rows.Count,
                    },
                    ct
                );
                RootStore = new ThreadScopedStore(Inner, RootThread, Log);
            }
        }

        if (Loop is null)
        {
            StartLoop();
        }
    }

    /// <summary>Runs every step of the scenario and evaluates what it left behind.</summary>
    public async Task<(ScenarioModeResult Result, CorpusRunData Data)> RunAsync(CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        await StartAsync(ct);
        foreach (var step in _scenario.Steps)
        {
            await ExecuteAsync(step, ct);
            _callsAtStep.Add(Root.CallCount);
        }

        await StopLoopAsync();
        stopwatch.Stop();
        var data = await CollectAsync(stopwatch.ElapsedMilliseconds, ct);
        return (CorpusEvaluator.Evaluate(data), data);
    }

    /// <summary>Sends one user turn and waits for its run (and only its run) to complete.</summary>
    public async Task<RunOutcome> SayAsync(string text, CancellationToken ct = default, bool expectError = false)
    {
        await StartAsync(ct);
        return await ExecuteSayAsync(CorpusStep.Say(text, expectError), ct);
    }

    public async Task RestartAsync(CancellationToken ct = default)
    {
        await StopLoopAsync();
        StartLoop();
        await Task.CompletedTask;
    }

    /// <summary>
    /// Restarts the loop with a different compaction setup: the rollout seam. Returning null from
    /// <paramref name="reconfigure"/> brings the next loop up with no compaction at all (feature off).
    /// </summary>
    public async Task RestartWithAsync(
        Func<CompactionSetup, CompactionSetup?> reconfigure,
        CancellationToken ct = default
    )
    {
        await StopLoopAsync();
        var next = reconfigure(Setup ?? Configure(NewSetup()));
        _featureOff = next is null;
        Setup = next;
        StartLoop();
        await Task.CompletedTask;
    }

    public async Task ExecuteAsync(CorpusStep step, CancellationToken ct)
    {
        switch (step.Kind)
        {
            case "say":
                _ = await ExecuteSayAsync(step, ct);
                break;
            case "board":
                await ConversationTodoProjection.SaveAsync(
                    RootStore,
                    new TodoBoardSnapshot
                    {
                        ThreadId = RootThread,
                        CapturedAtUtc = Clock.Now,
                        Tasks =
                        [
                            .. _scenario.BoardTasks.Select(
                                (title, i) =>
                                    new TodoTaskNode
                                    {
                                        Id = $"{i + 1}",
                                        Status = i == 0 ? TodoTaskStatus.InProgress : TodoTaskStatus.NotStarted,
                                        Title = title,
                                    }
                            ),
                        ],
                    },
                    ct
                );
                break;
            case "resolve":
                await Loop!.ResolveToolCallAsync(step.Id!, step.Text!, ct: ct);
                _expectedRuns++;
                await WaitForRunsAsync(_expectedRuns, ct);
                break;
            case "await_runs":
                _expectedRuns += step.Runs;
                await WaitForRunsAsync(_expectedRuns, ct);
                break;
            case "release":
                Gates.Release(step.Id!);
                _expectedRuns += step.Runs;
                await WaitForRunsAsync(_expectedRuns, ct);
                break;
            case "restart":
                await RestartAsync(ct);
                break;
            default:
                throw new NotSupportedException($"unknown corpus step '{step.Kind}'");
        }
    }

    private async Task<RunOutcome> ExecuteSayAsync(CorpusStep step, CancellationToken ct)
    {
        var loop = Loop!;
        var runTask = Task.Run(
            async () =>
            {
                RunCompletedMessage? completed = null;
                await foreach (
                    var message in loop.ExecuteRunAsync(
                        new UserInput([new TextMessage { Text = step.Text!, Role = Role.User }]),
                        ct
                    )
                )
                {
                    if (message is RunCompletedMessage done)
                    {
                        completed = done;
                    }
                }

                return completed ?? throw new InvalidOperationException("run never completed");
            },
            ct
        );

        if (step.Inject is not null)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (Root.CallCount < step.AfterCall && !runTask.IsCompleted && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10, ct);
            }

            _ = await loop.SendAsync(new UserInput([new TextMessage { Text = step.Inject, Role = Role.User }]), ct);
        }

        var completed = await runTask.WaitAsync(TimeSpan.FromSeconds(60), ct);
        var outcome = new RunOutcome(
            step.Text!,
            completed.CompletedRunId,
            completed.IsError,
            completed.ErrorMessage,
            step.ExpectError
        );
        _runs.Add(outcome);
        _expectedRuns++;
        await WaitForRunsAsync(_expectedRuns, ct);
        return outcome;
    }

    private async Task WaitForRunsAsync(int expected, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            lock (_completions)
            {
                if (_completions.Count >= expected)
                {
                    return;
                }
            }

            if (_runTask is { IsFaulted: true })
            {
                await _runTask;
            }

            if (DateTime.UtcNow > deadline)
            {
                int seen;
                lock (_completions)
                {
                    seen = _completions.Count;
                }

                throw new TimeoutException($"expected {expected} run completions, saw {seen}");
            }

            await Task.Delay(20, ct);
        }
    }

    private void StartLoop()
    {
        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var registry = new FunctionRegistry().AddFunction(
            new FunctionContract
            {
                Name = "Echo",
                Description = "Returns padding",
                Parameters = [],
            },
            (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText(EchoPadding))
        );

        if (!_featureOff)
        {
            Setup ??= Configure(NewSetup());
        }

        SubAgentOptions? subAgents = null;
        if (_scenario.Children.Count > 0)
        {
            subAgents = new SubAgentOptions
            {
                Templates = _scenario.Children.ToDictionary(
                    kv => kv.Key,
                    kv => new SubAgentTemplate
                    {
                        SystemPrompt = $"You are the {kv.Key} agent.",
                        Role = kv.Key,
                        AgentFactory = () =>
                        {
                            var child = new ScriptedProvider(kv.Key, kv.Value, Gates, _scenario.WindowTokens);
                            lock (_children)
                            {
                                _children.Add(child);
                            }

                            return child;
                        },
                        DefaultOptions = new GenerateReplyOptions { ModelId = _scenario.ModelId, MaxToken = 100 },
                    },
                    StringComparer.Ordinal
                ),
                DefaultConversationStoreFactory = threadId => new ThreadScopedStore(Inner, threadId, Log),
            };
        }

        AgentCollaborationSetup? collaboration = null;
        if (_scenario.WorkflowController)
        {
            var root = AgentCollaborationSetup.CreateRoot(new AgentCollaborationOptions());
            var context = root.Context.CreateChild(
                "workflow-controller",
                AgentKind.WorkflowController,
                "controller",
                "runs the workflow"
            );
            collaboration = root.ForChild(context, "controller");
        }

        Loop = new MultiTurnAgentLoop(
            Root,
            registry,
            RootThread,
            includeAskUserQuestionTool: _scenario.IncludeAskUserQuestionTool,
            includeNotifyClientTool: false,
            defaultOptions: new GenerateReplyOptions { ModelId = _scenario.ModelId, MaxToken = 100 },
            store: RootStore,
            subAgentOptions: subAgents,
            triggerOptions: _scenario.IncludeWaitTool ? new TriggerOptions() : null,
            pricingResolver: new CorpusPricingResolver(_scenario.Pricing),
            lifecycleServices: new MultiTurnLifecycleServices { Publisher = Publisher },
            collaboration: collaboration,
            compaction: _featureOff ? null : Setup
        );
        _runTask = Loop.RunAsync(_cts.Token);
        _drain = LoopSubscription.StartDraining(
            Loop,
            message =>
            {
                if (message is RunCompletedMessage completed)
                {
                    lock (_completions)
                    {
                        _completions.Add(completed);
                    }
                }
            },
            _cts.Token
        );
    }

    private CompactionSetup Configure(CompactionSetup setup) => _configureSetup?.Invoke(setup) ?? setup;

    private CompactionSetup NewSetup() =>
        new()
        {
            Options = Options,
            Summarizer = Summarizer,
            ResolveWindowTokens = _ => _scenario.WindowTokens,
            ProviderId = "corpus",
            Clock = Clock,
            ReadEnvironment = _ => KillSwitchEnv,
            IsContextOverflow = ex => ex is HttpRequestException { StatusCode: HttpStatusCode.BadRequest },
        };

    private async Task StopLoopAsync()
    {
        if (Loop is null)
        {
            return;
        }

        SnapshotRoster();
        await _cts!.CancelAsync();
        try
        {
            await _runTask!;
        }
        catch (OperationCanceledException) { }

        await Loop.DisposeAsync();
        _cts.Dispose();
        Loop = null;
        _runTask = null;
        _drain = null;
    }

    private void SnapshotRoster()
    {
        if (Loop?.SubAgentManager is { } manager)
        {
            foreach (var agent in manager.ListAgents())
            {
                _roster[agent.AgentId] = agent;
            }
        }
    }

    public IReadOnlyList<SubAgentSnapshot> Roster
    {
        get
        {
            SnapshotRoster();
            return [.. _roster.Values];
        }
    }

    public async Task<CorpusRunData> CollectAsync(long latencyMs, CancellationToken ct = default)
    {
        await StopLoopAsync();
        var records = await SettledUsageRecordsAsync(ct);
        var rootRows = await Inner.LoadMessagesAsync(RootThread, ct);
        var childThreads = new Dictionary<string, (IReadOnlyList<PersistedMessage>, CompactionState?)>(
            StringComparer.Ordinal
        );
        foreach (var agent in _roster.Values)
        {
            childThreads[agent.ThreadId] = (
                await Inner.LoadMessagesAsync(agent.ThreadId, ct),
                await CompactionStateProjection.LoadAsync(Inner, agent.ThreadId, ct)
            );
        }

        return new CorpusRunData
        {
            Scenario = _scenario,
            Mode = Mode,
            Runs = [.. _runs],
            CallsAtStep = [.. _callsAtStep],
            RootSummaries =
            [
                .. Summarizer
                    .Summaries.Where(s => string.Equals(s.ThreadId, RootThread, StringComparison.Ordinal))
                    .Select(s => s.Summary),
            ],
            Root = Root,
            Children = [.. _children],
            RootRows = rootRows,
            RootMessages = MessagePersistenceConverter.FromPersistedMessagesResilient(rootRows),
            RootState = await CompactionStateProjection.LoadAsync(Inner, RootThread, ct),
            ChildThreads = childThreads,
            Roster = [.. _roster.Values],
            Board = await ConversationTodoProjection.LoadAsync(Inner, RootThread, ct),
            Usage = await ConversationUsageProjection.LoadAsync(Inner, RootThread, ct),
            UsageRecords = records,
            Decided = Publisher.Payloads<CompactionPayload>(LifecycleEventTypes.CompactionDecided),
            Applied = Publisher.Payloads<CompactionPayload>(LifecycleEventTypes.CompactionApplied),
            CrossThread = Log.CrossThreadRowAccess,
            Metadata = await Inner.LoadMetadataAsync(RootThread, ct),
            Ledger = await ((IRunLedgerStore)Inner).ListRunLedgerAsync(RootThread, ct),
            LatencyMs = latencyMs,
        };
    }

    /// <summary>Usage is persisted fire-and-forget; wait until the record count stops moving.</summary>
    private async Task<IReadOnlyList<UsageRecord>> SettledUsageRecordsAsync(CancellationToken ct)
    {
        var records = await ConversationUsageProjection.LoadRecordsAsync(Inner, RootThread, ct);
        var stable = 0;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (stable < 3 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, ct);
            var next = await ConversationUsageProjection.LoadRecordsAsync(Inner, RootThread, ct);
            stable = next.Count == records.Count ? stable + 1 : 0;
            records = next;
        }

        return records;
    }

    public async ValueTask DisposeAsync() => await StopLoopAsync();
}
