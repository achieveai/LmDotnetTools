using AchieveAi.LmDotnetTools.LmTestUtils.Persistence;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// The sample's own agent factory, driven through the real pool: a mode or model switch that stays on
/// the API-backed arm must be served ON the live loop, keeping the conversation's stateful tools as the
/// SAME instances.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the composition root.</b> The pool's rule is reference equality of the returned agent, so the
/// pool's own suite can prove the branch exists with a one-line fake factory. What it cannot prove is
/// that the <i>sample's</i> 1,500-line factory takes that branch, or that the conversation-scoped tools
/// survive it — that lives entirely in <c>Program.cs</c>, and an omission there is silent: the switch
/// succeeds either way and the only symptom is a sub-agent that stopped existing.
/// </para>
/// <para>
/// <b>Why the WorkflowManager is the witness.</b> It is the conversation's most clearly stateful keyed
/// resource (it owns the running workflow runs and their controller loops), it is reachable from a test
/// through the public <see cref="WorkflowRunRegistry"/>, and — unlike the sandbox MCP client, the book
/// MCP clients or the hosted search session — it needs neither a live gateway nor a credential. The
/// factory force-enables the workflow tool family for the <c>test</c>/<c>test-anthropic</c> providers, so
/// it is built for every mode on this arm.
/// </para>
/// </remarks>
public sealed class ConversationToolReuseCompositionTests
{
    private static AgentProfile DefaultMode => SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
    private static AgentProfile OtherMode => SystemChatModes.GetById("math-helper")!;

    private sealed class ReuseWebAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _conversationsPath;

        public ReuseWebAppFactory(string conversationsPath)
        {
            _conversationsPath = conversationsPath;

            // Keeps startup provider discovery side-effect-free (no API key, no network) — the same
            // arrangement every other in-process host test here uses.
            Environment.SetEnvironmentVariable("LM_PROVIDER_MODE", "test");
            Environment.SetEnvironmentVariable("CLAUDE_CLI_PATH", null);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Avoids the Vite dev-server auto-spawn.
            builder.UseEnvironment("Production");

            builder.ConfigureTestServices(services =>
            {
                // Opens the claude CLI availability gate so the cross-arm case below can select
                // claude-mock without the CLI being installed on the machine running the suite.
                services.RemoveAll<IFileSystemProbe>();
                services.AddSingleton<IFileSystemProbe>(new FakeFileSystemProbe(executablesOnPath: ["claude"]));

                services.RemoveAll<IConversationStore>();
                services.AddSingleton<IConversationStore>(new FileConversationStore(_conversationsPath));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                Environment.SetEnvironmentVariable("LM_PROVIDER_MODE", null);
            }
        }
    }

    [Fact]
    public async Task AModeSwitchOnTheApiArm_KeepsTheLoopAndItsWorkflowManager()
    {
        await RunAsync(
            async (pool, workflows, threadId) =>
            {
                var before = pool.GetOrCreateAgent(threadId, DefaultMode, "test", requestResponseDumpFileName: null);
                workflows.TryGet(threadId, out var managerBefore).Should().BeTrue("guard: the witness must exist");

                var switched = await pool.RecreateAgentWithModeAsync(threadId, OtherMode);

                switched.Kind.Should().Be(MultiTurnAgentPool.AgentSwitchKind.ReconfiguredInPlace);
                switched.Agent.Should().BeSameAs(before, "the conversation keeps its loop, and with it its sub-agents");
                workflows.TryGet(threadId, out var managerAfter).Should().BeTrue();
                managerAfter.Should().BeSameAs(managerBefore, "a tool present before AND after is the same instance");
            }
        );
    }

    [Fact]
    public async Task AProviderSwitchBetweenTwoApiArmProviders_KeepsTheLoopAndItsWorkflowManager()
    {
        await RunAsync(
            async (pool, workflows, threadId) =>
            {
                var before = pool.GetOrCreateAgent(threadId, DefaultMode, "test", requestResponseDumpFileName: null);
                workflows.TryGet(threadId, out var managerBefore).Should().BeTrue("guard: the witness must exist");

                var switched = await pool.RecreateAgentWithProviderAsync(threadId, "test-anthropic", DefaultMode);

                switched.Kind.Should().Be(MultiTurnAgentPool.AgentSwitchKind.ReconfiguredInPlace);
                switched.Agent.Should().BeSameAs(before);
                workflows.TryGet(threadId, out var managerAfter).Should().BeTrue();
                managerAfter.Should().BeSameAs(managerBefore);
            }
        );
    }

    /// <summary>
    /// A configuration that does not select the workflow launch tools must leave the manager DORMANT —
    /// the same live instance, its runs intact — not dispose it. On the scripted providers the family is
    /// force-enabled for every mode, so the only configuration without it is the deployment kill switch,
    /// flipped between the two switches of one conversation.
    /// </summary>
    [Fact]
    public async Task ASwitchOntoAConfigurationWithoutTheWorkflowTools_KeepsTheWorkflowManagerDormant()
    {
        await RunAsync(
            async (services, pool, workflows, threadId) =>
            {
                var before = pool.GetOrCreateAgent(threadId, DefaultMode, "test", requestResponseDumpFileName: null);
                workflows.TryGet(threadId, out var managerBefore).Should().BeTrue("guard: the witness must exist");

                services.GetRequiredService<IConfiguration>()["WORKSPACE_AGENT_LMWORKFLOW_ENABLED"] = "false";
                var switched = await pool.RecreateAgentWithModeAsync(threadId, OtherMode);

                switched.Kind.Should().Be(MultiTurnAgentPool.AgentSwitchKind.ReconfiguredInPlace);
                switched.Agent.Should().BeSameAs(before);
                workflows.TryGet(threadId, out var managerAfter).Should().BeTrue();
                managerAfter.Should().BeSameAs(managerBefore);
                managerAfter!
                    .IsDisposed.Should()
                    .BeFalse(
                        "a tool the new configuration hides is dormant, not ended: its runs belong to the conversation"
                    );
            }
        );
    }

    /// <summary>
    /// The non-vacuity anchor: crossing onto a CLI-backed arm cannot be served by reassignment, and the
    /// factory's CLI branches return long before the in-place gate, so that switch must still recreate.
    /// </summary>
    [Fact]
    public async Task AProviderSwitchOntoACliArm_StillRecreates()
    {
        await RunAsync(
            async (pool, _, threadId) =>
            {
                var before = pool.GetOrCreateAgent(threadId, DefaultMode, "test", requestResponseDumpFileName: null);

                var switched = await pool.RecreateAgentWithProviderAsync(threadId, "claude-mock", DefaultMode);

                switched.Kind.Should().Be(MultiTurnAgentPool.AgentSwitchKind.Recreated);
                switched.Agent.Should().NotBeSameAs(before);
            }
        );
    }

    /// <summary>
    /// Boots the real host on an isolated conversation store, runs <paramref name="body"/> against its
    /// pool, then tears the store down. The host is disposed inside the block — before the purge —
    /// because driving the pool makes it a live store writer holding exclusive file handles.
    /// </summary>
    private static Task RunAsync(Func<MultiTurnAgentPool, WorkflowRunRegistry, string, Task> body) =>
        RunAsync((_, pool, workflows, threadId) => body(pool, workflows, threadId));

    private static async Task RunAsync(
        Func<IServiceProvider, MultiTurnAgentPool, WorkflowRunRegistry, string, Task> body
    )
    {
        var root = Path.Combine(Path.GetTempPath(), "lmstreaming-tool-reuse-composition", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        await using (var host = new ReuseWebAppFactory(Path.Combine(root, "conversations")))
        {
            await body(
                host.Services,
                host.Services.GetRequiredService<MultiTurnAgentPool>(),
                host.Services.GetRequiredService<WorkflowRunRegistry>(),
                $"tool-reuse-{Guid.NewGuid():N}"
            );
        }

        // #477: detach-then-delete rather than a recursive delete in place. Deliberately NOT in a
        // finally — a throw from Purge would replace the assertion failure unwinding through it.
        DetachedStoreTeardown.Purge(root);
    }
}
