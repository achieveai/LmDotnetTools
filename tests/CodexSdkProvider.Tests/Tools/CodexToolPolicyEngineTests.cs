using AchieveAi.LmDotnetTools.CodexSdkProvider.Models;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Tools;

namespace AchieveAi.LmDotnetTools.CodexSdkProvider.Tests.Tools;

public class CodexToolPolicyEngineTests
{
    [Fact]
    public void IsMcpToolAllowed_RespectsEnabledThenDisabledPrecedence()
    {
        var policy = new CodexToolPolicyEngine(
            new Dictionary<string, CodexMcpServerConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["sample"] = new()
                {
                    Enabled = true,
                    EnabledTools = ["calculate", "get_weather"],
                    DisabledTools = ["get_weather"],
                },
            });

        policy.IsMcpToolAllowed("sample", "calculate").Should().BeTrue();
        policy.IsMcpToolAllowed("sample", "get_weather").Should().BeFalse();
    }

    [Fact]
    public void IsDynamicToolAllowed_UsesRegisteredToolsAndModeAllowlist()
    {
        var policy = new CodexToolPolicyEngine(
            dynamicToolNames: ["calculate", "get_weather"],
            enabledTools: ["calculate"]);

        policy.IsDynamicToolAllowed("calculate").Should().BeTrue();
        policy.IsDynamicToolAllowed("get_weather").Should().BeFalse();
        policy.IsDynamicToolAllowed("unknown").Should().BeFalse();
    }

    [Fact]
    public void IsBuiltInAllowed_UsesEnabledToolListWhenPresent()
    {
        var policy = new CodexToolPolicyEngine(enabledTools: ["calculate", "web_search"]);

        policy.IsBuiltInAllowed("web_search").Should().BeTrue();
        policy.IsBuiltInAllowed("view_image").Should().BeFalse();
    }
}
