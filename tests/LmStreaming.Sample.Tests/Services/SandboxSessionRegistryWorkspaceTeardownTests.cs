using static LmStreaming.Sample.Tests.Services.WorkspacePluginSelectionServiceTests;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Workspace teardown as the inspector's backup restore uses it: a create still in flight is awaited and
/// destroyed rather than skipped into an untracked sandbox, and the app-scoped overload leaves other apps'
/// sandboxes on the same workspace running.
/// </summary>
public class SandboxSessionRegistryWorkspaceTeardownTests
{
    [Fact]
    public async Task DestroyWorkspaceSessionAsync_CreateInFlight_DestroysItOnceItCompletes()
    {
        await using var h = Harness.Create();
        var workspace = await SeedWorkspaceAsync(h, ["official"]);
        h.Gateway.HoldCreatesFor("app-a");
        var inFlight = SeedSessionAsync(h, workspace, "app-a");
        await h.Gateway.WaitForHeldCreatesAsync(1);

        var teardown = h.Registry.DestroyWorkspaceSessionAsync(workspace.Id);
        h.Gateway.ReleaseCreatesFor("app-a");
        await teardown;
        var session = await inFlight;

        h.Gateway.DeletedSessionIds.Should()
            .Contain(session.SessionId, "a skipped in-flight create would finish into a removed slot, untracked");
        h.Registry.TryGetSessionById(session.SessionId, out _).Should().BeFalse();
    }

    [Fact]
    public async Task DestroyWorkspaceSessionAsync_ForOneApp_LeavesOtherAppsSessionsRunning()
    {
        await using var h = Harness.Create();
        var workspace = await SeedWorkspaceAsync(h, ["official"]);
        var mine = await SeedSessionAsync(h, workspace, "app-a");
        var theirs = await SeedSessionAsync(h, workspace, "app-b");

        await h.Registry.DestroyWorkspaceSessionAsync(workspace.Id, "app-a");

        h.Gateway.DeletedSessionIds.Should().Equal(mine.SessionId);
        h.Registry.TryGetSessionById(theirs.SessionId, out _).Should().BeTrue();
    }
}
