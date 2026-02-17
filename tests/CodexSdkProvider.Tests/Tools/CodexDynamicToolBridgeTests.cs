using System.Text.Json;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Models;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Tools;
using AchieveAi.LmDotnetTools.LmCore.Core;

namespace AchieveAi.LmDotnetTools.CodexSdkProvider.Tests.Tools;

public class CodexDynamicToolBridgeTests
{
    [Fact]
    public async Task ExecuteAsync_ReturnsFailure_WhenToolIsDenied()
    {
        var contracts = new[]
        {
            new FunctionContract
            {
                Name = "calculate",
                Description = "calc",
            },
        };
        var handlers = new Dictionary<string, Func<string, Task<string>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["calculate"] = _ => Task.FromResult("10"),
        };
        var policy = new CodexToolPolicyEngine(dynamicToolNames: ["calculate"], enabledTools: ["get_weather"]);
        var bridge = new CodexDynamicToolBridge(contracts, handlers, policy);
        var args = JsonDocument.Parse("""{"a":2}""").RootElement.Clone();

        var response = await bridge.ExecuteAsync(
            new CodexDynamicToolCallRequest
            {
                Tool = "calculate",
                Arguments = args,
            });

        response.Success.Should().BeFalse();
        response.ContentItems.Should().ContainSingle();
        response.ContentItems[0].Text.Should().Contain("not enabled");
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsHandlerOutput_WhenAllowed()
    {
        var contracts = new[]
        {
            new FunctionContract
            {
                Name = "calculate",
                Description = "calc",
            },
        };
        var handlers = new Dictionary<string, Func<string, Task<string>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["calculate"] = _ => Task.FromResult("10"),
        };
        var policy = new CodexToolPolicyEngine(dynamicToolNames: ["calculate"], enabledTools: ["calculate"]);
        var bridge = new CodexDynamicToolBridge(contracts, handlers, policy);
        var args = JsonDocument.Parse("""{"a":2}""").RootElement.Clone();

        var response = await bridge.ExecuteAsync(
            new CodexDynamicToolCallRequest
            {
                Tool = "calculate",
                Arguments = args,
            });

        response.Success.Should().BeTrue();
        response.ContentItems.Should().ContainSingle();
        response.ContentItems[0].Text.Should().Be("10");
    }
}
