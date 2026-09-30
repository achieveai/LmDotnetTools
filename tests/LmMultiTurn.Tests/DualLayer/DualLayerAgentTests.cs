using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.DualLayer;
using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.DualLayer;

/// <summary>
/// Both layers as real <see cref="MultiTurnAgentLoop"/>s with scripted models. The planner owns only
/// mirrors of the executor's tools. The executor runs the real tool and answers the rationale. The
/// planner reads the executor's answer, never the raw tool output.
/// </summary>
public class DualLayerAgentTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>A provider whose next reply is computed from the request it receives.</summary>
    internal sealed class ScriptedAgent(Func<IReadOnlyList<IMessage>, IReadOnlyList<IMessage>> script) : IStreamingAgent
    {
        public ConcurrentQueue<IReadOnlyList<IMessage>> Requests { get; } = new();

        public ConcurrentQueue<IReadOnlyList<FunctionContract>> OfferedTools { get; } = new();

        /// <summary>What this model would answer, so another script can extend it.</summary>
        public IReadOnlyList<IMessage> Reply(IReadOnlyList<IMessage> request) => script(request);

        public Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            var request = messages.ToList();
            Requests.Enqueue(request);
            OfferedTools.Enqueue([.. options?.Functions ?? []]);
            return Task.FromResult(Stream([.. script(request).Select(m => m.WithIds(options))]));
        }

        public Task<IEnumerable<IMessage>> GenerateReplyAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        private static async IAsyncEnumerable<IMessage> Stream(
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
    }

    // A reply with both text and a tool call is stored as one CompositeMessage.
    private static IEnumerable<ToolCallResult> ToolResults(IEnumerable<IMessage> request) =>
        request
            .SelectMany(m => m is CompositeMessage composite ? composite.Messages : [m])
            .OfType<ToolsCallAggregateMessage>()
            .SelectMany(a => a.ToolsCallResult.ToolCallResults);

    private static string? LastUserText(IReadOnlyList<IMessage> request) =>
        request.LastOrDefault() is TextMessage { Role: Role.User } text ? text.Text : null;

    private static ToolCallMessage Call(string id, string name, string argsJson) =>
        new()
        {
            ToolCallId = id,
            FunctionName = name,
            FunctionArgs = argsJson,
            Role = Role.Assistant,
        };

    private static TextMessage Say(string text) => new() { Text = text, Role = Role.Assistant };

    /// <summary>
    /// A cheap executor that does what the ideal one does: runs the named tool with the given
    /// arguments, then reports back the one line the rationale asks for, never the whole output.
    /// </summary>
    private static ScriptedAgent FaithfulExecutor() =>
        new(request =>
        {
            if (LastUserText(request) is { } delegation && delegation.Contains("<planner-tool-call>"))
            {
                var lines = delegation.Split('\n');
                var tool = lines.Single(l => l.StartsWith("Tool: ", StringComparison.Ordinal))["Tool: ".Length..];
                var args = lines.Single(l => l.StartsWith("Arguments: ", StringComparison.Ordinal))[
                    "Arguments: ".Length..
                ];
                return [Call($"ex-{request.Count}", tool, args)];
            }

            var raw = ToolResults(request).Last().Result;
            return [Say("report: " + raw.Split('\n')[0])];
        });

    /// <summary>
    /// The executor's real tools: a read that returns a two-line file (so a report can be shown to
    /// hold less than the raw output) and records the arguments it was called with.
    /// </summary>
    private sealed class RealTools
    {
        public ConcurrentQueue<string> ReadArgs { get; } = new();

        public FunctionRegistry Registry { get; }

        public RealTools()
        {
            Registry = new FunctionRegistry().AddFunction(
                new FunctionContract
                {
                    Name = "read_file",
                    Description = "Reads a file.",
                    Parameters =
                    [
                        new FunctionParameterContract
                        {
                            Name = "path",
                            ParameterType = new JsonSchemaObject { Type = new("string") },
                            IsRequired = true,
                        },
                    ],
                },
                (args, _, _) =>
                {
                    ReadArgs.Enqueue(args);
                    var path = JsonNode.Parse(args)!["path"]!.GetValue<string>();
                    return Task.FromResult<ToolHandlerResult>(
                        ToolHandlerResult.FromText($"contents of {path}\nSECRET-BULK-THE-PLANNER-NEVER-NEEDS")
                    );
                }
            );
        }
    }

    private sealed class Pair : IAsyncDisposable
    {
        public required MultiTurnAgentLoop Planner { get; init; }
        public required ScriptedAgent PlannerModel { get; init; }
        public required MultiTurnAgentLoop ExecutorLoop { get; init; }
        public required ScriptedAgent ExecutorModel { get; init; }
        public required AgentDelegatedToolExecutor Executor { get; init; }
        public required RealTools Tools { get; init; }
        public required CancellationTokenSource Cts { get; init; }

        public Task<AgentTextResult> AskAsync(string text) => AgentTextCollector.CollectAsync(Planner, text, Cts.Token);

        public async ValueTask DisposeAsync()
        {
            await Planner.DisposeAsync();
            await Executor.DisposeAsync();
            Cts.Dispose();
        }
    }

    /// <summary>Wires the pair the way a host must: the planner's registry is built from the executor's.</summary>
    private static Pair Build(
        ScriptedAgent plannerModel,
        ScriptedAgent? executorModel = null,
        bool shareReferenceContext = true
    )
    {
        executorModel ??= FaithfulExecutor();
        var tools = new RealTools();
        var executorUsage = new DeferredUsageSink();
        var executorLoop = new MultiTurnAgentLoop(
            executorModel,
            tools.Registry,
            "thread-1::executor",
            includeAskUserQuestionTool: false,
            includeNotifyClientTool: false,
            systemPrompt: DualLayerPrompts.ComposeExecutorSystemPrompt(null),
            externalUsageSink: executorUsage
        );
        var executor = new AgentDelegatedToolExecutor(executorLoop);
        var planner = new MultiTurnAgentLoop(
            plannerModel,
            DelegatingToolProvider.CreatePlannerRegistry(tools.Registry, executor),
            "thread-1",
            includeAskUserQuestionTool: false,
            includeNotifyClientTool: false,
            systemPrompt: DualLayerPrompts.ComposePlannerSystemPrompt("Be helpful.")
        );
        executorUsage.Target = planner.UsageSink;
        DualLayerInputRouting.Link(planner, executorLoop, executor, shareReferenceContext: shareReferenceContext);
        var cts = new CancellationTokenSource(Timeout);
        _ = planner.RunAsync(cts.Token);
        return new Pair
        {
            Planner = planner,
            PlannerModel = plannerModel,
            ExecutorLoop = executorLoop,
            ExecutorModel = executorModel,
            Executor = executor,
            Tools = tools,
            Cts = cts,
        };
    }

    private const string Rationale = "Find the first line of the file; return only that line verbatim.";

    [Fact]
    public async Task Planner_read_is_carried_out_by_the_executor_and_the_planner_sees_only_its_report()
    {
        var plannerModel = new ScriptedAgent(request =>
            ToolResults(request).Any()
                ? [Say("answer: " + ToolResults(request).Single().Result)]
                : [Call("p1", "read_file", $$"""{"path":"notes.md","rationale":"{{Rationale}}"}""")]
        );
        await using var pair = Build(plannerModel);

        var answer = await pair.AskAsync("What does notes.md start with?");

        answer.Text.Should().Be("answer: report: contents of notes.md");

        // The real tool ran once, with the planner's arguments and without the rationale.
        JsonNode
            .DeepEquals(JsonNode.Parse(pair.Tools.ReadArgs.Single()), JsonNode.Parse("""{"path":"notes.md"}"""))
            .Should()
            .BeTrue();

        // The executor was told what the planner wanted and why.
        var delegation = LastUserText(pair.ExecutorModel.Requests.First());
        delegation.Should().Contain("Tool: read_file").And.Contain("Rationale: " + Rationale);

        // The planner offered its model the mirrored tool, and only that tool.
        pair.PlannerModel.OfferedTools.First().Select(f => f.Name).Should().Equal("read_file");
        pair.PlannerModel.OfferedTools.First()
            .Single()
            .Parameters!.Should()
            .Contain(p => p.Name == "rationale" && p.IsRequired);

        // The planner's context holds the executor's report, not the raw output.
        pair.PlannerModel.Requests.SelectMany(ToolResults)
            .Should()
            .OnlyContain(r => !r.Result.Contains("SECRET-BULK"));
    }

    [Fact]
    public async Task Planner_call_without_a_rationale_is_rejected_and_the_executor_is_never_woken()
    {
        var plannerModel = new ScriptedAgent(request =>
            ToolResults(request).Any()
                ? [Say("saw: " + ToolResults(request).Single().Result)]
                : [Call("p1", "read_file", """{"path":"notes.md"}""")]
        );
        await using var pair = Build(plannerModel);

        var answer = await pair.AskAsync("Read notes.md");

        answer.Text.Should().Contain("'rationale' is required").And.Contain("did not run");
        pair.ExecutorModel.Requests.Should().BeEmpty();
        pair.Tools.ReadArgs.Should().BeEmpty();
    }

    [Fact]
    public async Task Executor_keeps_one_conversation_across_the_planners_calls()
    {
        var plannerModel = new ScriptedAgent(request =>
            ToolResults(request).Count() switch
            {
                0 => [Call("p1", "read_file", $$"""{"path":"a.md","rationale":"{{Rationale}}"}""")],
                1 => [Call("p2", "read_file", $$"""{"path":"b.md","rationale":"{{Rationale}}"}""")],
                _ => [Say("done")],
            }
        );
        await using var pair = Build(plannerModel);

        (await pair.AskAsync("Read a.md then b.md")).Text.Should().Be("done");

        // The second delegation arrives on top of the first one's whole exchange: the executor
        // remembers what it already read, which is what lets it answer later calls in context.
        // First request of the run that carried the b.md call (the first run's reference block also names b.md).
        var secondDelegation = pair.ExecutorModel.Requests.First(r =>
            r[^1] is TextMessage { Role: Role.User } t
            && t.Text.StartsWith("<planner-tool-call>", StringComparison.Ordinal)
            && t.Text.Contains("b.md", StringComparison.Ordinal)
        );
        secondDelegation.OfType<TextMessage>().Should().Contain(m => m.Role == Role.User && m.Text.Contains("a.md"));
        ToolResults(secondDelegation).Should().Contain(r => r.Result.Contains("contents of a.md"));
    }

    [Fact]
    public async Task Parallel_planner_calls_each_get_their_own_executor_report()
    {
        var plannerModel = new ScriptedAgent(request =>
            ToolResults(request).Any()
                ? [Say(string.Join(" | ", ToolResults(request).OrderBy(r => r.ToolCallId).Select(r => r.Result)))]
                :
                [
                    Call("p1", "read_file", $$"""{"path":"a.md","rationale":"{{Rationale}}"}"""),
                    Call("p2", "read_file", $$"""{"path":"b.md","rationale":"{{Rationale}}"}"""),
                ]
        );
        await using var pair = Build(plannerModel);

        var answer = await pair.AskAsync("Read both files at once");

        answer.Text.Should().Be("report: contents of a.md | report: contents of b.md");
        pair.Tools.ReadArgs.Should().HaveCount(2);
    }

    [Fact]
    public async Task Executor_that_reports_nothing_reaches_the_planner_as_a_failure_not_an_empty_result()
    {
        var plannerModel = new ScriptedAgent(request =>
            ToolResults(request).Any()
                ? [Say("saw: " + ToolResults(request).Single().Result)]
                : [Call("p1", "read_file", $$"""{"path":"notes.md","rationale":"{{Rationale}}"}""")]
        );
        var silentExecutor = new ScriptedAgent(_ => [Say("   ")]);
        await using var pair = Build(plannerModel, silentExecutor);

        var answer = await pair.AskAsync("Read notes.md");

        answer.Text.Should().Contain("could not carry out read_file").And.Contain("without reporting anything");
    }

    [Fact]
    public async Task Executor_usage_is_counted_in_the_planners_ledger()
    {
        const int ExecutorPromptTokens = 7777;
        var plannerModel = new ScriptedAgent(request =>
            ToolResults(request).Any()
                ? [Say("done")]
                : [Call("p1", "read_file", $$"""{"path":"notes.md","rationale":"{{Rationale}}"}""")]
        );
        var faithful = FaithfulExecutor();
        var billedExecutor = new ScriptedAgent(request =>
            [
                .. faithful.Reply(request),
                new UsageMessage
                {
                    Usage = new Usage
                    {
                        PromptTokens = ExecutorPromptTokens,
                        CompletionTokens = 1,
                        TotalTokens = ExecutorPromptTokens + 1,
                    },
                },
            ]
        );
        await using var pair = Build(plannerModel, billedExecutor);

        (await pair.AskAsync("Read notes.md")).Text.Should().Be("done");

        // One conversation, one bill: the cheap layer's spend is not invisible to the host.
        var ledger = pair.Planner.UsageSink.Should().BeOfType<UsageLedger>().Subject;
        ledger.SnapshotRecords().Should().Contain(r => r.InputTokens == ExecutorPromptTokens);
    }

    [Fact]
    public async Task Executor_that_runs_an_unrequested_next_step_is_exposed_to_the_planner()
    {
        // Found by hand with a real cheap executor: asked for one step of a sequence, it also ran the
        // next step and reported on that one. The planner must not take that report at face value.
        var plannerModel = new ScriptedAgent(request =>
            ToolResults(request).Any()
                ? [Say(ToolResults(request).Single().Result)]
                : [Call("p1", "read_file", $$"""{"path":"a.md","rationale":"{{Rationale}}"}""")]
        );
        var eagerExecutor = new ScriptedAgent(request =>
        {
            var delegationAt = request.ToList().FindLastIndex(m => m is TextMessage { Role: Role.User });
            var resultsSinceDelegation = ToolResults(request.Skip(delegationAt)).ToList();
            return resultsSinceDelegation.Count switch
            {
                0 => [Call("ex-1", "read_file", """{"path":"a.md"}""")],
                1 => [Say("report: contents of a.md"), Call("ex-2", "read_file", """{"path":"b.md"}""")],
                _ => [Say("report: " + resultsSinceDelegation[^1].Result.Split('\n')[0])],
            };
        });
        await using var pair = Build(plannerModel, eagerExecutor);

        var seen = (await pair.AskAsync("Read a.md")).Text;

        pair.Tools.ReadArgs.Should().HaveCount(2, "the executor really did run the extra step");
        seen.Should()
            .StartWith("report: contents of b.md", "the executor's prose answers the wrong call")
            .And.Contain("""1. read_file {"path":"a.md"}: succeeded (your call)""")
            .And.Contain("""2. read_file {"path":"b.md"}: succeeded (extra: not the call you made)""");
    }

    private static ScriptedAgent PlannerReadingTwoFiles() =>
        new(request =>
            ToolResults(request).Count() switch
            {
                0 => [Call("p1", "read_file", $$"""{"path":"a.md","rationale":"{{Rationale}}"}""")],
                1 => [Call("p2", "read_file", $$"""{"path":"b.md","rationale":"{{Rationale}}"}""")],
                _ => [Say("done")],
            }
        );

    [Fact]
    public async Task What_the_user_told_the_planner_is_shown_to_the_executor_once_as_reference()
    {
        await using var pair = Build(PlannerReadingTwoFiles());

        _ = await pair.AskAsync("Compare a.md with b.md");

        // Two executor runs, both delegations: the mirrored user message caused no run of its own.
        var delegations = Delegations(pair.ExecutorModel);
        delegations.Should().HaveCount(2);
        var first = delegations[0];
        first
            .Should()
            .StartWith("<conversation-context>")
            .And.Contain("[user] Compare a.md with b.md")
            .And.Contain("For reference only");
        first
            .IndexOf("<planner-tool-call>", StringComparison.Ordinal)
            .Should()
            .BeGreaterThan(first.IndexOf("</conversation-context>", StringComparison.Ordinal));
        // Shown once: the second delegation carries nothing new.
        delegations[1].Should().StartWith("<planner-tool-call>");
    }

    [Fact]
    public async Task With_the_reference_context_off_the_executor_sees_only_the_planners_calls()
    {
        await using var pair = Build(PlannerReadingTwoFiles(), shareReferenceContext: false);

        _ = await pair.AskAsync("Compare a.md with b.md");

        Delegations(pair.ExecutorModel)
            .Should()
            .HaveCount(2)
            .And.AllSatisfy(d => d.Should().StartWith("<planner-tool-call>").And.NotContain("Compare a.md"));
    }

    /// <summary>
    /// The turn each executor run started from. A run makes several model requests (call, then
    /// report); only its first ends on the delegated user text.
    /// </summary>
    private static List<string> Delegations(ScriptedAgent executorModel) =>
        [
            .. executorModel
                .Requests.Select(r => r[^1] is TextMessage { Role: Role.User } t ? t.Text : null)
                .OfType<string>(),
        ];

    [Fact]
    public async Task A_notice_raised_inside_the_executor_goes_to_the_planner_and_comes_back_as_reference()
    {
        // The planner acts on a notice with a tool call; the executor must never run on it directly.
        var plannerModel = new ScriptedAgent(request =>
            ToolResults(request).Any() ? [Say("handled")]
            : request.Any(m => m is ICanGetText t && (t.GetText() ?? "").Contains("child finished"))
                ? [Call("p1", "read_file", $$"""{"path":"a.md","rationale":"{{Rationale}}"}""")]
            : [Say("nothing to do")]
        );
        await using var pair = Build(plannerModel);
        var notice = NotifyMessage.Create(NotifyKinds.SubAgentCompletion, detail: "child finished", label: "linter");

        // As SubAgentManager does for a completed child: a send on the loop that owns the child.
        var receipt = await pair.ExecutorLoop.SendAsync([notice], ct: pair.Cts.Token);

        receipt.Should().NotBeNull();
        await WaitUntilAsync(() => pair.Tools.ReadArgs.Count == 1, pair.Cts.Token);
        // Exactly one executor run, and it is the delegation the planner made in response, with the
        // notice ahead of it as reference. No run was started by the notice itself.
        Delegations(pair.ExecutorModel)
            .Should()
            .ContainSingle()
            .Which.Should()
            .Contain("[notification subagent-completion \"linter\"] child finished")
            .And.Contain("Tool: read_file");
        pair.ExecutorModel.Requests.SelectMany(r => r)
            .OfType<NotifyMessage>()
            .Should()
            .BeEmpty("the executor sees the notice only as reference text, never as an input of its own");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
        {
            await Task.Delay(20, ct);
        }
    }
}
