using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// The reported defect, pinned end to end: a clone of Workspace Agent had none of its sandbox,
/// sub-agent or workflow tools.
/// </summary>
/// <remarks>
/// The cause was that every one of those families was granted by <c>mode.Id == "workspace-agent"</c>,
/// so a copy — which necessarily has a fresh <see cref="Guid"/> id — matched none of them. These
/// tests go through the real <see cref="FileChatModeStore"/> rather than constructing a
/// <see cref="ChatMode"/> by hand, because the copy path is where the selection could be dropped,
/// and a hand-built mode would prove nothing about it.
/// </remarks>
public class ModeCapabilitiesCloneTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "lmstreaming-mode-clone-" + Guid.NewGuid().ToString("N")
    );

    [Fact]
    public async Task CopyOfWorkspaceAgent_ResolvesToTheSameCapabilities()
    {
        var store = new FileChatModeStore(_dir);
        var original = SystemChatModes.GetById(SystemChatModes.WorkspaceAgentModeId);
        original.Should().NotBeNull();

        var copy = await store.CopyModeAsync(SystemChatModes.WorkspaceAgentModeId, "My Workspace");

        copy.Id.Should().NotBe(SystemChatModes.WorkspaceAgentModeId);
        copy.IsSystemDefined.Should().BeFalse();
        ModeCapabilities
            .Resolve(copy)
            .Should()
            .Be(
                ModeCapabilities.Resolve(original!),
                "a copy differs from its source only by id and name, and capability must not be a "
                    + "function of either"
            );
    }

    [Fact]
    public async Task CopyOfWorkspaceAgent_KeepsSandboxSubAgentsAndWorkflow()
    {
        // Spelled out rather than left to the equality above: if BOTH modes resolved to
        // LegacyDefaults the comparison test would still pass while the clone had nothing.
        var store = new FileChatModeStore(_dir);

        var copy = await store.CopyModeAsync(SystemChatModes.WorkspaceAgentModeId, "My Workspace");
        var caps = ModeCapabilities.Resolve(copy);

        caps.NeedsSandbox.Should().BeTrue();
        caps.SandboxToolAllowList.Should().BeNull("Workspace Agent takes the whole gateway surface");
        caps.SubAgents.Should().BeTrue();
        caps.Collaboration.Should().BeTrue();
        caps.StartWorkflowTools.Should().BeTrue();
    }

    [Fact]
    public async Task CopyOfWorkflowAuthor_KeepsItsNarrowReadOnlySandboxSlice()
    {
        var store = new FileChatModeStore(_dir);

        var copy = await store.CopyModeAsync(SystemChatModes.WorkflowAuthorModeId, "My Author");
        var caps = ModeCapabilities.Resolve(copy);

        caps.NeedsSandbox.Should().BeTrue();
        caps.SandboxToolAllowList.Should().BeEquivalentTo(["Read", "Grep", "Skill"]);
        caps.WorkflowAuthoringTools.Should().BeTrue();
        // The legacy sub-agent surface, not the collaboration one — this mode never had it.
        caps.SubAgents.Should().BeTrue();
        caps.Collaboration.Should().BeFalse();
    }

    [Fact]
    public async Task EditingACopy_PreservesItsCapabilitySelection()
    {
        // The editor round-trip is the other half of the bug: ChatModeCreateUpdate used to carry
        // neither field, so the first save of a copy dropped what CopyModeAsync had preserved.
        var store = new FileChatModeStore(_dir);
        var copy = await store.CopyModeAsync(SystemChatModes.WorkspaceAgentModeId, "My Workspace");

        var saved = await store.UpdateModeAsync(
            copy.Id,
            new ChatModeCreateUpdate
            {
                Name = "My Workspace",
                SystemPrompt = copy.SystemPrompt,
                EnabledTools = copy.EnabledTools,
                EnabledBuiltInTools = copy.EnabledBuiltInTools,
                EnabledCapabilityTools = copy.EnabledCapabilityTools,
            }
        );

        saved.EnabledBuiltInTools.Should().BeEquivalentTo(copy.EnabledBuiltInTools);
        ModeCapabilities.Resolve(saved).Should().Be(ModeCapabilities.Resolve(copy));
    }

    [Fact]
    public async Task NarrowingACopysSandboxSelection_NarrowsItsCapabilities()
    {
        // Deriving from the selection has to cut both ways, or "derive" would just mean "always
        // grant everything the original had".
        var store = new FileChatModeStore(_dir);
        var copy = await store.CopyModeAsync(SystemChatModes.WorkspaceAgentModeId, "Read Only");

        var saved = await store.UpdateModeAsync(
            copy.Id,
            new ChatModeCreateUpdate
            {
                Name = "Read Only",
                SystemPrompt = copy.SystemPrompt,
                EnabledCapabilityTools = [ToolGroups.Qualify(ToolGroups.Sandbox, "Read")],
            }
        );

        var caps = ModeCapabilities.Resolve(saved);
        caps.NeedsSandbox.Should().BeTrue();
        caps.SandboxToolAllowList.Should().BeEquivalentTo(["Read"]);
        caps.SubAgents.Should().BeFalse();
        caps.StartWorkflowTools.Should().BeFalse();
        caps.Collaboration.Should().BeFalse();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }
}
