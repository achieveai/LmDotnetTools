using AchieveAi.LmDotnetTools.LmCore.Agents;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests;

/// <summary>
/// The <c>ClientTools:AskUserQuestion</c> host switch reaches the sub-agent tree: an unattended host
/// (the compaction eval runner) must not have a child park the run on a question nobody answers.
/// </summary>
public class ProgramClientToolsSwitchTests
{
    [Fact]
    public async Task Sub_agent_options_keep_the_question_tool_by_default()
    {
        var options = await BuildAsync(includeAskUserQuestionTool: null);

        options.IncludeAskUserQuestionTool.Should().BeTrue();
    }

    [Fact]
    public async Task Sub_agent_options_drop_the_question_tool_when_the_host_switch_is_off()
    {
        var options = await BuildAsync(includeAskUserQuestionTool: false);

        options.IncludeAskUserQuestionTool.Should().BeFalse();
    }

    private static async Task<AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents.SubAgentOptions> BuildAsync(
        bool? includeAskUserQuestionTool
    )
    {
        var options = includeAskUserQuestionTool is { } include
            ? await global::Program.BuildSubAgentOptionsAsync(
                isTestMode: false,
                testAgentBuilder: Mock.Of<ITestAgentBuilder>(),
                loggerFactory: NullLoggerFactory.Instance,
                providerAgentFactory: () => Mock.Of<IStreamingAgent>(),
                characteristicsAgentFactory: _ => throw new InvalidOperationException("not spawned here"),
                sandboxSession: null,
                workspaceLoader: null!,
                marketplaceLoader: null!,
                workspaceStore: null!,
                logger: NullLogger.Instance,
                mode: new AgentProfile("mode-1", "Mode One", "primary prompt"),
                includeAskUserQuestionTool: include
            )
            : await global::Program.BuildSubAgentOptionsAsync(
                isTestMode: false,
                testAgentBuilder: Mock.Of<ITestAgentBuilder>(),
                loggerFactory: NullLoggerFactory.Instance,
                providerAgentFactory: () => Mock.Of<IStreamingAgent>(),
                characteristicsAgentFactory: _ => throw new InvalidOperationException("not spawned here"),
                sandboxSession: null,
                workspaceLoader: null!,
                marketplaceLoader: null!,
                workspaceStore: null!,
                logger: NullLogger.Instance,
                mode: new AgentProfile("mode-1", "Mode One", "primary prompt")
            );

        return options ?? throw new InvalidOperationException("built-in catalog yields options");
    }
}
