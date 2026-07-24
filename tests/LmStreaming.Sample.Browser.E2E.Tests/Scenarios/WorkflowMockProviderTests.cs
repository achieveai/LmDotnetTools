using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// Deterministic proof that a StartWorkflowAgent workflow runs end to end on a MOCK provider — the path
/// unlocked by (a) the gate relaxation that wires the workflow tool family for the scripted <c>test</c>/
/// <c>test-anthropic</c> providers without a sandbox, and (b) the workflow controller running on the same
/// mock wire so it can be scripted. The scripted MAIN conversation calls <c>StartWorkflowAgent</c> with a
/// minimal start→terminal graph; the workflow CONTROLLER loop (a separate mock agent, role-dispatched by
/// its <c>"You are the CONTROLLER of a workflow"</c> system prompt) routes start→terminal in one turn. The
/// run then surfaces as a completed <c>kind:"workflow"</c> tab.
/// </summary>
/// <remarks>
/// Tier-1 (start→terminal, no delegate) is deliberately robust: the ScriptedSseResponder is FIFO per role
/// and falls back to a plain text plan once a role's queue drains, so any extra controller request cleanly
/// ends the run — only the first controller turn (the route) must be exact. Delegate-tab correctness for a
/// procedural workflow (which depends on the exact <c>nodeId:visit:taskId</c> correlation name) is covered
/// at the API level by ConversationsControllerSubAgentsTests, not raced in the browser.
/// </remarks>
[Collection(PlaywrightCollection.Name)]
public sealed class WorkflowMockProviderTests
{
    private readonly PlaywrightFixture _fixture;

    public WorkflowMockProviderTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("test")]
    [InlineData("test-anthropic")]
    public async Task Workflow_runs_on_mock_provider_and_surfaces_a_completed_tab(string providerMode)
    {
        var responder = ScriptedSseResponder
            .New()
            // The isolated controller loop drives the loaded start→terminal graph in one route.
            .ForRole("controller", ctx => ctx.SystemPromptContains("You are the CONTROLLER of a workflow"))
            .Turn(t => t.ToolCall("SetCurrentNode", new { nextNodeId = "t" }))
            .Turn(t => t.Text("Workflow finished."))
            // The main conversation authors + launches the workflow (async).
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            .Turn(t =>
                t.ToolCall(
                    "StartWorkflowAgent",
                    new
                    {
                        workflowId = "wf-e2e",
                        workflow = new
                        {
                            objective = "e2e",
                            steps = new object[]
                            {
                                new { id = "s", kind = "start", next = "t" },
                                new { id = "t", kind = "end" },
                            },
                        },
                        mode = "async",
                    }
                )
            )
            .Turn(t => t.Text("Started the workflow."))
            .Build();

        await using var session = await _fixture.OpenAsync(providerMode, responder.HandlerFor(providerMode));
        var page = session.Page;

        await page.SendMessageAsync("run a minimal workflow");
        await page.WaitForStreamIdleAsync(timeoutMs: 30_000);

        // The main conversation delegated via the StartWorkflowAgent tool.
        await page.ToolCallPills().WaitForCountAtLeastAsync(1, timeoutMs: 20_000);
        (await page.ToolCallNamesAsync()).Should().Contain("StartWorkflowAgent");

        // The run surfaces as a ⚙ workflow tab (its data-tab-id is the workflowId).
        var workflowTab = page.ConversationTab("wf-e2e");
        await workflowTab.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 20_000 }
        );

        // Poll the workflow tab's own title (which carries "· <status>") until it reports completed — the
        // browser-observable terminal proof, no sidebar/threadId lookup required.
        var completed = false;
        for (var attempt = 0; attempt < 40 && !completed; attempt++)
        {
            var title = await workflowTab.GetAttributeAsync("title");
            if (title is not null && title.Contains("completed", StringComparison.OrdinalIgnoreCase))
            {
                completed = true;
                break;
            }

            await page.WaitForTimeoutAsync(500);
        }

        completed.Should().BeTrue("the mock workflow run must reach a completed status");

        await session.SaveSuccessScreenshotAsync($"WorkflowMockProvider.{providerMode}");
    }
}
