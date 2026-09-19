using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Compaction;

/// <summary>
/// Pins the cached-prefix summary pass (eval spec §5.2): the agent's own prefix goes out verbatim, the
/// instruction and a seq-only row index follow as one user turn, the loop's tools are sent with
/// <c>tool_choice=none</c>, and a reply that calls a tool fails the pass instead of building a checkpoint.
/// </summary>
public sealed class CachedPrefixCheckpointSummarizerTests
{
    private sealed class FakeAgent(Func<IEnumerable<IMessage>, GenerateReplyOptions?, IEnumerable<IMessage>> reply)
        : IAgent
    {
        public List<IMessage> Sent { get; } = [];

        public GenerateReplyOptions? Options { get; private set; }

        public Task<IEnumerable<IMessage>> GenerateReplyAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            Sent.AddRange(messages);
            Options = options;
            return Task.FromResult(reply(messages, options));
        }
    }

    private const string Json =
        """{"instructions":[],"goals":[],"decisions":[],"tasks":[],"artifacts":[],"headlines":{"run-1":"h"},"agent_outcomes":{},"narrative":"n"}""";

    private static readonly IReadOnlyList<IMessage> Prefix =
    [
        new TextMessage { Text = "AGENT SYSTEM PROMPT", Role = Role.System },
        new TextMessage { Text = "go", Role = Role.User },
        new TextMessage { Text = "ok", Role = Role.Assistant },
    ];

    private static readonly FunctionContract Tool = new() { Name = "Read", Description = "d" };

    private static CheckpointSummaryRequest Request(ThreadFixture thread) =>
        new()
        {
            ThreadId = "t",
            Rows = thread.Rows,
            RunIds = ["run-1"],
            MaxOutputTokens = 500,
        };

    [Fact]
    public async Task SummarizeAsync_SendsTheAgentsPrefixVerbatim_ThenOneInstructionTurn_WithToolsButNoToolUse()
    {
        var agent = new FakeAgent((_, _) => [new TextMessage { Text = Json, Role = Role.Assistant }]);
        var thread = new ThreadFixture().Human("go").Assistant("ok");
        var asked = new List<long>();
        var summarizer = new CachedPrefixCheckpointSummarizer(
            agent,
            seq =>
            {
                asked.Add(seq);
                return Prefix;
            },
            () => [Tool],
            instruction: "SUMMARIZE NOW"
        );

        var response = await summarizer.SummarizeAsync(Request(thread));

        asked.Should().Equal(thread.LastSeq);
        agent.Sent.Take(3).Should().BeEquivalentTo(Prefix, o => o.WithStrictOrdering());
        var tail = agent.Sent[3].Should().BeOfType<TextMessage>().Subject;
        tail.Role.Should().Be(Role.User);
        tail.Text.Should().StartWith("SUMMARIZE NOW");
        tail.Text.Should().Contain("[seq 1] (run-1) user: go");
        agent.Options!.Functions.Should().ContainSingle().Which.Name.Should().Be("Read");
        agent.Options.ToolChoice.Should().Be("none");
        agent.Options.MaxToken.Should().Be(500);
        response.Summary.Headlines["run-1"].Should().Be("h");
    }

    [Fact]
    public async Task SummarizeAsync_FailsWhenTheModelCallsATool()
    {
        var agent = new FakeAgent(
            (_, _) =>
                [
                    new ToolCallMessage
                    {
                        ToolCallId = "x",
                        FunctionName = "Read",
                        FunctionArgs = "{}",
                    },
                ]
        );
        var summarizer = new CachedPrefixCheckpointSummarizer(agent, _ => Prefix, () => [Tool]);

        var act = () => summarizer.SummarizeAsync(Request(new ThreadFixture().Human("go").Assistant("ok")));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*called a tool*");
    }

    [Fact]
    public void BuildPromptIndex_ListsEveryRowByHeadOnly()
    {
        var thread = new ThreadFixture().Human(new string('h', 300)).Assistant("ok");

        var index = ProviderCheckpointSummarizer.BuildPromptIndex(Request(thread));

        // The head is measured over the whole described line, label included: "user: " plus 74 characters.
        index.Should().Contain("[seq 1] (run-1) user: " + new string('h', 74) + "…");
        index.Should().NotContain(new string('h', 75));
        index.Should().Contain("[seq 2] (run-1) assistant: ok");
        index.Should().Contain("Rows being compacted (already in your context above; cite by seq):");
    }
}
