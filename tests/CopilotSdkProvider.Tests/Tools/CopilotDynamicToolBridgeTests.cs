using System.Text.Json;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Models;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Tools;
using AchieveAi.LmDotnetTools.LmCore.Core;

namespace AchieveAi.LmDotnetTools.CopilotSdkProvider.Tests.Tools;

public class CopilotDynamicToolBridgeTests
{
    private static FunctionContract[] BuildContracts()
    {
        return
        [
            new FunctionContract
            {
                Name = "calculate",
                Description = "calc",
            },
        ];
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFailure_WhenToolIsDenied()
    {
        var handlers = new Dictionary<string, Func<string, Task<string>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["calculate"] = _ => Task.FromResult("10"),
        };
        var policy = new CopilotToolPolicyEngine(
            dynamicToolNames: ["calculate"],
            enabledTools: ["get_weather"]);
        var bridge = new CopilotDynamicToolBridge(BuildContracts(), handlers, policy);
        var args = JsonDocument.Parse("""{"a":2}""").RootElement.Clone();

        var response = await bridge.ExecuteAsync(
            new CopilotDynamicToolCallRequest
            {
                Tool = "calculate",
                Arguments = args,
            });

        response.Success.Should().BeFalse();
        response.ContentItems.Should().ContainSingle();
        response.ContentItems[0].Type.Should().Be("text");
        response.ContentItems[0].Text.Should().Contain("not enabled");
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsHandlerOutput_WhenAllowed()
    {
        var handlers = new Dictionary<string, Func<string, Task<string>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["calculate"] = _ => Task.FromResult("10"),
        };
        var policy = new CopilotToolPolicyEngine(
            dynamicToolNames: ["calculate"],
            enabledTools: ["calculate"]);
        var bridge = new CopilotDynamicToolBridge(BuildContracts(), handlers, policy);
        var args = JsonDocument.Parse("""{"a":2}""").RootElement.Clone();

        var response = await bridge.ExecuteAsync(
            new CopilotDynamicToolCallRequest
            {
                Tool = "calculate",
                Arguments = args,
            });

        response.Success.Should().BeTrue();
        response.ContentItems.Should().ContainSingle();
        response.ContentItems[0].Type.Should().Be("text");
        response.ContentItems[0].Text.Should().Be("10");
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFailure_WhenHandlerMissing()
    {
        var handlers = new Dictionary<string, Func<string, Task<string>>>(StringComparer.OrdinalIgnoreCase);
        var policy = new CopilotToolPolicyEngine(
            dynamicToolNames: ["calculate"],
            enabledTools: ["calculate"]);
        var bridge = new CopilotDynamicToolBridge(BuildContracts(), handlers, policy);
        var args = JsonDocument.Parse("""{"a":2}""").RootElement.Clone();

        var response = await bridge.ExecuteAsync(
            new CopilotDynamicToolCallRequest
            {
                Tool = "calculate",
                Arguments = args,
            });

        response.Success.Should().BeFalse();
        response.ContentItems[0].Text.Should().Contain("not registered");
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFailure_WhenHandlerThrows()
    {
        var handlers = new Dictionary<string, Func<string, Task<string>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["calculate"] = _ => throw new InvalidOperationException("boom"),
        };
        var policy = new CopilotToolPolicyEngine(
            dynamicToolNames: ["calculate"],
            enabledTools: ["calculate"]);
        var bridge = new CopilotDynamicToolBridge(BuildContracts(), handlers, policy);
        var args = JsonDocument.Parse("""{"a":2}""").RootElement.Clone();

        var response = await bridge.ExecuteAsync(
            new CopilotDynamicToolCallRequest
            {
                Tool = "calculate",
                Arguments = args,
            });

        response.Success.Should().BeFalse();
        response.ContentItems[0].Text.Should().Be("boom");
    }

    [Fact]
    public void GetToolSpecs_RespectsPolicy()
    {
        var contracts = new[]
        {
            new FunctionContract { Name = "calculate", Description = "c" },
            new FunctionContract { Name = "get_weather", Description = "w" },
        };
        var handlers = new Dictionary<string, Func<string, Task<string>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["calculate"] = _ => Task.FromResult(""),
            ["get_weather"] = _ => Task.FromResult(""),
        };
        var policy = new CopilotToolPolicyEngine(
            dynamicToolNames: ["calculate", "get_weather"],
            enabledTools: ["calculate"]);
        var bridge = new CopilotDynamicToolBridge(contracts, handlers, policy);

        var specs = bridge.GetToolSpecs();

        specs.Should().ContainSingle();
        specs[0].Name.Should().Be("calculate");
    }
}
