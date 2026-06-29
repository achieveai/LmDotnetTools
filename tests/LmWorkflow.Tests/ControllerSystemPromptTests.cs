using AchieveAi.LmDotnetTools.LmWorkflow.Prompts;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Guards the production controller prompt: it must be present and must name the four tools the
///     controller drives the workflow with, so a regression that empties or truncates it is caught.
/// </summary>
public class ControllerSystemPromptTests
{
    [Fact]
    public void Default_IsNonEmpty_AndNamesTheKeyTools()
    {
        var prompt = ControllerSystemPrompt.Default;

        prompt.Should().NotBeNullOrWhiteSpace();
        prompt.Should().Contain("SetWorkflow");
        prompt.Should().Contain("GetWorkflow");
        prompt.Should().Contain("SetCurrentNode");
        prompt.Should().Contain("Agent");
    }
}
