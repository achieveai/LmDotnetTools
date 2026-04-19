using AchieveAi.LmDotnetTools.CopilotSdkProvider.Tools;

namespace AchieveAi.LmDotnetTools.CopilotSdkProvider.Tests.Tools;

public class CopilotToolPolicyEngineTests
{
    [Fact]
    public void IsBuiltInAllowed_NullEnabledTools_AllowsAny()
    {
        var policy = new CopilotToolPolicyEngine();

        policy.IsBuiltInAllowed("web_search").Should().BeTrue();
        policy.IsBuiltInAllowed("bash").Should().BeTrue();
    }

    [Fact]
    public void IsBuiltInAllowed_EnabledToolListPresent_OnlyAllowsListed()
    {
        var policy = new CopilotToolPolicyEngine(enabledTools: ["web_search"]);

        policy.IsBuiltInAllowed("web_search").Should().BeTrue();
        policy.IsBuiltInAllowed("bash").Should().BeFalse();
    }

    [Fact]
    public void IsBuiltInAllowed_EmptyOrWhitespaceName_ReturnsFalse()
    {
        var policy = new CopilotToolPolicyEngine();

        policy.IsBuiltInAllowed("").Should().BeFalse();
        policy.IsBuiltInAllowed("   ").Should().BeFalse();
    }

    [Fact]
    public void IsDynamicToolAllowed_OnlyRegisteredNames_ArePermitted()
    {
        var policy = new CopilotToolPolicyEngine(
            dynamicToolNames: ["calculate", "get_weather"]);

        policy.IsDynamicToolAllowed("calculate").Should().BeTrue();
        policy.IsDynamicToolAllowed("get_weather").Should().BeTrue();
        policy.IsDynamicToolAllowed("unknown").Should().BeFalse();
    }

    [Fact]
    public void IsDynamicToolAllowed_EnabledToolsAllowlistApplied()
    {
        var policy = new CopilotToolPolicyEngine(
            dynamicToolNames: ["calculate", "get_weather"],
            enabledTools: ["calculate"]);

        policy.IsDynamicToolAllowed("calculate").Should().BeTrue();
        policy.IsDynamicToolAllowed("get_weather").Should().BeFalse();
    }

    [Fact]
    public void IsDynamicToolAllowed_NullOrWhitespace_ReturnsFalse()
    {
        var policy = new CopilotToolPolicyEngine(dynamicToolNames: ["calculate"]);

        policy.IsDynamicToolAllowed(null).Should().BeFalse();
        policy.IsDynamicToolAllowed("").Should().BeFalse();
        policy.IsDynamicToolAllowed("   ").Should().BeFalse();
    }

    [Fact]
    public void CaseInsensitive_ToolNameMatching()
    {
        var policy = new CopilotToolPolicyEngine(
            dynamicToolNames: ["Calculate"],
            enabledTools: ["calculate"]);

        policy.IsDynamicToolAllowed("CALCULATE").Should().BeTrue();
        policy.IsBuiltInAllowed("CALCULATE").Should().BeTrue();
    }
}
