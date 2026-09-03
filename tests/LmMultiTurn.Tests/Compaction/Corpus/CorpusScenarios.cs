using AchieveAi.LmDotnetTools.LmMultiTurn.ClientTools;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;

namespace LmMultiTurn.Tests.Compaction.Corpus;

/// <summary>
/// The fixed corpus (spec 679 §12.4 (a)-(m)). Sizes are the ones <c>CompactionLoopTests</c> made visible:
/// the window is 2400 tokens, the reserve is the loop's MaxToken (100) with no margin, every Echo result
/// is 312 tokens (~330 with its call row), so warn is 1610, compact is 1840 and the hard row is 2060.
/// A scenario that must compact in Compact mode and still fit a raw provider request in Off mode sits
/// between 1840 and 2400; (a) deliberately runs far past the window.
/// </summary>
public static class CorpusScenarios
{
    public const long Window = 2_400;
    public const string Model = "corpus-model";

    /// <summary>A user turn long enough (250 tokens) to push a warn-band thread into the compact band.</summary>
    private static readonly string LongInstruction = "Please continue with the analysis. " + new string('y', 960);

    private static string Spawn(string template, string prompt, bool background = false) =>
        $$"""{"subagent_type":"{{template}}","prompt":"{{prompt}}","role":"{{template}}","description":"{{prompt}}"{{(background ? ",\"run_in_background\":true" : "")}}}""";

    private const string AskArgs = """
        {"context":"The plan needs a human decision before the next step.","questions":[{"prompt":"Proceed with the second approach?","header":"Approach","options":[{"label":"Yes","description":"Use the second approach."},{"label":"No","description":"Stop here."}]}]}
        """;

    private const string WaitArgs = """{"kind":"timer","args":{"delay":"3s"},"timeout":"10m"}""";

    public static IReadOnlyList<CorpusScenario> All { get; } =
    [
        new CorpusScenario
        {
            Id = "a",
            Item = "primary 40-turn tool-heavy",
            Title = "A single run of 40 tool turns; the raw thread is 5x the window.",
            Steps = [CorpusStep.Say("Audit every file and report.")],
            Root = CorpusScript.EchoThenDone(40),
            ExceedsWindow = true,
        },
        new CorpusScenario
        {
            Id = "b",
            Item = "mid-run correction + errored run",
            Title = "A planned run, a run the provider fails, then a run corrected while in flight (R4).",
            Steps =
            [
                CorpusStep.Say("Plan the work."),
                CorpusStep.Board(),
                CorpusStep.Say("Go.", expectError: true),
                CorpusStep.SayWithCorrection("Retry the work.", afterCall: 7, "Correction: use the second approach."),
            ],
            Root = new CorpusScript
            {
                Replies =
                [
                    ScriptedReply.Call("Echo", "b-1"),
                    ScriptedReply.Call("Echo", "b-2"),
                    ScriptedReply.Say("Planned: two steps."),
                    ScriptedReply.Call("Echo", "b-3"),
                    ScriptedReply.Fail("provider failed mid-run"),
                    ScriptedReply.Call("Echo", "b-4"),
                    ScriptedReply.Call("Echo", "b-5"),
                    ScriptedReply.Call("Echo", "b-6"),
                ],
            },
            BoardTasks = ["Write the plan", "Ship the second approach"],
        },
        new CorpusScenario
        {
            Id = "c",
            Item = "primary spawning three sub-agents, one non-terminal at the cut",
            Title = "Two foreground workers finish; a background worker is still running when the cut lands.",
            Steps = [CorpusStep.Say("Delegate the three tasks."), CorpusStep.Release("slow", runs: 1)],
            Root = new CorpusScript
            {
                Replies =
                [
                    ScriptedReply.Call(SubAgentToolProvider.SpawnToolName, "c-spawn-1", Spawn("worker", "first task")),
                    ScriptedReply.Call(SubAgentToolProvider.SpawnToolName, "c-spawn-2", Spawn("worker", "second task")),
                    ScriptedReply.Call(
                        SubAgentToolProvider.SpawnToolName,
                        "c-spawn-3",
                        Spawn("slow", "third task", background: true)
                    ),
                    .. CorpusScript.EchoThenDone(6, idPrefix: "c").Replies,
                ],
            },
            Children = new Dictionary<string, CorpusScript>(StringComparer.Ordinal)
            {
                ["worker"] = new CorpusScript { Default = ScriptedReply.Say("worker done") },
                ["slow"] = new CorpusScript { Default = ScriptedReply.Block("slow", "slow done") },
            },
        },
        new CorpusScenario
        {
            Id = "d",
            Item = "sub-agent that compacts itself",
            Title = "The root delegates once; the child runs six tool turns and compacts its own thread.",
            Steps = [CorpusStep.Say("Delegate the deep task.")],
            Root = new CorpusScript
            {
                Replies =
                [
                    ScriptedReply.Call(SubAgentToolProvider.SpawnToolName, "d-spawn-1", Spawn("deep", "deep task")),
                ],
            },
            Children = new Dictionary<string, CorpusScript>(StringComparer.Ordinal)
            {
                ["deep"] = CorpusScript.EchoThenDone(6, "deep done", "deep"),
            },
            ExpectRootCompaction = false,
            ExpectChildCompaction = true,
        },
        new CorpusScenario
        {
            Id = "e",
            Item = "workflow controller + two tasks",
            Title = "A workflow-controller loop delegates two tasks, then works six tool turns of its own.",
            Steps = [CorpusStep.Say("Run the workflow.")],
            Root = new CorpusScript
            {
                Replies =
                [
                    ScriptedReply.Call(SubAgentToolProvider.SpawnToolName, "e-spawn-1", Spawn("task", "task one")),
                    ScriptedReply.Call(SubAgentToolProvider.SpawnToolName, "e-spawn-2", Spawn("task", "task two")),
                    .. CorpusScript.EchoThenDone(6, idPrefix: "e").Replies,
                ],
            },
            Children = new Dictionary<string, CorpusScript>(StringComparer.Ordinal)
            {
                ["task"] = CorpusScript.EchoThenDone(1, "task done", "task"),
            },
            WorkflowController = true,
        },
        new CorpusScenario
        {
            Id = "f",
            Item = "continuation after an interrupted stream",
            Title = "The stream ends prematurely on turn six; the loop retries with a continuation instruction.",
            Steps = [CorpusStep.Say("Keep going until done.")],
            Root = new CorpusScript
            {
                Replies =
                [
                    .. CorpusScript.Echoes(6, "f"),
                    ScriptedReply.Interrupted(),
                    ScriptedReply.Call("Echo", "f-7"),
                ],
            },
            MustSkipWith = CompactionSkipReasons.UnsafeState,
        },
        new CorpusScenario
        {
            Id = "g",
            Item = "deferred AskUserQuestion outstanding (must skip)",
            Title =
                "A question is parked with the client: no request is built and no cut lands until the human answers (R6).",
            Steps =
            [
                CorpusStep.Say("Work through the plan."),
                CorpusStep.Say("Any progress?", expectError: true),
                CorpusStep.Resolve("g-ask-1", """{"answers":{"q0":"Yes"}}"""),
            ],
            Root = new CorpusScript
            {
                Replies =
                [
                    .. CorpusScript.Echoes(6, "g"),
                    ScriptedReply.Call(AskUserQuestionToolProvider.ToolName, "g-ask-1", AskArgs),
                ],
            },
            IncludeAskUserQuestionTool = true,
            ParksAtCall = 7,
        },
        new CorpusScenario
        {
            Id = "h",
            Item = "parked Wait (must skip)",
            Title =
                "A timer Wait is parked: a run that arrives meanwhile is refused, no cut lands, the timer resumes the run (R6).",
            Steps =
            [
                CorpusStep.Say("Work, then wait."),
                CorpusStep.Say("Any progress?", expectError: true),
                CorpusStep.AwaitRuns(1),
            ],
            Root = new CorpusScript
            {
                Replies =
                [
                    .. CorpusScript.Echoes(6, "h"),
                    ScriptedReply.Call(WaitToolProvider.WaitToolName, "h-wait-1", WaitArgs),
                ],
            },
            IncludeWaitTool = true,
            ParksAtCall = 7,
        },
        new CorpusScenario
        {
            Id = "i",
            Item = "restart mid-conversation",
            Title = "The process restarts between two runs; the second loop adopts the checkpoint the first activated.",
            Steps =
            [
                CorpusStep.Say("Start the work."),
                CorpusStep.Restart(),
                CorpusStep.Say("Continue after the restart."),
            ],
            Root = new CorpusScript { Replies = [.. CorpusScript.Echoes(6, "i"), ScriptedReply.Say("paused")] },
        },
        new CorpusScenario
        {
            Id = "j",
            Item = "recall round trip",
            Title =
                "After the cut the model recalls the compacted instruction by keyword and gets it verbatim (window 2500: the recall pair sits on top of the seven turns that reach the hard band).",
            Steps = [CorpusStep.Say("Start the recall work.")],
            WindowTokens = 2_500,
            Root = new CorpusScript
            {
                Replies =
                [
                    .. CorpusScript.Echoes(7, "j"),
                    ScriptedReply.Call(
                        RecallConversationToolProvider.ToolName,
                        "j-recall-1",
                        """{"query":"recall work"}"""
                    ),
                ],
            },
        },
        new CorpusScenario
        {
            Id = "k",
            Item = "legacy rows without Seq",
            Title = "Five rows older than the binary carry no Seq; the first append backfills them before any cut.",
            Steps = [CorpusStep.Say("Continue the old thread.")],
            Root = CorpusScript.EchoThenDone(2, idPrefix: "k"),
            Store = "file-legacy",
            LegacyRows = [.. Enumerable.Range(1, 5).Select(i => $"legacy row {i} " + new string('z', 1_200))],
        },
        new CorpusScenario
        {
            Id = "l",
            Item = "unknown model (capacity unknown)",
            Title = "No window is known for the model: the policy can only warn, and no cost can be estimated.",
            Steps = [CorpusStep.Say("Work on the unknown model.")],
            Root = CorpusScript.EchoThenDone(3, idPrefix: "l"),
            ModelId = "unknown-model",
            WindowTokens = null,
            Pricing = CorpusPricing.None,
            ExpectRootCompaction = false,
            MustSkipWith = CompactionSkipReasons.CapacityUnknown,
        },
        new CorpusScenario
        {
            Id = "m",
            Item = "unpriced category (partial cost)",
            Title =
                "Cache reads are reported but the price list has no cache rate: the cost is partial, never silently complete.",
            Steps = [CorpusStep.Say("Work on the partially priced model.")],
            Root = CorpusScript.EchoThenDone(7, idPrefix: "m"),
            Pricing = CorpusPricing.NoCacheRates,
        },
    ];

    public static CorpusScenario ById(string id) => All.Single(s => string.Equals(s.Id, id, StringComparison.Ordinal));
}
