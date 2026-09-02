using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Compaction;

/// <summary>
/// Pins the default summarizer's provider call (#683; spec 679 §3.2): a fixed system prompt, the rows
/// with their seq numbers, no tools, the usage message handed back, and a reply without a JSON object
/// treated as a failed call rather than an empty manifest.
/// </summary>
public sealed class ProviderCheckpointSummarizerTests
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

    private static CheckpointSummaryRequest Request(ThreadFixture thread) =>
        new()
        {
            ThreadId = "t",
            Rows = thread.Rows,
            RunIds = ["run-1"],
            ModelId = "summary-model",
            Roster =
            [
                new AgentRef
                {
                    AgentId = "agent-1",
                    Status = "Completed",
                    Template = "coder",
                    Task = "lint",
                },
            ],
            PreviousManifest = new ContextManifest { Goals = ["green"] },
            PreviousNarrative = "Earlier we set up.",
        };

    private const string Json = """
        {"instructions":[{"seq":1,"quote":"flaky"}],"goals":["green"],"decisions":[],
         "tasks":[{"title":"rerun","status":"open"}],"artifacts":[{"path":"src/a.cs","origin_seq":2}],
         "headlines":{"run-1":"the fix"},"agent_outcomes":{"agent-1":"linted"},"narrative":"Did it."}
        """;

    [Fact]
    public void BuildPrompt_ListsEveryRowWithItsSeq_AndCarriesThePreviousManifestAndRoster()
    {
        var thread = new ThreadFixture()
            .Human("fix the flaky test")
            .ToolTurn(tool: "Write", args: """{"file_path":"a"}""");

        var prompt = ProviderCheckpointSummarizer.BuildPrompt(Request(thread));

        prompt.Should().Contain("[seq 1] (run-1) user: fix the flaky test");
        prompt.Should().Contain("[seq 2] (run-1) assistant tool call Write {\"file_path\":\"a\"}");
        prompt.Should().Contain("[seq 3] (run-1) tool result Write: ok");
        prompt.Should().Contain("\"goals\":[\"green\"]");
        prompt.Should().Contain("Earlier we set up.");
        prompt.Should().Contain("- agent-1: coder, Completed — lint");
        prompt.Should().Contain("Runs needing a headline: run-1");
    }

    [Fact]
    public void ParseSummary_ToleratesFencesAndProse_AndMapsEveryField()
    {
        var summary = ProviderCheckpointSummarizer.ParseSummary($"Here you go:\n```json\n{Json}\n```\nDone.");

        summary.Should().NotBeNull();
        summary!.Instructions.Should().Equal(new QuotedItem { Seq = 1, Quote = "flaky" });
        summary.Goals.Should().Equal("green");
        summary.Tasks.Should().Equal(new TaskRef { Title = "rerun", Status = "open" });
        summary.Artifacts.Should().Equal(new ArtifactRef { Path = "src/a.cs", OriginSeq = 2 });
        summary.Headlines.Should().Contain("run-1", "the fix");
        summary.AgentOutcomes.Should().Contain("agent-1", "linted");
        summary.Narrative.Should().Be("Did it.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("no json here")]
    [InlineData("{not: valid json")]
    public void ParseSummary_ReturnsNull_WhenThereIsNoObject(string text)
    {
        ProviderCheckpointSummarizer.ParseSummary(text).Should().BeNull();
    }

    [Fact]
    public async Task SummarizeAsync_SendsSystemAndUserTurns_WithoutTools_AndReturnsTheUsage()
    {
        var usage = new UsageMessage
        {
            Usage = new Usage
            {
                PromptTokens = 10,
                CompletionTokens = 5,
                TotalTokens = 15,
            },
        };
        var agent = new FakeAgent(
            (_, _) =>
                [
                    new TextMessage
                    {
                        Text = "thinking",
                        Role = Role.Assistant,
                        IsThinking = true,
                    },
                    new TextMessage { Text = Json, Role = Role.Assistant },
                    usage,
                ]
        );
        var thread = new ThreadFixture().Human("go").ToolTurns(1);

        var response = await new ProviderCheckpointSummarizer(agent, "default-model").SummarizeAsync(Request(thread));

        response.Usage.Should().BeSameAs(usage);
        response.Summary.Narrative.Should().Be("Did it.");
        agent.Sent.Should().HaveCount(2);
        agent
            .Sent[0]
            .Should()
            .BeOfType<TextMessage>()
            .Which.Should()
            .Match<TextMessage>(m => m.Role == Role.System && m.Text == ProviderCheckpointSummarizer.SystemPrompt);
        agent
            .Sent[1]
            .Should()
            .BeOfType<TextMessage>()
            .Which.Should()
            .Match<TextMessage>(m => m.Role == Role.User && m.Text.Contains("[seq 1]"));
        agent.Options!.ModelId.Should().Be("summary-model", "the request's model wins over the default");
        agent.Options.Functions.Should().BeNull();
    }

    [Fact]
    public async Task SummarizeAsync_UsesTheDefaultModel_WhenTheRequestNamesNone()
    {
        var agent = new FakeAgent((_, _) => [new TextMessage { Text = Json, Role = Role.Assistant }]);
        var request = Request(new ThreadFixture().Human("go")) with { ModelId = null };

        var response = await new ProviderCheckpointSummarizer(agent, "default-model").SummarizeAsync(request);

        response.Usage.Should().BeNull();
        agent.Options!.ModelId.Should().Be("default-model");
    }

    [Fact]
    public async Task SummarizeAsync_Throws_WhenTheReplyHasNoJsonObject()
    {
        var agent = new FakeAgent((_, _) => [new TextMessage { Text = "I cannot do that.", Role = Role.Assistant }]);

        var act = () =>
            new ProviderCheckpointSummarizer(agent).SummarizeAsync(Request(new ThreadFixture().Human("go")));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
