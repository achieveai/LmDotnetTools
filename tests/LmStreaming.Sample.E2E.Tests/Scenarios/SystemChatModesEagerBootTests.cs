using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.E2E.Tests.Infrastructure;
using LmStreaming.Sample.Persistence;
using Xunit.Abstractions;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
/// Pins the EAGER CALL SITE for system-mode loading (#628 / PR #630 F-001): Program.cs must touch
/// <see cref="SystemChatModes"/> during host startup so a deployed Prompts.yaml that is broken or
/// missing a required mode kills the boot with the clear validation message — instead of booting a
/// host that looks healthy to the watchdog and 500s every mode-touching request (a failure shape
/// the review daemon retries unbounded, because host 5xx is deliberately outside its retry budget).
/// The validation LOGIC is pinned elsewhere (SystemChatModesTests); this test pins that startup
/// actually runs it: <see cref="SystemChatModes.StartupLoadCompleted"/> is set ONLY by
/// <see cref="SystemChatModes.EnsureLoadedAtStartup"/>, whose only production caller is Program.cs
/// startup. Mutation flip: delete the <c>SystemChatModes.EnsureLoadedAtStartup()</c> line from
/// Program.cs and this test goes red (no request in this test — or any other — would set the flag).
/// </summary>
public sealed class SystemChatModesEagerBootTests : LoggingTestBase
{
    public SystemChatModesEagerBootTests(ITestOutputHelper output)
        : base(output) { }

    [Fact]
    public void Host_startup_eagerly_loads_the_system_modes_before_any_request_is_served()
    {
        LogTestStart();
        var responder = ScriptedSseResponder.New().ForRole("noop", _ => true).Turn(t => t.Text("ok")).Build();
        using var factory = new E2EWebAppFactory("test", new ScriptedBuilder(responder.AsAnthropicHandler()));

        // Boot the host. No HTTP request, no WebSocket — startup alone must have loaded the modes.
        _ = factory.Services;

        SystemChatModes
            .StartupLoadCompleted.Should()
            .BeTrue(
                "Program.cs must call SystemChatModes.EnsureLoadedAtStartup() during startup so a bad "
                    + "Prompts.yaml fails the boot loudly instead of 500ing the first mode-touching request"
            );
        LogTestEnd();
    }
}
