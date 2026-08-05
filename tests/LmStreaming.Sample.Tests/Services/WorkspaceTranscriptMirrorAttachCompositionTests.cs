using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// PR #252 review round 8 (P1): every agent <c>Program.cs</c>'s pool factory builds must be registered
/// with the <see cref="WorkspaceTranscriptMirror"/>, whichever provider branch built it.
/// </summary>
/// <remarks>
/// <para>
/// The factory has seven construction branches and each ends in its own <c>return</c>. The mirror attach
/// used to live in only one of them — the branch that builds the API-backed providers — so the six
/// CLI-backed branches (<c>codex</c>, <c>claude</c>, <c>copilot</c> and their three <c>*-mock</c>
/// siblings) returned before ever reaching it. Those loops all support <c>SubscribeAsync</c>, so nothing
/// about them prevents mirroring; they were simply never registered, and a workspace conversation on any
/// of them produced neither a root transcript nor descendant files.
/// </para>
/// <para>
/// <b>Why this has to be a composition-root test.</b> The bug is not in the mirror and not in any agent
/// loop — both halves are individually correct and individually covered. It is in the wiring between
/// them, and it is an <i>omission</i>: nothing throws, nothing logs, and the only symptom is a file that
/// never appears. A test built against <see cref="WorkspaceTranscriptMirror"/> directly has to call
/// <c>Attach</c> to do anything at all, which is precisely the call under dispute. So the host has to be
/// booted for real and the agent has to be requested through the real pool.
/// </para>
/// <para>
/// <b>Why the CLI mock providers.</b> They are the cheapest branches to reach that were actually broken:
/// availability is gated only on a CLI being on PATH (faked here) plus the in-process mock provider host,
/// which the host itself starts. Their loops are constructed, not started — <c>RunLoopAsync</c> blocks on
/// its input channel before touching a CLI — so no child process is spawned. <c>codex</c>/<c>codex-mock</c>
/// are deliberately excluded because their branch calls <c>EnsureStartedAsync</c> on the codex MCP server,
/// which is real process startup and does not belong in a wiring test.
/// </para>
/// </remarks>
public sealed class WorkspaceTranscriptMirrorAttachCompositionTests
{
    private static readonly AgentProfile Mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;

    /// <summary>
    /// Boots the real <c>Program</c> host with both CLI gates forced open, so the <c>claude-mock</c> and
    /// <c>copilot-mock</c> providers are selectable without either CLI being installed on the machine
    /// running the suite.
    /// </summary>
    private sealed class MirrorAttachWebAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _conversationsPath;

        public MirrorAttachWebAppFactory(string conversationsPath)
        {
            _conversationsPath = conversationsPath;

            // 'test' mode keeps startup provider discovery side-effect-free (no real API key or network
            // needed to boot) — same rationale as the other in-process host tests here.
            Environment.SetEnvironmentVariable("LM_PROVIDER_MODE", "test");

            // ProviderRegistry prefers an explicit CLI path over the PATH probe. Cleared so the faked
            // probe below is what decides availability, rather than whatever the developer's machine has.
            Environment.SetEnvironmentVariable("CLAUDE_CLI_PATH", null);
            Environment.SetEnvironmentVariable("COPILOT_CLI_PATH", null);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Avoids the Vite dev-server auto-spawn (matches every other in-process host test here).
            builder.UseEnvironment("Production");

            builder.ConfigureTestServices(services =>
            {
                // Forces the claude/copilot CLI availability gates open. ProviderRegistry is registered
                // with a factory that resolves IFileSystemProbe, so replacing the probe here is enough —
                // the registry singleton has not been built yet.
                services.RemoveAll<IFileSystemProbe>();
                services.AddSingleton<IFileSystemProbe>(
                    new FakeFileSystemProbe(executablesOnPath: ["claude", "copilot"]));

                // Isolates conversation storage to this test's temp dir.
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

    /// <summary>
    /// <c>claude-mock</c> and <c>copilot-mock</c> are the two CLI-backed branches reachable without
    /// starting a real subprocess or MCP server. Against the pre-fix factory both fail here: the agent is
    /// created successfully and <c>IsMirroring</c> returns false, because their branch returned before the
    /// single <c>transcriptMirror.Attach(agent)</c> call.
    /// </summary>
    [Theory]
    [InlineData("claude-mock")]
    [InlineData("copilot-mock")]
    public async Task Host_AttachesTheMirrorToAgentsBuiltByCliBackedProviderBranches(string providerId)
    {
        var root = Path.Combine(
            Path.GetTempPath(), "lmstreaming-mirror-attach-composition-test", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        try
        {
            await using var host = new MirrorAttachWebAppFactory(Path.Combine(root, "conversations"));

            var registry = host.Services.GetRequiredService<ProviderRegistry>();
            registry.IsAvailable(providerId).Should().BeTrue(
                "the faked CLI probe and the host's own in-process mock provider host must make "
                    + "{0} selectable, otherwise this test would silently stop covering that branch",
                providerId);

            var mirror = host.Services.GetRequiredService<WorkspaceTranscriptMirror>();
            var pool = host.Services.GetRequiredService<MultiTurnAgentPool>();

            var threadId = $"mirror-attach-{providerId}-{Guid.NewGuid():N}";
            var agent = pool.GetOrCreateAgent(threadId, Mode, providerId, requestResponseDumpFileName: null);

            agent.Should().NotBeNull("the provider branch under test must actually build an agent");
            mirror.IsMirroring(threadId).Should().BeTrue(
                "every agent the pool factory returns must be registered with the transcript mirror; "
                    + "{0} builds and returns from its own branch, so an attach that lives inside one "
                    + "other branch never runs for it and the conversation is mirrored nowhere",
                providerId);
        }
        finally
        {
            TryDeleteDir(root);
        }
    }

    private static void TryDeleteDir(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup; a leftover temp dir must not fail the test.
        }
    }
}
