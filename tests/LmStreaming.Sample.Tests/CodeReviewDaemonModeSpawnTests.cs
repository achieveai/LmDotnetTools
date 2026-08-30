using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using LmStreaming.Sample.Services;
using Microsoft.Extensions.Logging;

namespace LmStreaming.Sample.Tests;

/// <summary>
/// The #628 spawn pin: under the SHIPPED <c>code-review-daemon</c> mode, a restricted
/// code-reviewer-shaped template (frontmatter <c>tools: Read, Grep, Glob, Bash, Skill</c>) still
/// receives the whole task-tool family and the sub-agent tools, and the #623 "ordered to work the
/// board but has no board tools" warning does NOT fire. The mode's <c>subAgentRequiredTools</c>
/// travels the REAL path: yaml → <see cref="SystemChatModes"/> → <see cref="ChatMode.ToAgentProfile"/>
/// → <c>Program.ApplyModeRequiredTools</c> → the real <see cref="SubAgentManager"/> spawn.
/// <para>
/// Mutation-proofing lives in the suite itself: the contrast test below strips the mode's
/// <c>subAgentRequiredTools</c> and shows the SAME spawn losing the task tools and firing the
/// warning — i.e. deleting the field from Prompts.yaml flips the first test red exactly the way
/// the contrast test is green.
/// </para>
/// </summary>
public sealed class CodeReviewDaemonModeSpawnTests : IAsyncLifetime
{
    /// <summary>The #623 warning's stable message marker (see <c>SubAgentManager</c>).</summary>
    private const string WarningMarker = "contains NONE of the task tools";

    /// <summary>The restricted frontmatter of the code-reviewer plugin's review templates.</summary>
    private static readonly IReadOnlyList<string> RestrictedTemplateTools = ["Read", "Grep", "Glob", "Bash", "Skill"];

    private readonly Mock<IMultiTurnAgent> _parentMock = new();
    private readonly List<SubAgentManager> _managers = [];

    public Task InitializeAsync()
    {
        _ = _parentMock
            .Setup(p =>
                p.SendAsync(
                    It.IsAny<List<IMessage>>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new SendReceipt("receipt-1", null, DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var manager in _managers)
        {
            await Wait.ForTeardownAsync(manager, "a sub-agent manager created by this test");
        }
    }

    [Fact]
    public async Task RestrictedReviewTemplate_UnderTheShippedMode_GetsBoardAndSubAgentTools_WithoutTheWarning()
    {
        var mode = SystemChatModes.GetById(SystemChatModes.CodeReviewDaemonModeId)!.ToAgentProfile();
        var logger = new CapturingLogger<SubAgentManager>();
        var manager = CreateManager(mode, logger);

        // The #623 incident's verbatim dispatch shape: the primary orders board work by prompt.
        var childLoop = await SpawnChildAsync(manager, task: "Claim Todo 2.1 under name correctness-reviewer");

        // The union arrives: every task tool and every sub-agent tool, on top of the template's own
        // restricted set.
        childLoop.RegisteredToolNames.Should().Contain(ModeSubAgentRequiredTools.TaskToolNames);
        childLoop.RegisteredToolNames.Should().Contain(SubAgentToolProvider.AllToolNames);
        childLoop.RegisteredToolNames.Should().Contain(RestrictedTemplateTools);

        // And precisely because the board tools arrived, the #623 warning floor stays silent.
        logger.CountAtLevel(LogLevel.Warning, WarningMarker).Should().Be(0);
    }

    /// <summary>
    /// The red half of the pin, kept green in the suite: WITHOUT the mode's
    /// <c>subAgentRequiredTools</c> the very same spawn is stripped to the template list and the
    /// #623 warning fires — proving the assertion above is answered by the yaml field, not by the
    /// harness.
    /// </summary>
    [Fact]
    public async Task SameSpawn_WithTheRequiredToolsFieldStripped_LosesTheBoardToolsAndWarns()
    {
        var stripped = SystemChatModes.GetById(SystemChatModes.CodeReviewDaemonModeId)!.ToAgentProfile() with
        {
            SubAgentRequiredTools = null,
        };
        var logger = new CapturingLogger<SubAgentManager>();
        var manager = CreateManager(stripped, logger);

        var childLoop = await SpawnChildAsync(manager, task: "Claim Todo 2.1 under name correctness-reviewer");

        childLoop.RegisteredToolNames.Should().NotContain(ModeSubAgentRequiredTools.TaskToolNames);
        logger.CountAtLevel(LogLevel.Warning, WarningMarker).Should().Be(1);
    }

    #region Harness

    /// <summary>
    /// A real <see cref="SubAgentManager"/> whose parent surface mirrors what a code-review-daemon
    /// conversation exposes: the task-tool family, the sub-agent tools, and the sandbox file/shell
    /// tools the restricted template names. Options run through the REAL composition-root seam
    /// (<c>Program.ApplyModeRequiredTools</c>) with the profile under test, plus the #623 warning
    /// floor's task-tool roster — the same wiring <c>BuildSubAgentOptionsAsync</c> applies.
    /// </summary>
    private SubAgentManager CreateManager(AgentProfile mode, ILogger logger)
    {
        var parentToolNames = ModeSubAgentRequiredTools
            .TaskToolNames.Concat(SubAgentToolProvider.AllToolNames)
            .Concat(RestrictedTemplateTools)
            .ToList();

        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate> { ["reviewer"] = RestrictedTemplate() },
            MaxConcurrentSubAgents = 5,
            TaskToolNames = ModeSubAgentRequiredTools.TaskToolNames,
        };
        options = global::Program.ApplyModeRequiredTools(options, mode, logger);

        var source = new MutableSubAgentTemplateSource(options.Templates);
        var manager = new SubAgentManager(
            parentAgent: _parentMock.Object,
            parentContracts: [.. parentToolNames.Select(Contract)],
            parentHandlers: parentToolNames.ToDictionary(n => n, _ => OkHandler(), StringComparer.Ordinal),
            options: options,
            source: source,
            logger: logger
        );

        _managers.Add(manager);
        return manager;
    }

    private SubAgentTemplate RestrictedTemplate() =>
        new()
        {
            Name = "reviewer",
            SystemPrompt = "You are a review-dimension worker.",
            EnabledTools = RestrictedTemplateTools,
            AgentFactory = () =>
            {
                var mock = new Mock<IStreamingAgent>();
                _ = mock.Setup(a =>
                        a.GenerateReplyStreamingAsync(
                            It.IsAny<IEnumerable<IMessage>>(),
                            It.IsAny<GenerateReplyOptions>(),
                            It.IsAny<CancellationToken>()
                        )
                    )
                    .Returns<IEnumerable<IMessage>, GenerateReplyOptions?, CancellationToken>(
                        (_, _, ct) => Task.FromResult(BlockingStream(ct))
                    );
                return mock.Object;
            },
        };

    private static FunctionContract Contract(string name) =>
        new()
        {
            Name = name,
            Description = name,
            Parameters = [],
        };

    private static ToolHandler OkHandler() =>
        (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("ok"));

    private static async Task<MultiTurnAgentLoop> SpawnChildAsync(SubAgentManager manager, string task)
    {
        var receipt = await manager.SpawnAsync("reviewer", task, runInBackground: true);
        using var doc = JsonDocument.Parse(receipt);
        var agentId = doc.RootElement.GetProperty("agent_id").GetString()!;
        manager.TryGetAgent(agentId, out var agent).Should().BeTrue();
        return agent.Should().BeOfType<MultiTurnAgentLoop>().Subject;
    }

    private static async IAsyncEnumerable<IMessage> BlockingStream(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct
    )
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }

    #endregion
}
