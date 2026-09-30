using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.DualLayer;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.DualLayer;

/// <summary>
/// The planner's side of the pair: every executor tool is mirrored with a required rationale, and
/// no call reaches the executor without one.
/// </summary>
public class DelegatingToolProviderTests
{
    private const string GoodRationale = "Find the retry limit; return only that value verbatim.";

    private sealed class RecordingExecutor : IDelegatedToolExecutor
    {
        public List<DelegatedToolCall> Calls { get; } = [];

        public Func<DelegatedToolCall, string> Answer { get; set; } = c => $"report for {c.ToolName}";

        public Task<string> ExecuteAsync(DelegatedToolCall call, CancellationToken cancellationToken)
        {
            Calls.Add(call);
            return Task.FromResult(Answer(call));
        }
    }

    private static FunctionContract ReadFile() =>
        new()
        {
            Name = "read_file",
            Description = "Reads a file.",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "path",
                    Description = "File path.",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = true,
                },
            ],
        };

    private static (DelegatingToolProvider Provider, RecordingExecutor Executor) Create(
        params FunctionContract[] contracts
    )
    {
        var executor = new RecordingExecutor();
        return (new DelegatingToolProvider(() => contracts, executor), executor);
    }

    private static Task<ToolHandlerResult> Invoke(
        DelegatingToolProvider provider,
        string argsJson,
        string? callId = "c1"
    ) => provider.GetFunctions().Single().Handler(argsJson, new ToolCallContext { ToolCallId = callId }, default);

    private static ToolHandlerResultPayload Payload(ToolHandlerResult result) =>
        result.Should().BeOfType<ToolHandlerResult.Resolved>().Subject.Payload;

    [Fact]
    public void Mirror_keeps_the_executor_tool_and_adds_a_required_rationale()
    {
        var (provider, _) = Create(ReadFile());

        var contract = provider.GetFunctions().Single().Contract;

        contract.Name.Should().Be("read_file");
        contract.Description.Should().Be("Reads a file.");
        contract.Parameters!.Select(p => (p.Name, p.IsRequired)).Should().Equal(("path", true), ("rationale", true));
    }

    [Fact]
    public void Mirror_of_a_parameterless_tool_still_demands_a_rationale()
    {
        var (provider, _) = Create(new FunctionContract { Name = "list_processes" });

        provider.GetFunctions().Single().Contract.Parameters!.Single().Name.Should().Be("rationale");
    }

    [Fact]
    public void A_tool_that_already_has_a_rationale_parameter_fails_loudly()
    {
        var clash = ReadFile();
        clash.Parameters =
        [
            .. clash.Parameters!,
            new FunctionParameterContract
            {
                Name = "rationale",
                ParameterType = new JsonSchemaObject { Type = new("string") },
            },
        ];
        var (provider, _) = Create(clash);

        var act = () => provider.GetFunctions().ToList();

        act.Should().Throw<InvalidOperationException>().WithMessage("*read_file*rationale*");
    }

    [Theory]
    [InlineData("RecallConversation")]
    [InlineData("AskUserQuestion")]
    [InlineData("NotifyClient")]
    public void Executor_private_tools_are_not_mirrored(string privateTool)
    {
        var (provider, _) = Create(ReadFile(), new FunctionContract { Name = privateTool });

        provider.GetFunctions().Select(f => f.Contract.Name).Should().Equal("read_file");
    }

    [Fact]
    public void Planner_registry_builds_beside_the_planners_own_recall_tool()
    {
        var executorRegistry = new FunctionRegistry()
            .AddFunction(ReadFile(), (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("")))
            .AddFunction(
                new FunctionContract { Name = "RecallConversation" },
                (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("executor history"))
            );
        var planner = DelegatingToolProvider
            .CreatePlannerRegistry(executorRegistry, new RecordingExecutor())
            .AddFunction(
                new FunctionContract { Name = "RecallConversation" },
                (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("planner history"))
            );

        var (contracts, _) = planner.Build();

        contracts.Select(c => c.Name).Should().BeEquivalentTo("read_file", "RecallConversation");
    }

    [Fact]
    public void Catalog_changes_reach_the_planner_on_the_next_read()
    {
        var catalog = new List<FunctionContract> { ReadFile() };
        var provider = new DelegatingToolProvider(() => catalog, new RecordingExecutor());

        provider.GetFunctions().Should().ContainSingle();
        catalog.Add(new FunctionContract { Name = "run_bash" });

        provider.GetFunctions().Select(f => f.Contract.Name).Should().Equal("read_file", "run_bash");
    }

    [Fact]
    public async Task Call_is_forwarded_with_the_rationale_split_from_the_tool_arguments()
    {
        var (provider, executor) = Create(ReadFile());

        var result = await Invoke(provider, $$"""{"path":"src/a.cs","rationale":"  {{GoodRationale}}  "}""");

        Payload(result).Should().Be(new ToolHandlerResultPayload("report for read_file"));
        var call = executor.Calls.Single();
        call.ToolName.Should().Be("read_file");
        call.Rationale.Should().Be(GoodRationale);
        call.ToolCallId.Should().Be("c1");
        JsonNode
            .DeepEquals(JsonNode.Parse(call.ArgumentsJson), JsonNode.Parse("""{"path":"src/a.cs"}"""))
            .Should()
            .BeTrue("the executor's tool never takes a rationale, so the call must match its own contract");
    }

    [Theory]
    [InlineData("""{"path":"a"}""", "is required")]
    [InlineData("""{"path":"a","rationale":"   "}""", "is required")]
    [InlineData("""{"path":"a","rationale":42}""", "is required")]
    [InlineData("""{"path":"a","rationale":"read it"}""", "at least 4 words")]
    [InlineData("""not json""", "not valid JSON")]
    [InlineData("""["a"]""", "must be a JSON object")]
    public async Task Call_without_a_clear_rationale_is_rejected_and_never_reaches_the_executor(
        string argsJson,
        string reason
    )
    {
        var (provider, executor) = Create(ReadFile());

        var payload = Payload(await Invoke(provider, argsJson));

        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be(DelegatingToolProvider.RationaleRejectedErrorCode);
        payload.Text.Should().Contain(reason).And.Contain("did not run");
        executor.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Minimum_rationale_length_is_configurable()
    {
        var executor = new RecordingExecutor();
        var provider = new DelegatingToolProvider(() => [ReadFile()], executor, minRationaleWords: 1);

        Payload(await Invoke(provider, """{"path":"a","rationale":"why"}""")).IsError.Should().BeFalse();
        executor.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task Executor_failure_becomes_this_calls_error_instead_of_failing_the_planner()
    {
        var (provider, executor) = Create(ReadFile());
        executor.Answer = _ => throw new InvalidOperationException("provider returned 529");

        var payload = Payload(await Invoke(provider, $$"""{"path":"a","rationale":"{{GoodRationale}}"}"""));

        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be(DelegatingToolProvider.ExecutorFailedErrorCode);
        payload.Text.Should().Contain("read_file").And.Contain("provider returned 529");
    }

    [Fact]
    public async Task Planner_cancellation_propagates_instead_of_becoming_a_tool_error()
    {
        var (provider, executor) = Create(ReadFile());
        using var cts = new CancellationTokenSource();
        executor.Answer = _ =>
        {
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
            return "unreachable";
        };

        var act = () =>
            provider
                .GetFunctions()
                .Single()
                .Handler($$"""{"path":"a","rationale":"{{GoodRationale}}"}""", new ToolCallContext(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Planner_registry_holds_only_mirrors_of_the_executor_tools()
    {
        var realToolRan = false;
        var executorRegistry = new FunctionRegistry().AddFunction(
            ReadFile(),
            (_, _, _) =>
            {
                realToolRan = true;
                return Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("raw"));
            }
        );

        var planner = DelegatingToolProvider.CreatePlannerRegistry(executorRegistry, new RecordingExecutor());
        var (contracts, handlers) = planner.Build();

        contracts.Single().Parameters!.Should().Contain(p => p.Name == "rationale");
        handlers.Should().ContainSingle();
        _ = handlers.Single().Value("""{"path":"a"}""", new ToolCallContext(), default);
        realToolRan.Should().BeFalse("the planner's handler is the proxy, never the executor's real tool");
    }

    [Fact]
    public void Delegation_prompt_carries_tool_arguments_and_rationale_inside_the_tag()
    {
        var text = DualLayerPrompts.FormatDelegation(
            new DelegatedToolCall("read_file", """{"path":"a"}""", GoodRationale, "c1")
        );

        text.Should()
            .Be(
                "<planner-tool-call>\nTool: read_file\nArguments: {\"path\":\"a\"}\n"
                    + $"Rationale: {GoodRationale}\n</planner-tool-call>"
            );
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void System_prompts_stand_alone_when_the_host_has_none(string? hostPrompt)
    {
        DualLayerPrompts
            .ComposePlannerSystemPrompt(hostPrompt)
            .Should()
            .Be(DualLayerPrompts.PairInstructions.TrimEnd() + "\n\n" + DualLayerPrompts.PlannerInstructions);
        DualLayerPrompts
            .ComposeExecutorSystemPrompt(hostPrompt)
            .Should()
            .Be(DualLayerPrompts.PairInstructions.TrimEnd() + "\n\n" + DualLayerPrompts.ExecutorInstructions);
    }

    [Fact]
    public void System_prompts_keep_the_host_prompt_and_add_the_layer_role()
    {
        DualLayerPrompts
            .ComposePlannerSystemPrompt("Host rules.")
            .Should()
            .Be(
                "Host rules.\n\n"
                    + DualLayerPrompts.PairInstructions.TrimEnd()
                    + "\n\n"
                    + DualLayerPrompts.PlannerInstructions
            );
        // Both layers get the same explanation of the pair; the executor gets the host's text last.
        DualLayerPrompts
            .ComposeExecutorSystemPrompt("Host rules.")
            .Should()
            .Be(
                DualLayerPrompts.PairInstructions.TrimEnd()
                    + "\n\n"
                    + DualLayerPrompts.ExecutorInstructions.TrimEnd()
                    + "\n\nHost rules."
            );
    }
}
