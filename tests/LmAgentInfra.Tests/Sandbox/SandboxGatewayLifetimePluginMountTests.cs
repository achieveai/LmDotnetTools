using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Sandbox;

public sealed class SandboxGatewayLifetimePluginMountTests
{
    [Fact]
    public void SpawnedGatewayReceivesConfiguredPluginsBasePath()
    {
        var options = new SandboxGatewayOptions { PluginsBasePath = @"C:\sandbox-plugins" };
        var lifetime = new SandboxGatewayLifetime(
            options,
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient()
        );

        var start = lifetime.BuildStartInfo(@"C:\gateway\mcp-gateway.exe", @"C:\gateway\agent-cli.exe");

        start.Environment["PLUGINS_BASE_PATH"].Should().Be(@"C:\sandbox-plugins");
    }
}
