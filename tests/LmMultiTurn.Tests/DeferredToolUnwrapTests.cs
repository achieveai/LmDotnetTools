using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Approval;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmLifecycle.Payloads;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmTestUtils;
using FluentAssertions;
using LmMultiTurn.Tests.Lifecycle;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// What a deferred-tool provider does to a tool call in the loop: the call it unwraps is approved, run
/// and reported as its target under the model's own ToolCallId, and a call it refuses or fails on is
/// answered with an error for the wrapper before any approver or handler is reached.
/// </summary>
/// <remarks>
/// The model calls <c>Wrapper({"tool":..., "arguments":...})</c>. <c>Target</c> is registered but not
/// advertised. <c>Wrapper</c>'s own handler fails closed, so a call the loop forgot to unwrap is visible
/// as that handler running.
/// </remarks>
public class DeferredToolUnwrapTests
{
    private const string Wrapper = "Wrapper";
    private const string Target = "Target";
    private const string CallId = "tc_1";
    private const string TargetArgs = """{"path":"a.txt"}""";

    [Fact]
    public async Task AWrappedCallIsApprovedRunAndReportedAsItsTargetUnderTheSameId()
    {
        var h = new Harness();
        var wrapperArgs = $$"""{"tool":"{{Target}}","arguments":{{TargetArgs}}}""";

        var published = await h.RunOneCallAsync(wrapperArgs);

        var approval = h.Gate.Requests.Should().ContainSingle().Subject;
        approval.ToolName.Should().Be(Target, "the approver must decide on the tool that will actually run");
        approval.ToolCallId.Should().Be(CallId);
        approval.Arguments.Json.Should().Be(TargetArgs);

        h.Ran(Target).Should().Be(1);
        h.LastArgs(Target).Should().Be(TargetArgs);
        h.Ran(Wrapper).Should().Be(0);

        var result = published.OfType<ToolCallResultMessage>().Should().ContainSingle().Subject;
        result.ToolName.Should().Be(Target);
        result.ToolCallId.Should().Be(CallId);
        result.IsError.Should().BeFalse();

        var completed = h.ToolCompleted().Should().ContainSingle().Subject;
        completed.ToolName.Should().Be(Target);
        completed.ToolCallId.Should().Be(CallId);

        // The model's own call stays in history as it was sent; only the result names the target.
        var nextRequest = h.Agent.Requests[1];
        ToolCalls(nextRequest).Should().Equal((Wrapper, CallId, wrapperArgs));
        ToolResults(nextRequest).Should().Equal((Target, CallId));
    }

    [Fact]
    public async Task ARefusalAnswersTheWrapperWithoutAskingTheApprover()
    {
        var h = new Harness();

        var published = await h.RunOneCallAsync($$"""{"tool":"{{FakeDeferred.Refused}}"}""");

        h.Gate.WasConsulted.Should().BeFalse("a call that will not run must not cost an approval");
        h.Ran(Wrapper).Should().Be(0);
        h.Ran(Target).Should().Be(0);

        var result = published.OfType<ToolCallResultMessage>().Should().ContainSingle().Subject;
        result.ToolName.Should().Be(Wrapper);
        result.ToolCallId.Should().Be(CallId);
        result.IsError.Should().BeTrue();
        result.ErrorCode.Should().Be(FakeDeferred.RefusalCode);
        result.Result.Should().Be(FakeDeferred.RefusalJson);

        var completed = h.ToolCompleted().Should().ContainSingle().Subject;
        completed.ToolName.Should().Be(Wrapper);
        completed.Outcome.Should().Be(LifecycleToolOutcomes.Failed);
    }

    [Fact]
    public async Task AThrowingProviderAnswersUnwrapFailedAndTheRunGoesOn()
    {
        var h = new Harness();

        var published = await h.RunOneCallAsync($$"""{"tool":"{{FakeDeferred.Throws}}"}""");

        h.Gate.WasConsulted.Should().BeFalse();
        h.Ran(Wrapper).Should().Be(0);
        h.Ran(Target).Should().Be(0);

        var result = published.OfType<ToolCallResultMessage>().Should().ContainSingle().Subject;
        result.ToolName.Should().Be(Wrapper);
        result.ToolCallId.Should().Be(CallId);
        result.IsError.Should().BeTrue();
        result.ErrorCode.Should().Be("unwrap_failed");
        result.Result.Should().Contain(FakeDeferred.ThrowMessage);

        // The run went back to the model with the error and finished normally.
        h.Agent.Requests.Should().HaveCount(2);
        published.OfType<TextMessage>().Should().Contain(t => t.Text == "done");
    }

    [Fact]
    public async Task ACallNoProviderClaimsRunsAsItself()
    {
        // Once a host is tiered every ordinary call passes through the providers, and one none of them
        // claims must run as it would without them. Target is hidden, so this also pins that hiding a
        // contract is not refusing its call.
        var h = new Harness();

        var published = await h.RunOneCallAsync(TargetArgs, functionName: Target);

        h.Gate.ToolNames.Should().Equal([Target]);
        h.Ran(Target).Should().Be(1);
        h.LastArgs(Target).Should().Be(TargetArgs);

        var result = published.OfType<ToolCallResultMessage>().Should().ContainSingle().Subject;
        result.ToolName.Should().Be(Target);
        result.ToolCallId.Should().Be(CallId);
        result.IsError.Should().BeFalse();
    }

    // SC3: the error for a name nothing handles lists only the tools the model is advertised (hidden Target is not),
    // and points to SearchTool only when the model has it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnknownFunctionErrorListsOnlyAdvertisedTools_AndHintsSearchToolOnlyWhenListed(
        bool hasSearchTool
    )
    {
        var counts = new Counts();
        var registry = Harness.Registry(counts);
        _ = registry.AddProvider(new FakeDeferred());
        if (hasSearchTool)
        {
            _ = registry.AddFunction(
                new FunctionContract
                {
                    Name = "SearchTool",
                    Description = "Find a tool.",
                    Parameters = [],
                },
                counts.Handler("SearchTool")
            );
        }

        var agent = new ScriptedAgent([
            [Call("{}") with { FunctionName = "Missing" }],
            [Done()],
        ]);
        await using var loop = new MultiTurnAgentLoop(agent, registry, "deferred-unknown");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = loop.RunAsync(cts.Token);
        var published = await Drain(loop, "go", cts.Token);
        await cts.CancelAsync();

        var result = published.OfType<ToolCallResultMessage>().Should().ContainSingle().Subject;
        result.IsError.Should().BeTrue();
        using var error = JsonDocument.Parse(result.Result);
        error
            .RootElement.GetProperty("available_functions")
            .EnumerateArray()
            .Select(e => e.GetString())
            .Should()
            .Contain(Wrapper)
            .And.NotContain(Target, "a hidden tool is not advertised, so an error must not name it either");
        if (hasSearchTool)
        {
            error.RootElement.GetProperty("hint").GetString().Should().Contain("SearchTool");
        }
        else
        {
            error.RootElement.TryGetProperty("hint", out _).Should().BeFalse("SearchTool is not this model's tool");
        }
    }

    [Fact]
    public async Task ToolSchemaTokensCountOnlyTheAdvertisedContracts()
    {
        var registry = Harness.Registry(new Counts());
        _ = registry.AddProvider(new FakeDeferred());

        await using var loop = new MultiTurnAgentLoop(new ScriptedAgent([]), registry, "deferred-estimate");

        // Read after construction: the loop adds its own client tools to the registry it is given.
        var advertised = CompactionTokenEstimate.EstimateToolSchemas(registry.BuildContracts());
        var registered = CompactionTokenEstimate.EstimateToolSchemas(registry.Build().Item1);
        loop.ToolSchemaTokens.Should().Be(advertised);
        advertised.Should().BeLessThan(registered, "the hidden contract must be the difference");
    }

    [Fact]
    public async Task AReconfigureSwapsTheDeferredProvidersWithTheRegistry()
    {
        var counts = new Counts();
        var wrapperArgs = $$"""{"tool":"{{Target}}","arguments":{{TargetArgs}}}""";
        var agent = new ScriptedAgent([
            [Call(wrapperArgs)],
            [Done()],
            [Call(wrapperArgs) with { ToolCallId = "tc_2" }],
            [Done()],
        ]);
        var deferred = Harness.Registry(counts);
        _ = deferred.AddProvider(new FakeDeferred());

        await using var loop = new MultiTurnAgentLoop(agent, Harness.Registry(counts), "deferred-reconfigure");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = loop.RunAsync(cts.Token);

        loop.Reconfigure(Spec(agent, deferred)).Should().Be(ReconfigureOutcome.Applied);
        _ = await Drain(loop, "first", cts.Token);
        (counts[Wrapper], counts[Target]).Should().Be((0, 1), "the new registry's provider unwraps");

        loop.Reconfigure(Spec(agent, Harness.Registry(counts))).Should().Be(ReconfigureOutcome.Applied);
        _ = await Drain(loop, "second", cts.Token);
        (counts[Wrapper], counts[Target]).Should().Be((1, 1), "no provider survives the registry it came with");

        await cts.CancelAsync();
    }

    private static AgentReconfiguration Spec(IStreamingAgent agent, FunctionRegistry registry) =>
        new(agent, registry, null, new GenerateReplyOptions(), true, true, null);

    private static ToolCallMessage Call(string args) =>
        new()
        {
            FunctionName = Wrapper,
            FunctionArgs = args,
            ToolCallId = CallId,
            Role = Role.Assistant,
        };

    private static TextMessage Done() => new() { Text = "done", Role = Role.Assistant };

    // The loop sends history as plural ToolsCall/ToolsCallResult messages; read either shape.
    private static List<(string?, string?, string?)> ToolCalls(IReadOnlyList<IMessage> request) =>
        [
            .. request.OfType<ToolCallMessage>().Select(c => (c.FunctionName, c.ToolCallId, c.FunctionArgs)),
            .. request
                .OfType<ToolsCallMessage>()
                .SelectMany(m => m.ToolCalls)
                .Select(c => (c.FunctionName, c.ToolCallId, c.FunctionArgs)),
        ];

    private static List<(string?, string?)> ToolResults(IReadOnlyList<IMessage> request) =>
        [
            .. request.OfType<ToolCallResultMessage>().Select(r => (r.ToolName, r.ToolCallId)),
            .. request
                .OfType<ToolsCallResultMessage>()
                .SelectMany(m => m.ToolCallResults)
                .Select(r => (r.ToolName, r.ToolCallId)),
        ];

    private static async Task<List<IMessage>> Drain(MultiTurnAgentLoop loop, string text, CancellationToken ct)
    {
        var published = new List<IMessage>();
        var input = new UserInput([new TextMessage { Text = text, Role = Role.User }]);
        await foreach (var message in loop.ExecuteRunAsync(input, ct))
        {
            published.Add(message);
        }

        return published;
    }

    private sealed class Harness
    {
        private readonly Counts _counts = new();

        public RecordingToolApprovalGate Gate { get; } = RecordingToolApprovalGate.Allowing();

        public RecordingLifecyclePublisher Publisher { get; } = new();

        public ScriptedAgent Agent { get; private set; } = new([]);

        public int Ran(string name) => _counts[name];

        public string? LastArgs(string name) => _counts.LastArgs(name);

        public IReadOnlyList<ToolCompletedPayload> ToolCompleted() =>
            Publisher.Payloads<ToolCompletedPayload>(LifecycleEventTypes.ToolCompleted);

        /// <summary>One turn calling <paramref name="functionName"/> with <paramref name="args"/>, then one turn of text.</summary>
        public async Task<List<IMessage>> RunOneCallAsync(string args, string functionName = Wrapper)
        {
            Agent = new ScriptedAgent([
                [Call(args) with { FunctionName = functionName }],
                [Done()],
            ]);
            var registry = Registry(_counts);
            _ = registry.AddProvider(new FakeDeferred());

            await using var loop = new MultiTurnAgentLoop(
                Agent,
                registry,
                "deferred-unwrap",
                lifecycleServices: new MultiTurnLifecycleServices
                {
                    Publisher = Publisher,
                    Approval = new ToolInvocationPreparer(new ToolApprovalOptions { Gates = [Gate] }),
                }
            );

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            _ = loop.RunAsync(cts.Token);
            var published = await Drain(loop, "go", cts.Token);
            await cts.CancelAsync();
            return published;
        }

        public static FunctionRegistry Registry(Counts counts)
        {
            var registry = new FunctionRegistry();
            _ = registry.AddFunction(
                new FunctionContract
                {
                    Name = Target,
                    Description = "A tool only reachable through the wrapper.",
                    Parameters =
                    [
                        new FunctionParameterContract
                        {
                            Name = "path",
                            Description = "The path.",
                            ParameterType = new JsonSchemaObject { Type = JsonSchemaTypeHelper.ToType("string") },
                            IsRequired = true,
                        },
                    ],
                },
                counts.Handler(Target)
            );
            _ = registry.AddFunction(
                new FunctionContract
                {
                    Name = Wrapper,
                    Description = "Run an available tool that is not in your tool list.",
                    Parameters = [],
                },
                counts.Handler(Wrapper, failClosed: true)
            );
            return registry;
        }
    }

    /// <summary>Counts handler runs by tool name; a fail-closed handler returns an error as the real wrapper's does.</summary>
    private sealed class Counts
    {
        private readonly Dictionary<string, int> _runs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _args = new(StringComparer.Ordinal);

        public int this[string name]
        {
            get
            {
                lock (_runs)
                {
                    return _runs.GetValueOrDefault(name);
                }
            }
        }

        public string? LastArgs(string name)
        {
            lock (_runs)
            {
                return _args.GetValueOrDefault(name);
            }
        }

        public ToolHandler Handler(string name, bool failClosed = false) =>
            (args, _, _) =>
            {
                lock (_runs)
                {
                    _runs[name] = _runs.GetValueOrDefault(name) + 1;
                    _args[name] = args;
                }

                return Task.FromResult<ToolHandlerResult>(
                    failClosed
                        ? ToolHandlerResult.FromError($"{name} must be unwrapped by the agent loop", "not_supported")
                        : ToolHandlerResult.FromText($"{name} ran")
                );
            };
    }

    /// <summary>Hides Target; unwraps Wrapper({"tool","arguments"}); refuses one tool name and throws on another.</summary>
    private sealed class FakeDeferred : IDeferredToolProvider
    {
        public const string Refused = "Nope";
        public const string RefusalCode = "tool_not_available";
        public const string RefusalJson = """{"error":"Nope cannot be run through Wrapper."}""";
        public const string Throws = "Boom";
        public const string ThrowMessage = "view disposed mid-swap";

        public string ProviderName => nameof(FakeDeferred);

        public int Priority => 1000;

        public IEnumerable<FunctionDescriptor> GetFunctions() => [];

        public bool IsAdvertised(string functionName) => functionName != Target;

        public ToolCallUnwrap? TryUnwrap(string functionName, string argumentsJson)
        {
            if (functionName != Wrapper)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(argumentsJson);
            var tool = doc.RootElement.GetProperty("tool").GetString()!;
            return tool switch
            {
                Refused => ToolCallUnwrap.Refuse(RefusalJson, RefusalCode),
                Throws => throw new InvalidOperationException(ThrowMessage),
                _ => ToolCallUnwrap.Run(
                    tool,
                    doc.RootElement.TryGetProperty("arguments", out var a) ? a.GetRawText() : "{}"
                ),
            };
        }
    }

    /// <summary>Turn i streams script[i] and records every request the loop sends.</summary>
    private sealed class ScriptedAgent(IReadOnlyList<IMessage[]> script) : IStreamingAgent
    {
        public List<IReadOnlyList<IMessage>> Requests { get; } = [];

        public Task<IEnumerable<IMessage>> GenerateReplyAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException("The loop streams.");

        public Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            IMessage[] turn;
            lock (Requests)
            {
                Requests.Add([.. messages]);
                turn = Requests.Count <= script.Count ? script[Requests.Count - 1] : [Done()];
            }

            return Task.FromResult(Stream(turn));
        }

        private static async IAsyncEnumerable<IMessage> Stream(
            IMessage[] turn,
            [EnumeratorCancellation] CancellationToken ct = default
        )
        {
            foreach (var message in turn)
            {
                ct.ThrowIfCancellationRequested();
                yield return message;
                await Task.Yield();
            }
        }
    }
}
