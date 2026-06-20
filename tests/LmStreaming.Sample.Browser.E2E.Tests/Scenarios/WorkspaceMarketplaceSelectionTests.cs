using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;
using LmStreaming.Sample.Persistence;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// End-to-end proof, through the BROWSER, that the per-workspace marketplace pick is honoured for
/// that conversation: a workspace enabling ONLY <c>ClaudePlugins</c> + <c>superpowers</c>, selected
/// and run under Workspace Agent mode, makes the backend send a sandbox-create request whose
/// <c>marketplaces</c> array is exactly that subset — overriding the global default and never falling
/// back to the gateway's full set.
/// </summary>
/// <remarks>
/// Deterministic and CI-safe: a <see cref="CapturingSandboxGatewayHandler"/> stands in for the
/// sandbox gateway (no live gateway needed), the model is scripted by an instruction chain
/// (<see cref="ScriptedSseResponder"/>), and the gateway's MCP endpoint is intentionally unserved so
/// the app degrades to "no sandbox tools" — leaving the create call as the clean, observable signal.
/// The create request fires as the agent loop is built for the first turn, so a plain text turn is
/// enough to trigger and capture it.
/// </remarks>
[Collection(PlaywrightCollection.Name)]
public sealed class WorkspaceMarketplaceSelectionTests
{
    private readonly PlaywrightFixture _fixture;

    public WorkspaceMarketplaceSelectionTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Workspace_marketplace_pick_drives_the_sandbox_create_for_that_conversation()
    {
        var gateway = new CapturingSandboxGatewayHandler();

        var workspaceBase = Path.Combine(Path.GetTempPath(), "lm-e2e-mkt", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceBase);
        var sandboxOptions = new SandboxGatewayOptions
        {
            // A closed loopback port: the gateway's MCP transport dials BaseUrl directly (not via the
            // capturing handler) and must fail fast so the agent degrades to "no sandbox tools".
            BaseUrl = "http://127.0.0.1:1",
            WorkspaceBasePath = workspaceBase,
            AppId = "lm-e2e",
            AutoSpawn = false,
            // The global default — the per-workspace pick must WIN over this, proving the override path.
            Marketplaces = "global-fallback",
        };

        // Workspace Agent mode runs through a middleware provider; the model only needs to emit text.
        var responder = ScriptedSseResponder
            .New()
            .ForRole("workspace-agent", _ => true)
            .Turn(t => t.Text("Workspace ready."))
            .Build();

        try
        {
            await using var session = await _fixture.OpenAsync(
                "test-anthropic",
                responder.HandlerFor("test-anthropic"),
                sandboxGatewayHandler: gateway,
                sandboxOptions: sandboxOptions);
            var page = session.Page;

            // Create a workspace that enables ONLY a specific subset of marketplaces. Same-origin POST,
            // so no base-url plumbing; the store returns the new workspace id.
            var workspaceId = await page.EvaluateAsync<string>(
                """
                async () => {
                    const res = await fetch('/api/workspaces', {
                        method: 'POST',
                        headers: { 'content-type': 'application/json' },
                        body: JSON.stringify({
                            name: 'Plugin Pick',
                            directoryRelPath: 'plugin-pick',
                            marketplaces: ['ClaudePlugins', 'superpowers']
                        })
                    });
                    if (!res.ok) { throw new Error('create workspace failed: ' + res.status); }
                    const ws = await res.json();
                    return ws.id;
                }
                """);
            workspaceId.Should().NotBeNullOrWhiteSpace();

            // Reload so the selector lists the freshly-created workspace, then pick it.
            await page.ReloadAsync();
            await page.Textarea().WaitForAsync();

            await page.GetByTestId("workspace-selector-button").ClickAsync();
            await page.GetByTestId($"workspace-option-{workspaceId}").ClickAsync();

            // Workspace Agent mode is what provisions the sandbox; select it before the first send
            // (the thread locks its mode + workspace on send).
            await page.ModeSelectorButton().ClickAsync();
            await page.ModeOption(SystemChatModes.WorkspaceAgentModeId).ClickAsync();

            await page.SendMessageAsync("Get the workspace ready.");
            await page.WaitForStreamIdleAsync(timeoutMs: 60_000);

            // Decisive: the create-sandbox request the backend sent for THIS conversation enabled exactly
            // the workspace's picked marketplaces — not the global fallback, not the gateway default set.
            gateway.LastCreateBody.Should().NotBeNull("the Workspace-Agent turn must provision a sandbox");
            gateway.CapturedMarketplaces().Should().Equal("ClaudePlugins", "superpowers");

            await session.SaveSuccessScreenshotAsync("WorkspaceMarketplaceSelection.Pick_drives_sandbox_create");
        }
        finally
        {
            try
            {
                Directory.Delete(workspaceBase, recursive: true);
            }
            catch
            {
                // Best-effort cleanup of the temp workspace base.
            }
        }
    }
}
