using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.DependencyInjection;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// Issue #218 item 10 — the daemon must resolve the sandbox gateway's base URL ONCE and hand that single
/// value to every gateway consumer. The boot <see cref="SandboxSessionAdapter"/> used to read
/// <c>CRD_SANDBOX_GATEWAY</c> on its own with its own <c>:8080</c> default, while the rest of the graph
/// resolved env → <c>SandboxGateway:BaseUrl</c> → <c>:3000</c>. A profile that set only
/// <c>SandboxGateway:BaseUrl</c> was therefore silently ignored by the adapter, which then talked to a
/// gateway that was not the one the operator configured (or to nothing at all).
/// </summary>
public sealed class DaemonGatewayUrlWiringTests
{
    private const string ProfileGatewayUrl = "http://127.0.0.1:4321";

    /// <summary>
    /// Guards the assertions below from going vacuous: <c>CRD_SANDBOX_GATEWAY</c> wins over the profile in
    /// the production resolution order, so a developer machine with it exported would prove nothing about
    /// the profile path. Fail loudly rather than skip — a silently-skipped test reads exactly like a
    /// passing one.
    /// </summary>
    private static void RequireNoGatewayEnvOverride()
    {
        var env = Environment.GetEnvironmentVariable("CRD_SANDBOX_GATEWAY");
        if (!string.IsNullOrWhiteSpace(env))
        {
            Assert.Fail(
                $"CRD_SANDBOX_GATEWAY is set to '{env}' in this process's environment. It overrides the "
                    + "profile value these tests configure, so they cannot observe the profile path. Unset it "
                    + "and re-run.");
        }
    }

    [Fact]
    public void The_boot_sandbox_adapter_uses_the_gateway_url_the_profile_configures()
    {
        RequireNoGatewayEnvOverride();

        using var factory = new DaemonWebAppFactory();
        using var configured = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("SandboxGateway:BaseUrl", ProfileGatewayUrl));

        var adapter = configured.Services.GetRequiredService<SandboxSessionAdapter>();

        adapter.GatewayBaseUrl.Should().Be(
            ProfileGatewayUrl,
            "a profile-only gateway URL must reach the boot adapter, not just the rest of the graph");
    }

    [Fact]
    public void The_boot_sandbox_adapter_and_the_review_provisioner_agree_on_one_gateway_url()
    {
        RequireNoGatewayEnvOverride();

        using var factory = new DaemonWebAppFactory();
        using var configured = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("SandboxGateway:BaseUrl", ProfileGatewayUrl));

        var adapter = configured.Services.GetRequiredService<SandboxSessionAdapter>();
        var provisioner = configured.Services.GetRequiredService<IReviewSessionProvisioner>();

        // The provisioner is constructed from the single resolved `gatewayBaseUrl`; the adapter must share
        // it. Comparing the two consumers (rather than either against a literal) is what pins "one source of
        // truth" regardless of which source the resolution order happened to pick.
        adapter.GatewayBaseUrl.Should().Be(
            ((ReviewSessionProvisioner)provisioner).GatewayBaseUrl,
            "every gateway consumer must be handed the same resolved gateway URL");
    }
}
