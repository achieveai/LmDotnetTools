using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Pins the sub-agent model-inheritance contract (<see cref="SubAgentManager.ResolveSubAgentOptions"/>):
/// a spawned sub-agent must inherit the PARENT's model when neither a per-spawn override nor the
/// template specifies one. Without this, a template carrying no model (e.g. the built-in sub-agents)
/// lets the provider agent fall back to its hardcoded default model — observed in production as a
/// sub-agent sending <c>claude-3-sonnet-20240229</c> to a backend that only serves the parent's
/// model, yielding HTTP 400 <c>model_not_supported</c>.
/// </summary>
public class SubAgentModelResolutionTests
{
    [Fact]
    public void Override_WinsOverTemplateAndParent()
    {
        var result = SubAgentManager.ResolveSubAgentOptions(
            templateDefaults: new GenerateReplyOptions { ModelId = "template-model" },
            modelOverride: "override-model",
            parentModelId: "parent-model");

        result!.ModelId.Should().Be("override-model");
    }

    [Fact]
    public void TemplateModel_UsedWhenNoOverride_ParentIgnored()
    {
        var result = SubAgentManager.ResolveSubAgentOptions(
            templateDefaults: new GenerateReplyOptions { ModelId = "template-model" },
            modelOverride: null,
            parentModelId: "parent-model");

        result!.ModelId.Should().Be("template-model");
    }

    [Fact]
    public void ParentModel_InheritedWhenNeitherOverrideNorTemplateModel()
    {
        // The fix: a built-in template (no DefaultOptions at all) inherits the parent's model
        // instead of letting the provider agent use its stale hardcoded default.
        var result = SubAgentManager.ResolveSubAgentOptions(
            templateDefaults: null,
            modelOverride: null,
            parentModelId: "parent-model");

        result.Should().NotBeNull();
        result!.ModelId.Should().Be("parent-model");
    }

    [Fact]
    public void ParentModel_InheritedWhenTemplateHasOptionsButBlankModel()
    {
        // ModelId defaults to "" on GenerateReplyOptions — that must count as "no model".
        var result = SubAgentManager.ResolveSubAgentOptions(
            templateDefaults: new GenerateReplyOptions { Temperature = 0.5f },
            modelOverride: null,
            parentModelId: "parent-model");

        result!.ModelId.Should().Be("parent-model");
        result.Temperature.Should().Be(0.5f, "applying the parent model must preserve other template options");
    }

    [Fact]
    public void ReturnsNull_WhenNoModelAnywhere_AndNoTemplateOptions()
    {
        // Nothing to set and no template options → keep the previous "inherit provider defaults"
        // behavior (null options), rather than fabricating an empty options object.
        var result = SubAgentManager.ResolveSubAgentOptions(
            templateDefaults: null,
            modelOverride: null,
            parentModelId: null);

        result.Should().BeNull();
    }

    [Fact]
    public void KeepsTemplateOptions_WhenNoModelAnywhere_ButTemplateHasOtherFields()
    {
        var templateDefaults = new GenerateReplyOptions { Temperature = 0.2f };

        var result = SubAgentManager.ResolveSubAgentOptions(
            templateDefaults: templateDefaults,
            modelOverride: null,
            parentModelId: null);

        result.Should().BeSameAs(templateDefaults);
        result!.ModelId.Should().BeEmpty();
    }
}
