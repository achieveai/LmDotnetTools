using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.E2E.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
/// The in-memory host has no network address, so the sandboxes it creates must not register an event webhook.
/// With one, a live gateway delivers every discovery to the sample's default PublicBaseUrl (127.0.0.1:5000), which
/// is whatever else happens to run on that port.
/// </summary>
public sealed class E2EHostEventWebhookTests : LoggingTestBase
{
    public E2EHostEventWebhookTests(ITestOutputHelper output)
        : base(output) { }

    [Fact]
    public void The_in_memory_host_registers_no_event_webhook()
    {
        LogTestStart();
        using var factory = NewFactory();

        factory.Services.GetRequiredService<SandboxSessionRegistry>().DiscoveryEnabled.Should().BeFalse();
    }

    [Fact]
    public void A_test_can_turn_the_event_webhook_back_on()
    {
        LogTestStart();
        using var factory = NewFactory(
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Auth:Webhook:RegisterEventWebhook"] = "true" }
        );

        factory.Services.GetRequiredService<SandboxSessionRegistry>().DiscoveryEnabled.Should().BeTrue();
    }

    private static E2EWebAppFactory NewFactory(IDictionary<string, string?>? settings = null)
    {
        // Nothing here creates an agent; only the registry's configuration is read.
        var responder = ScriptedSseResponder.New().ForRole("noop", _ => true).Turn(t => t.Text("ok")).Build();

        return new E2EWebAppFactory("test", new ScriptedBuilder(responder.AsAnthropicHandler()), settings);
    }
}
