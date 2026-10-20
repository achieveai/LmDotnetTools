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

    [Fact]
    public void SpawnedGatewayGetsARelativeMarketplaceStoreResolvedAgainstTheAppNotItsOwnFolder()
    {
        // The gateway runs with its exe folder as cwd, so a relative store path must be made absolute here.
        var start = StartInfo(new SandboxGatewayOptions { MarketplaceStorePath = "marketplace-store" });

        start
            .Environment["MARKETPLACE_STORE_PATH"]
            .Should()
            .Be(Path.Combine(AppContext.BaseDirectory, "marketplace-store"));
    }

    [Fact]
    public void SpawnedGatewayKeepsAnAbsoluteMarketplaceStore()
    {
        // Rooted on every OS, so the test means the same thing off Windows.
        var store = Path.Combine(Path.GetTempPath(), "stores", "mkt");
        var start = StartInfo(new SandboxGatewayOptions { MarketplaceStorePath = store });

        start.Environment["MARKETPLACE_STORE_PATH"].Should().Be(store);
    }

    [Fact]
    public void SpawnedGatewayGetsNoMarketplaceStoreOrPythonWhenUnset()
    {
        var start = StartInfo(new SandboxGatewayOptions());

        start.Environment.Should().NotContainKey("MARKETPLACE_STORE_PATH");
        start.Environment.Should().NotContainKey("LOCAL_SANDBOX_PYTHON");
    }

    [Fact]
    public void SpawnedGatewayGetsTheLocalSandboxPythonVerbatim()
    {
        // Not resolved: the gateway refuses a relative interpreter, and resolving it here would hide that.
        var start = StartInfo(new SandboxGatewayOptions { LocalSandboxPython = @"python\python.exe" });

        start.Environment["LOCAL_SANDBOX_PYTHON"].Should().Be(@"python\python.exe");
    }

    private static System.Diagnostics.ProcessStartInfo StartInfo(SandboxGatewayOptions options) =>
        new SandboxGatewayLifetime(
            options,
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient()
        ).BuildStartInfo(@"C:\gateway\mcp-gateway.exe", @"C:\gatewaygent-cli.exe");
}
