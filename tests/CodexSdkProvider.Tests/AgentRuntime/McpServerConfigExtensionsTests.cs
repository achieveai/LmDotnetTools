using AchieveAi.LmDotnetTools.CodexSdkProvider.AgentRuntime;
using AchieveAi.LmDotnetTools.LmCore.AgentRuntime;

namespace AchieveAi.LmDotnetTools.CodexSdkProvider.Tests.AgentRuntime;

public class McpServerConfigExtensionsTests
{
    [Fact]
    public void ToCodexConfig_StdioSource_MapsCommandArgsEnv()
    {
        var source = McpServerConfig.CreateStdio(
            "node",
            ["a.js", "--flag"],
            new Dictionary<string, string> { ["K"] = "V" });

        var codex = source.ToCodexConfig();

        codex.Command.Should().Be("node");
        codex.Args.Should().Equal("a.js", "--flag");
        codex.Env.Should().ContainKey("K").WhoseValue.Should().Be("V");
        codex.Url.Should().BeNull();
        codex.Enabled.Should().BeNull();
        codex.EnabledTools.Should().BeNull();
        codex.DisabledTools.Should().BeNull();
    }

    [Fact]
    public void ToCodexConfig_HttpSource_MapsUrl()
    {
        var source = McpServerConfig.CreateHttp("https://mcp.example/v1");

        var codex = source.ToCodexConfig();

        codex.Url.Should().Be("https://mcp.example/v1");
        codex.Command.Should().BeNull();
        codex.Args.Should().BeNull();
    }

    [Fact]
    public void ToCodexConfig_NullSource_Throws()
    {
        McpServerConfig? source = null;

        var act = () => source!.ToCodexConfig();

        act.Should().Throw<ArgumentNullException>();
    }
}
