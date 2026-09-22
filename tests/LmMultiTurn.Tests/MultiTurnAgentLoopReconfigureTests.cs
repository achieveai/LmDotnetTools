using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;
using AchieveAi.LmDotnetTools.LmTestUtils;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Pins <see cref="MultiTurnAgentLoop.Reconfigure"/>: a mode or model switch must change what the NEXT
/// turn sends — provider, model id, system prompt, tool surface — without disposing the loop, because
/// disposal is what kills running sub-agents, armed Waits and anything already queued.
/// </summary>
/// <remarks>
/// Every test here asserts on the seam the host actually observes: the options and contracts that reach
/// the provider on the next turn, and the identity/liveness of the collaborators the loop keeps. None of
/// them reach into private state.
/// </remarks>
public class MultiTurnAgentLoopReconfigureTests
{
    private const string OldModel = "old-model";
    private const string NewModel = "new-model";

    /// <summary>
    /// The whole point of the feature: a child that is mid-run when the host switches mode keeps running,
    /// under the same manager, and is never stamped with the host-shutdown failure code that disposal
    /// applies.
    /// </summary>
    [Fact]
    public async Task Reconfigure_KeepsTheSubAgentManagerAndLeavesARunningChildAlone()
    {
        var childEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var subAgentOptions = new SubAgentOptions
        {
            MaxConcurrentSubAgents = 2,
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["worker"] = new()
                {
                    Name = "worker",
                    SystemPrompt = "You are a worker.",
                    AgentFactory = () => new BlockingProvider(childEntered, childRelease.Task),
                },
            },
        };

        var provider = new ScriptedProvider();
        await using var loop = new MultiTurnAgentLoop(
            provider,
            new FunctionRegistry(),
            threadId: "reconfigure-subagent",
            defaultOptions: new GenerateReplyOptions { ModelId = OldModel },
            subAgentOptions: subAgentOptions
        );

        var manager = loop.SubAgentManager.Should().NotBeNull().And.Subject.As<SubAgentManager>();
        _ = await manager.SpawnAsync("worker", "a task that does not finish", runInBackground: true);
        await childEntered.Task.WaitAsync(Wait.DefaultTimeout);

        try
        {
            var outcome = loop.Reconfigure(
                NewSpec(new ScriptedProvider(), new FunctionRegistry(), subAgentOptions: subAgentOptions)
            );

            outcome.Should().Be(ReconfigureOutcome.Applied);
            loop.SubAgentManager.Should().BeSameAs(manager, "the manager owns the live children and must survive");

            var child = manager.ListAgents().Should().ContainSingle().Subject;
            child.Status.Should().Be(SubAgentStatus.Running, "a reconfiguration must not end a child's run");
            child
                .FailureCode.Should()
                .BeNull("host_shutdown is disposal's stamp; a reconfiguration never tears the manager down");
        }
        finally
        {
            _ = childRelease.TrySetResult();
        }
    }

    /// <summary>
    /// A template's factory is bound to a provider. After a PROVIDER switch the spec carries the same
    /// templates rebuilt over the new one, and a child spawned from then on must run on it: pairing the
    /// parent's new model id with the old transport is a request to the wrong endpoint.
    /// </summary>
    [Fact]
    public async Task AChildSpawnedAfterAProviderSwitch_RunsOnTheProviderTheSwitchBrought()
    {
        var oldProviderEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newProviderEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        SubAgentOptions OptionsOver(TaskCompletionSource entered) =>
            new()
            {
                MaxConcurrentSubAgents = 2,
                Templates = new Dictionary<string, SubAgentTemplate>
                {
                    ["worker"] = new()
                    {
                        Name = "worker",
                        SystemPrompt = "You are a worker.",
                        AgentFactory = () => new BlockingProvider(entered, release.Task),
                    },
                },
            };

        await using var loop = new MultiTurnAgentLoop(
            new ScriptedProvider(),
            new FunctionRegistry(),
            threadId: "reconfigure-spawn-after-switch",
            defaultOptions: new GenerateReplyOptions { ModelId = OldModel },
            subAgentOptions: OptionsOver(oldProviderEntered)
        );
        var manager = loop.SubAgentManager.Should().NotBeNull().And.Subject.As<SubAgentManager>();

        loop.Reconfigure(
                NewSpec(
                    new ScriptedProvider(),
                    new FunctionRegistry(),
                    subAgentOptions: OptionsOver(newProviderEntered)
                )
            )
            .Should()
            .Be(ReconfigureOutcome.Applied);

        try
        {
            _ = await manager.SpawnAsync("worker", "a task that does not finish", runInBackground: true);

            await newProviderEntered.Task.WaitAsync(Wait.DefaultTimeout);
            oldProviderEntered
                .Task.IsCompleted.Should()
                .BeFalse("the factory bound to the provider the parent left must not serve a spawn after the switch");
        }
        finally
        {
            _ = release.TrySetResult();
        }
    }

    /// <summary>
    /// A child spawned after the switch is built on the manager's child options, and its OWN manager
    /// spawns grandchildren from them: the plain-path characteristics and tier factories in there are
    /// bound to a provider just as a template factory is. They are derived once from the options the
    /// manager was built with, so a switch that refreshed the manager's spawn inputs but not the
    /// derived copy would hand every later child the old transport for its grandchildren, one level
    /// below where the parent-level test can see.
    /// </summary>
    [Fact]
    public async Task AfterAProviderSwitch_TheOptionsAChildIsBuiltOn_ComeFromTheSwitchedToOptions()
    {
        SubAgentOptions OptionsWith(
            Func<SubAgentCharacteristics, SubAgentProviderAgent> plainPath,
            Func<string, IStreamingAgent> tier
        ) =>
            new()
            {
                MaxConcurrentSubAgents = 2,
                Templates = new Dictionary<string, SubAgentTemplate>(),
                PlainPathCharacteristicsAgentFactory = plainPath,
                TierAgentFactory = tier,
                SpawnNameGate = name => name,
            };

        SubAgentProviderAgent OldPlain(SubAgentCharacteristics _) => throw new InvalidOperationException("old");
        SubAgentProviderAgent NewPlain(SubAgentCharacteristics _) => throw new InvalidOperationException("new");
        IStreamingAgent OldTier(string _) => new ScriptedProvider();
        IStreamingAgent NewTier(string _) => new ScriptedProvider();
        Func<SubAgentCharacteristics, SubAgentProviderAgent> newPlain = NewPlain;
        Func<string, IStreamingAgent> newTier = NewTier;

        await using var loop = new MultiTurnAgentLoop(
            new ScriptedProvider(),
            new FunctionRegistry(),
            threadId: "reconfigure-child-options-after-switch",
            defaultOptions: new GenerateReplyOptions { ModelId = OldModel },
            subAgentOptions: OptionsWith(OldPlain, OldTier)
        );
        var manager = loop.SubAgentManager.Should().NotBeNull().And.Subject.As<SubAgentManager>();
        var ordinalsBefore = manager.ChildOptions.OrdinalAllocator;

        loop.Reconfigure(
                NewSpec(new ScriptedProvider(), new FunctionRegistry(), subAgentOptions: OptionsWith(newPlain, newTier))
            )
            .Should()
            .Be(ReconfigureOutcome.Applied);

        var childOptions = manager.ChildOptions;
        childOptions
            .PlainPathCharacteristicsAgentFactory.Should()
            .BeSameAs(newPlain, "a grandchild's provider is resolved through the factory the switch brought");
        childOptions.TierAgentFactory.Should().BeSameAs(newTier);
        childOptions
            .SpawnNameGate.Should()
            .BeNull("the spawn-authority hooks are still this level's, never inherited by a child");
        childOptions
            .OrdinalAllocator.Should()
            .BeSameAs(ordinalsBefore, "the ordinal sequence sizes live state a switch cannot restart");
    }

    /// <summary>
    /// A run cancelled mid-flight never completes, so its id stays on the loop after the loop has
    /// stopped. That stale id is not a run in progress: the pool reads such an entry as idle and lets
    /// the switch through, and a loop that then refused would turn every later switch into a 409 on a
    /// conversation nothing is running.
    /// </summary>
    [Fact]
    public async Task AStaleRunIdLeftByACancelledRun_DoesNotRefuseAReconfigure()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider(entered, release.Task);

        await using var loop = new MultiTurnAgentLoop(
            provider,
            RegistryWith("OldOnly"),
            threadId: "reconfigure-stale-run-id",
            defaultOptions: new GenerateReplyOptions { ModelId = OldModel }
        );

        using var cts = new CancellationTokenSource();
        var running = loop.RunAsync(cts.Token);
        _ = await loop.SendAsync([new TextMessage { Text = "first", Role = Role.User }]);
        await entered.Task.WaitAsync(Wait.DefaultTimeout);

        await cts.CancelAsync();
        try
        {
            await running.WaitAsync(Wait.DefaultTimeout);
        }
        catch (OperationCanceledException)
        {
            // The loop surfaces its own cancellation; that is the state under test, not a failure.
        }

        loop.IsRunning.Should().BeFalse("guard: the loop must have stopped");
        loop.CurrentRunId.Should()
            .NotBeNullOrWhiteSpace("guard: the cancelled run's id is exactly what used to read as busy");

        var replacement = new ScriptedProvider();
        loop.Reconfigure(NewSpec(replacement, RegistryWith("NewOnly"))).Should().Be(ReconfigureOutcome.Applied);
    }

    /// <summary>
    /// The turn after a reconfiguration must be sent with the new model and advertise the new mode's
    /// tools only — the previous mode's tool is gone from the contract list the provider receives.
    /// </summary>
    [Fact]
    public async Task AfterReconfigure_TheNextTurnCarriesTheNewModelAndTheNewToolContracts()
    {
        var provider = new ScriptedProvider();
        await using var loop = new MultiTurnAgentLoop(
            provider,
            RegistryWith("OldOnly"),
            threadId: "reconfigure-tools",
            defaultOptions: new GenerateReplyOptions { ModelId = OldModel }
        );

        using var cts = new CancellationTokenSource();
        var runs = LoopSubscription.SubscribeForRunCompletions(loop, cts.Token, expectedCount: 2);
        _ = loop.RunAsync(cts.Token);

        _ = await loop.SendAsync([new TextMessage { Text = "first", Role = Role.User }]);
        await runs.WaitAsync(0);

        provider.LastOptions!.ModelId.Should().Be(OldModel);
        ToolNames(provider.LastOptions).Should().Contain("OldOnly");

        var replacement = new ScriptedProvider();
        loop.Reconfigure(NewSpec(replacement, RegistryWith("NewOnly"))).Should().Be(ReconfigureOutcome.Applied);

        _ = await loop.SendAsync([new TextMessage { Text = "second", Role = Role.User }]);
        await runs.WaitAsync(1);

        replacement.LastOptions.Should().NotBeNull("the new provider agent serves the next turn");
        replacement.LastOptions!.ModelId.Should().Be(NewModel);
        ToolNames(replacement.LastOptions).Should().Contain("NewOnly").And.NotContain("OldOnly");
        provider.CallCount.Should().Be(1, "the old provider must not be called again");

        await cts.CancelAsync();
    }

    /// <summary>
    /// A switch that arrives mid-stream is refused rather than applied half-way: the run in flight keeps
    /// the configuration it started with, and so does the turn after it.
    /// </summary>
    [Fact]
    public async Task Reconfigure_WhileARunIsInProgress_IsRefusedAndChangesNothing()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider(entered, release.Task);

        await using var loop = new MultiTurnAgentLoop(
            provider,
            RegistryWith("OldOnly"),
            threadId: "reconfigure-busy",
            defaultOptions: new GenerateReplyOptions { ModelId = OldModel }
        );

        using var cts = new CancellationTokenSource();
        var runs = LoopSubscription.SubscribeForRunCompletions(loop, cts.Token, expectedCount: 2);
        _ = loop.RunAsync(cts.Token);

        _ = await loop.SendAsync([new TextMessage { Text = "first", Role = Role.User }]);
        await entered.Task.WaitAsync(Wait.DefaultTimeout);

        var replacement = new ScriptedProvider();
        loop.Reconfigure(NewSpec(replacement, RegistryWith("NewOnly"))).Should().Be(ReconfigureOutcome.RefusedBusy);

        _ = release.TrySetResult();
        await runs.WaitAsync(0);

        _ = await loop.SendAsync([new TextMessage { Text = "second", Role = Role.User }]);
        await runs.WaitAsync(1);

        replacement.CallCount.Should().Be(0, "a refused reconfiguration must never reach the new provider");
        provider.LastOptions!.ModelId.Should().Be(OldModel);
        ToolNames(provider.LastOptions).Should().Contain("OldOnly").And.NotContain("NewOnly");

        await cts.CancelAsync();
    }

    /// <summary>
    /// Build-then-assign: a spec the loop cannot build from leaves every observable part of the loop on
    /// the old configuration, rather than a half-switched loop with the new model and the old tools.
    /// </summary>
    [Fact]
    public async Task AReconfigureThatFailsToBuild_LeavesTheNextTurnOnTheOldModelAndTools()
    {
        var provider = new ScriptedProvider();
        await using var loop = new MultiTurnAgentLoop(
            provider,
            RegistryWith("OldOnly"),
            threadId: "reconfigure-throws",
            defaultOptions: new GenerateReplyOptions { ModelId = OldModel }
        );

        using var cts = new CancellationTokenSource();
        var runs = LoopSubscription.SubscribeForRunCompletions(loop, cts.Token, expectedCount: 1);
        _ = loop.RunAsync(cts.Token);

        var broken = RegistryWith("NewOnly").AddProvider(new ThrowingFunctionProvider());
        var replacement = new ScriptedProvider();

        loop.Invoking(l => l.Reconfigure(NewSpec(replacement, broken)))
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*cannot enumerate*");

        _ = await loop.SendAsync([new TextMessage { Text = "after the failure", Role = Role.User }]);
        await runs.WaitAsync(0);

        replacement.CallCount.Should().Be(0, "a failed reconfiguration must not repoint the loop at the new provider");
        provider.LastOptions!.ModelId.Should().Be(OldModel);
        ToolNames(provider.LastOptions).Should().Contain("OldOnly").And.NotContain("NewOnly");

        await cts.CancelAsync();
    }

    /// <summary>
    /// An accepted-but-unstarted input is not "busy": it survives the reconfiguration and then runs under
    /// the new configuration, which is the behaviour change this feature is for (today the switch throws
    /// such a turn away with the loop).
    /// </summary>
    [Fact]
    public async Task AnInputQueuedBeforeReconfigure_RunsAfterwardsUnderTheNewModel()
    {
        var provider = new ScriptedProvider();
        await using var loop = new MultiTurnAgentLoop(
            provider,
            RegistryWith("OldOnly"),
            threadId: "reconfigure-queued",
            defaultOptions: new GenerateReplyOptions { ModelId = OldModel }
        );

        // Queued before the run loop is started, so it is accepted and waiting rather than in flight.
        _ = await loop.SendAsync([new TextMessage { Text = "queued first", Role = Role.User }]);

        var replacement = new ScriptedProvider();
        loop.Reconfigure(NewSpec(replacement, RegistryWith("NewOnly"))).Should().Be(ReconfigureOutcome.Applied);

        using var cts = new CancellationTokenSource();
        var runs = LoopSubscription.SubscribeForRunCompletions(loop, cts.Token, expectedCount: 1);
        _ = loop.RunAsync(cts.Token);
        await runs.WaitAsync(0);

        provider.CallCount.Should().Be(0, "the queued turn belongs to the new configuration");
        replacement.LastOptions!.ModelId.Should().Be(NewModel);
        ToolNames(replacement.LastOptions).Should().Contain("NewOnly");

        await cts.CancelAsync();
    }

    /// <summary>
    /// The trigger runtime is kept, so a Wait armed before the switch is still armed after it: firing it
    /// resumes the parked run, and the resumed turn goes out under the NEW model.
    /// </summary>
    [Fact]
    public async Task AnArmedWait_SurvivesReconfigure_AndResumesUnderTheNewModel()
    {
        var manual = new ManualSource();
        var triggerOptions = new TriggerOptions
        {
            AdditionalRegistrations =
            [
                new TriggerSourceRegistration
                {
                    Kind = "host_event",
                    Description = "wait for a host-fired event",
                    ArgsSchema = "{}",
                    Capabilities = new TriggerCapabilities(true, false, false),
                    Source = manual,
                },
            ],
        };

        var provider = new ScriptedProvider
        {
            Reply = _ =>
                [
                    new ToolCallMessage
                    {
                        FunctionName = WaitToolProvider.WaitToolName,
                        FunctionArgs = JsonSerializer.Serialize(
                            new
                            {
                                kind = "host_event",
                                args = new { },
                                timeout = "10m",
                            }
                        ),
                        ToolCallId = "tc_host",
                        Role = Role.Assistant,
                    },
                ],
        };

        await using var loop = new MultiTurnAgentLoop(
            provider,
            new FunctionRegistry(),
            threadId: "reconfigure-wait",
            defaultOptions: new GenerateReplyOptions { ModelId = OldModel },
            triggerOptions: triggerOptions
        );

        using var cts = new CancellationTokenSource();
        var runs = LoopSubscription.SubscribeForRunCompletions(loop, cts.Token, expectedCount: 2);
        _ = loop.RunAsync(cts.Token);

        _ = await loop.SendAsync([new TextMessage { Text = "wait for host", Role = Role.User }]);
        await runs.WaitAsync(0);
        (await loop.GetDeferredToolCallsAsync()).Should().ContainSingle(p => p.ToolCallId == "tc_host");

        var replacement = new ScriptedProvider();
        loop.Reconfigure(NewSpec(replacement, new FunctionRegistry())).Should().Be(ReconfigureOutcome.Applied);

        await manual.FireAsync("host-payload");
        await runs.WaitAsync(1);

        replacement
            .LastOptions.Should()
            .NotBeNull("the wait armed before the switch must still be able to resume the parked run");
        replacement.LastOptions!.ModelId.Should().Be(NewModel);

        await cts.CancelAsync();
    }

    /// <summary>
    /// The prompt is re-composed the one way the constructor composes it — host prompt underneath the
    /// compaction note — and the compaction runtime is kept, so <c>RecallConversation</c> is still on the
    /// rebuilt registry rather than silently lost with the old one.
    /// </summary>
    [Fact]
    public async Task Reconfigure_RecomposesTheSystemPrompt_AndKeepsTheCompactionSurface()
    {
        var compaction = new CompactionSetup
        {
            Options = new CompactionOptions { Mode = CompactionMode.Warn },
            Summarizer = new NeverCalledSummarizer(),
        };

        await using var loop = new MultiTurnAgentLoop(
            new ScriptedProvider(),
            new FunctionRegistry(),
            threadId: "reconfigure-compaction",
            includeAskUserQuestionTool: false,
            includeNotifyClientTool: false,
            systemPrompt: "the old mode's prompt",
            defaultOptions: new GenerateReplyOptions { ModelId = OldModel },
            compaction: compaction
        );

        loop.SystemPromptForTests.Should().Contain("the old mode's prompt").And.Contain(CompactionRuntime.SystemNote);

        loop.Reconfigure(NewSpec(new ScriptedProvider(), new FunctionRegistry()))
            .Should()
            .Be(ReconfigureOutcome.Applied);

        loop.SystemPromptForTests.Should()
            .Contain("the new mode's prompt", "the host's prompt for the new mode replaces the old one")
            .And.Contain(CompactionRuntime.SystemNote, "the compaction note is re-applied, not carried over verbatim")
            .And.NotContain("the old mode's prompt");

        loop.RegisteredToolNames.Should()
            .Contain(
                RecallConversationToolProvider.ToolName,
                "the compaction runtime is kept, so its tool goes back onto the rebuilt registry"
            );
    }

    #region Helpers

    private static AgentReconfiguration NewSpec(
        IStreamingAgent provider,
        FunctionRegistry registry,
        SubAgentOptions? subAgentOptions = null
    ) =>
        new(
            provider,
            registry,
            SystemPrompt: "the new mode's prompt",
            DefaultOptions: new GenerateReplyOptions { ModelId = NewModel },
            IncludeAskUserQuestionTool: false,
            IncludeNotifyClientTool: false,
            SubAgentOptions: subAgentOptions
        );

    private static FunctionRegistry RegistryWith(string toolName) =>
        new FunctionRegistry().AddFunction(
            new FunctionContract
            {
                Name = toolName,
                Description = "A tool that exists only in one mode",
                Parameters = [],
            },
            (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("ok"))
        );

    private static IEnumerable<string> ToolNames(GenerateReplyOptions options) =>
        options.Functions?.Select(f => f.Name) ?? [];

    /// <summary>
    /// A provider that records every request and, by default, answers with one assistant text message.
    /// Optionally parks inside the call so a test can hold a run open.
    /// </summary>
    private sealed class ScriptedProvider(TaskCompletionSource? entered = null, Task? release = null) : IStreamingAgent
    {
        private readonly ConcurrentQueue<GenerateReplyOptions> _requests = new();

        /// <summary>What to answer with. Defaults to a single final text message.</summary>
        public Func<IReadOnlyList<IMessage>, IReadOnlyList<IMessage>> Reply { get; init; } =
            _ => [new TextMessage { Text = "done", Role = Role.Assistant }];

        public int CallCount => _requests.Count;

        public GenerateReplyOptions? LastOptions => _requests.LastOrDefault();

        public async Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            _requests.Enqueue(options ?? new GenerateReplyOptions());
            _ = entered?.TrySetResult();
            if (release is not null)
            {
                await release.WaitAsync(cancellationToken);
            }

            return ToAsyncEnumerable(Reply([.. messages]));
        }

        public async Task<IEnumerable<IMessage>> GenerateReplyAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            var stream = await GenerateReplyStreamingAsync(messages, options, cancellationToken);
            List<IMessage> collected = [];
            await foreach (var message in stream.WithCancellation(cancellationToken))
            {
                collected.Add(message);
            }

            return collected;
        }
    }

    /// <summary>A summarizer the test never drives; supplied so the runtime builds none over the provider.</summary>
    private sealed class NeverCalledSummarizer : ICheckpointSummarizer
    {
        public Task<CheckpointSummaryResponse> SummarizeAsync(
            CheckpointSummaryRequest request,
            CancellationToken ct = default
        ) => throw new NotSupportedException("this test never compacts");
    }

    /// <summary>A provider whose enumeration throws, so building the new registry fails.</summary>
    private sealed class ThrowingFunctionProvider : IFunctionProvider
    {
        public string ProviderName => "Throwing";

        public int Priority => 0;

        public IEnumerable<FunctionDescriptor> GetFunctions() =>
            throw new InvalidOperationException("this provider cannot enumerate its functions");
    }

    /// <summary>A trigger source the test fires by hand, so the armed Wait's lifetime is the test's.</summary>
    private sealed class ManualSource : ITriggerSource
    {
        private volatile ITriggerEventSink? _sink;

        public ValueTask<IArmedTrigger> ArmAsync(
            TriggerArmRequest request,
            ITriggerEventSink eventSink,
            CancellationToken cancellationToken
        )
        {
            _sink = eventSink;
            return ValueTask.FromResult<IArmedTrigger>(new Handle(request.WaitId));
        }

        public async Task FireAsync(string payload)
        {
            if (_sink is { } sink)
            {
                await sink.FireAsync(new TriggerFireEvent(payload), CancellationToken.None);
            }
        }

        private sealed class Handle(string waitId) : IArmedTrigger
        {
            public string WaitId { get; } = waitId;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>A sub-agent provider that parks until the test releases it, keeping the child Running.</summary>
    private sealed class BlockingProvider(TaskCompletionSource entered, Task release) : IStreamingAgent
    {
        public async Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            _ = entered.TrySetResult();
            await release.WaitAsync(cancellationToken);
            return ToAsyncEnumerable([new TextMessage { Text = "child done", Role = Role.Assistant }]);
        }

        public async Task<IEnumerable<IMessage>> GenerateReplyAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            var stream = await GenerateReplyStreamingAsync(messages, options, cancellationToken);
            List<IMessage> collected = [];
            await foreach (var message in stream.WithCancellation(cancellationToken))
            {
                collected.Add(message);
            }

            return collected;
        }
    }

    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
        IReadOnlyList<IMessage> messages,
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

    #endregion
}
