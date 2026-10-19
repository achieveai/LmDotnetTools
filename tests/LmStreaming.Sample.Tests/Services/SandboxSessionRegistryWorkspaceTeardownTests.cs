using AchieveAi.LmDotnetTools.Sandbox;
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

    [Fact]
    public async Task ReleaseWorkspaceSessionsAsync_DeleteFails_KeepsTheSessionTracked_AndARetryStopsIt()
    {
        await using var h = Harness.Create();
        var workspace = await SeedWorkspaceAsync(h, ["official"]);
        var session = await SeedSessionAsync(h, workspace, "app-a");
        h.Gateway.FailNextDeletes(1);

        var first = () => h.Registry.ReleaseWorkspaceSessionsAsync(workspace.Id, "app-a");

        var thrown = (await first.Should().ThrowAsync<SandboxException>()).Which;
        thrown.StatusCode.Should().Be(503);
        h.Gateway.DeletedSessionIds.Should().BeEmpty();
        h.Registry.TryGetSessionById(session.SessionId, out _)
            .Should()
            .BeTrue("a sandbox that is still running must stay tracked, or nothing can stop it later");

        await h.Registry.ReleaseWorkspaceSessionsAsync(workspace.Id, "app-a");

        h.Gateway.DeletedSessionIds.Should().Equal(session.SessionId);
        h.Registry.TryGetSessionById(session.SessionId, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Teardown_RightAfterAPluginMigration_StopsTheReplacementSandbox()
    {
        // The migration republishes the replacement into the cache; nothing reads that slot before the teardown.
        await using var h = Harness.Create();
        var workspace = await SeedWorkspaceAsync(h, ["official"]);
        var original = await SeedSessionAsync(h, workspace, "app-a");
        _ = await h.Service.ApplyPluginSelectionUpdateAsync(
            workspace.Id,
            Update(["official"], [SelectedPlugin], revision: 0)
        );
        var replacement = h.Gateway.CreatedSessionIds.Single(id => id != original.SessionId);

        await h.Registry.ReleaseWorkspaceSessionsAsync(workspace.Id, "app-a");

        h.Gateway.DeletedSessionIds.Should()
            .Equal(
                [original.SessionId, replacement],
                "the migration retired the original; the release stops its replacement"
            );
        h.Registry.TryGetSessionById(replacement, out _).Should().BeFalse();
    }

    [Fact]
    public async Task DestroyWorkspaceSessionAsync_ForOneApp_RightAfterAPluginMigration_StopsOnlyThatAppsReplacement()
    {
        await using var h = Harness.Create();
        var workspace = await SeedWorkspaceAsync(h, ["official"]);
        var originals = new[]
        {
            (await SeedSessionAsync(h, workspace, "app-a")).SessionId,
            (await SeedSessionAsync(h, workspace, "app-b")).SessionId,
        };
        _ = await h.Service.ApplyPluginSelectionUpdateAsync(
            workspace.Id,
            Update(["official"], [SelectedPlugin], revision: 0)
        );
        var replacements = h.Gateway.CreatedSessionIds.Except(originals).ToArray();

        await h.Registry.DestroyWorkspaceSessionAsync(workspace.Id, "app-a");

        var stopped = h.Gateway.DeletedSessionIds.Except(originals).ToArray();
        stopped.Should().ContainSingle("app-a's replacement is stopped").Which.Should().BeOneOf(replacements);
        h.Registry.TryGetSessionById(replacements.Single(id => id != stopped[0]), out _)
            .Should()
            .BeTrue("app-b's replacement keeps running");
    }
}
