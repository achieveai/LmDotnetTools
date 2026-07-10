using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmSampleShared.Discovery;

namespace LmStreaming.Sample.Tests.Services.Discovery;

/// <summary>
/// Pins <see cref="SubAgentTemplateMapper.Map"/>'s model handling. The load-bearing case is the
/// Claude-Code <c>model: inherit</c> convention used by marketplace sub-agents (e.g. gb-plugins
/// <c>code-reviewer:*</c>): it means "use the parent agent's model" and is NOT a real model id, so it
/// must map to <c>DefaultOptions == null</c> (inherit the parent). Passing it through verbatim made the
/// GitHub Copilot backend reject every sub-agent turn with <c>model_not_supported</c> (found in the live
/// Code-Review Daemon run).
/// </summary>
public class SubAgentTemplateMapperTests
{
    private static readonly Func<IStreamingAgent> AgentFactory = () => new Mock<IStreamingAgent>().Object;

    private static ParsedSubAgent Parsed(string? model) =>
        new("architecture-review", "desc", model, Tools: null, SystemPrompt: "You review architecture.");

    [Theory]
    [InlineData("inherit")]
    [InlineData("Inherit")]
    [InlineData("INHERIT")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Map_InheritOrBlankModel_LeavesDefaultOptionsNull(string? model)
    {
        var template = SubAgentTemplateMapper.Map(Parsed(model), AgentFactory, maxTurnsPerRun: 25);

        template.DefaultOptions.Should().BeNull(
            "'inherit'/blank means inherit the parent model, not send a literal unsupported model id");
        template.IsModelExplicitlySelected.Should().BeFalse();
    }

    [Fact]
    public void Map_ConcreteModel_SetsModelId()
    {
        var template = SubAgentTemplateMapper.Map(Parsed("claude-sonnet-5"), AgentFactory, maxTurnsPerRun: 25);

        template.DefaultOptions.Should().NotBeNull();
        template.DefaultOptions!.ModelId.Should().Be("claude-sonnet-5");
        template.IsModelExplicitlySelected.Should().BeTrue();
    }

    [Fact]
    public void Map_Effort_CarriesTypedValue()
    {
        var parsed = Parsed("claude-sonnet-5") with { Effort = ReasoningEffort.Xhigh };

        var template = SubAgentTemplateMapper.Map(parsed, AgentFactory, maxTurnsPerRun: 25);

        template.Effort.Should().Be(ReasoningEffort.Xhigh);
    }
}
