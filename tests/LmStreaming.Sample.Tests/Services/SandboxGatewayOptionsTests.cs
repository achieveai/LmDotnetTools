using Microsoft.Extensions.Configuration;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Unit tests for <see cref="SandboxGatewayOptions.ResolveWorkspace()"/> — the pure base/leaf/full-path
/// resolution that drives <c>WORKSPACE_BASE_PATH</c>, the session workspace, and directory creation.
/// Paths are built with <see cref="Path"/> APIs (never hardcoded separators) so the cases hold on any OS.
/// </summary>
public class SandboxGatewayOptionsTests
{
    [Fact]
    public void SessionCatalogTimeout_DefaultsToTheDocumentedBudget()
    {
        // The default is what an operator who configures nothing actually gets, and it is the value the
        // fix depends on: a cold gateway's first catalog read takes many times the best-effort browse
        // budget, so a default that merely matched the browse client would leave the 503 in place.
        new SandboxGatewayOptions()
            .SessionCatalogTimeout.Should()
            .Be(TimeSpan.FromSeconds(120));
        SandboxGatewayOptions.DefaultSessionCatalogTimeoutSeconds.Should().Be(120);
    }

    [Fact]
    public void SessionCatalogTimeout_HonoursAnOperatorValueInsideTheBounds()
    {
        // Non-vacuity guard for every bound below: an in-range value must pass through untouched, or
        // the property would be a constant wearing a setter.
        var options = new SandboxGatewayOptions { SessionCatalogTimeoutSeconds = 45 };

        options.SessionCatalogTimeout.Should().Be(TimeSpan.FromSeconds(45));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void SessionCatalogTimeout_TreatsAbsentOrNonsensicalValuesAsUnconfigured(int configured)
    {
        // Zero is what a missing/blank binding produces, and a negative budget has no meaning at all.
        // Both resolve to the DEFAULT rather than to the minimum: an operator who configured nothing
        // must not silently end up on the tightest budget the type allows.
        var options = new SandboxGatewayOptions { SessionCatalogTimeoutSeconds = configured };

        options.SessionCatalogTimeout.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void SessionCatalogTimeout_RaisesAValueBelowTheFloor()
    {
        // Below the floor the session check gives up faster than the browse client it was split away
        // from, which is strictly worse than not configuring it — the exact outage this option exists
        // to prevent, re-entered through configuration.
        var options = new SandboxGatewayOptions { SessionCatalogTimeoutSeconds = 1 };

        options
            .SessionCatalogTimeout.Should()
            .Be(TimeSpan.FromSeconds(SandboxGatewayOptions.MinSessionCatalogTimeoutSeconds));
        SandboxGatewayOptions.MinSessionCatalogTimeoutSeconds.Should().Be(10);
    }

    [Fact]
    public void SessionCatalogTimeout_CapsAValueAboveTheCeiling()
    {
        // Session creation blocks a request while this runs, so an unbounded budget lets one
        // misconfigured line hang every create against a gateway that is simply dead.
        var options = new SandboxGatewayOptions { SessionCatalogTimeoutSeconds = int.MaxValue };

        options
            .SessionCatalogTimeout.Should()
            .Be(TimeSpan.FromSeconds(SandboxGatewayOptions.MaxSessionCatalogTimeoutSeconds));
        SandboxGatewayOptions.MaxSessionCatalogTimeoutSeconds.Should().Be(300);
    }

    [Fact]
    public void SessionCatalogTimeout_AcceptsTheBoundsThemselves()
    {
        // The bounds are inclusive. Pinned separately because a clamp written with the wrong
        // comparison passes every test above and still rejects the two values it documents.
        new SandboxGatewayOptions
        {
            SessionCatalogTimeoutSeconds = SandboxGatewayOptions.MinSessionCatalogTimeoutSeconds,
        }
            .SessionCatalogTimeout.Should()
            .Be(TimeSpan.FromSeconds(SandboxGatewayOptions.MinSessionCatalogTimeoutSeconds));

        new SandboxGatewayOptions
        {
            SessionCatalogTimeoutSeconds = SandboxGatewayOptions.MaxSessionCatalogTimeoutSeconds,
        }
            .SessionCatalogTimeout.Should()
            .Be(TimeSpan.FromSeconds(SandboxGatewayOptions.MaxSessionCatalogTimeoutSeconds));
    }

    [Fact]
    public void SessionCatalogTimeout_BindsFromTheConfigurationSection()
    {
        // The property is only useful if the NAME an operator writes in appsettings reaches it. Bound
        // through the real binder rather than an object initializer, so a rename that breaks the
        // config key fails here instead of in production.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"{SandboxGatewayOptions.SectionName}:SessionCatalogTimeoutSeconds"] = "90",
                }
            )
            .Build();

        var options = configuration.GetSection(SandboxGatewayOptions.SectionName).Get<SandboxGatewayOptions>();

        options!.SessionCatalogTimeout.Should().Be(TimeSpan.FromSeconds(90));
    }

    [Fact]
    public void ResolveWorkspace_FallsBackToBaseAndLeaf_WhenWorkspacePathUnset()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "ws-base");
        var options = new SandboxGatewayOptions
        {
            WorkspaceBasePath = basePath,
            Workspace = "proj",
            WorkspacePath = null,
        };

        var (resolvedBase, leaf, full) = options.ResolveWorkspace();

        resolvedBase.Should().Be(basePath);
        leaf.Should().Be("proj");
        full.Should().Be(Path.Combine(basePath, "proj"));
    }

    [Fact]
    public void ResolveWorkspace_ReturnsAllNull_WhenNothingConfigured()
    {
        var options = new SandboxGatewayOptions();

        var (resolvedBase, leaf, full) = options.ResolveWorkspace();

        resolvedBase.Should().BeNull();
        leaf.Should().BeNull();
        full.Should().BeNull();
    }

    [Fact]
    public void ResolveWorkspace_FullPathIsNull_WhenBaseSetButLeafMissing()
    {
        // Base alone can't form a mountable workspace path — FullPath must be null so callers skip
        // directory creation rather than calling Path.Combine with a null leaf.
        var options = new SandboxGatewayOptions { WorkspaceBasePath = Path.GetTempPath(), Workspace = null };

        var (resolvedBase, leaf, full) = options.ResolveWorkspace();

        resolvedBase.Should().Be(Path.GetTempPath());
        leaf.Should().BeNull();
        full.Should().BeNull();
    }

    [Fact]
    public void ResolveWorkspace_SplitsAbsoluteWorkspacePath_IntoParentAndFolderName()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "a", "b", "myrepo");
        var options = new SandboxGatewayOptions { WorkspacePath = absolute };

        var (resolvedBase, leaf, full) = options.ResolveWorkspace();

        var expectedFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(absolute));
        full.Should().Be(expectedFull);
        leaf.Should().Be("myrepo");
        resolvedBase.Should().Be(Path.GetDirectoryName(expectedFull));
    }

    [Fact]
    public void ResolveWorkspace_IgnoresTrailingSeparator_OnWorkspacePath()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "a", "b", "myrepo");
        var options = new SandboxGatewayOptions { WorkspacePath = absolute + Path.DirectorySeparatorChar };

        var (resolvedBase, leaf, full) = options.ResolveWorkspace();

        // A trailing separator must not turn the leaf into an empty string.
        leaf.Should().Be("myrepo");
        full.Should().Be(Path.TrimEndingDirectorySeparator(Path.GetFullPath(absolute)));
        resolvedBase.Should().Be(Path.GetDirectoryName(full!));
    }

    [Fact]
    public void ResolveWorkspace_MakesRelativeWorkspacePathAbsolute_AgainstCurrentDirectory()
    {
        // Documents the (intentional) behavior that a relative WorkspacePath is resolved against the
        // process working directory rather than passed through raw.
        var relative = Path.Combine("sub", "leaf");
        var options = new SandboxGatewayOptions { WorkspacePath = relative };

        var (_, leaf, full) = options.ResolveWorkspace();

        leaf.Should().Be("leaf");
        full.Should().Be(Path.TrimEndingDirectorySeparator(Path.GetFullPath(relative)));
        Path.IsPathFullyQualified(full!).Should().BeTrue("a relative WorkspacePath is normalized to an absolute path");
    }

    [Fact]
    public void ResolveWorkspace_PrefersWorkspacePath_OverBaseAndLeaf()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "override-ws");
        var options = new SandboxGatewayOptions
        {
            WorkspaceBasePath = Path.Combine(Path.GetTempPath(), "ignored-base"),
            Workspace = "ignored-leaf",
            WorkspacePath = absolute,
        };

        var (resolvedBase, leaf, full) = options.ResolveWorkspace();

        var expectedFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(absolute));
        full.Should().Be(expectedFull);
        leaf.Should().Be("override-ws");
        resolvedBase.Should().Be(Path.GetDirectoryName(expectedFull));
        resolvedBase.Should().NotBe(options.WorkspaceBasePath, "WorkspacePath takes precedence over the legacy base");
    }

    [Fact]
    public void ResolveWorkspace_WithOverride_TreatsOverrideAsLeafUnderBase()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "ws-base");
        var options = new SandboxGatewayOptions { WorkspaceBasePath = basePath, Workspace = "proj" };

        var (resolvedBase, leaf, full) = options.ResolveWorkspace("projA");

        resolvedBase.Should().Be(basePath);
        leaf.Should().Be("projA");
        full.Should().Be(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(basePath, "projA"))));
    }

    [Fact]
    public void ResolveWorkspace_WithNullOrEmptyOverride_MatchesParameterless()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "ws-base");
        var options = new SandboxGatewayOptions { WorkspaceBasePath = basePath, Workspace = "proj" };

        options.ResolveWorkspace(null).Should().Be(options.ResolveWorkspace());
        options.ResolveWorkspace("   ").Should().Be(options.ResolveWorkspace());
    }

    [Fact]
    public void ResolveWorkspace_WithOverride_ReturnsLeafWithoutBaseOrFullPath_WhenNoBaseConfigured()
    {
        // A remote gateway (or one that roots the workspace under its own per-app base, ADR 0029) has
        // no local filesystem the client can resolve against or pre-create in. WorkspaceBasePath is
        // therefore OPTIONAL: with no base configured, resolving an override must NOT throw — it yields
        // just the leaf (the workspace identifier the gateway needs), with a null base and null full
        // path so the registry skips local directory pre-creation and lets the gateway own creation.
        var options = new SandboxGatewayOptions(); // no WorkspaceBasePath, no WorkspacePath

        var (resolvedBase, leaf, full) = options.ResolveWorkspace("projA");

        resolvedBase.Should().BeNull();
        leaf.Should().Be("projA");
        full.Should().BeNull();
    }

    [Fact]
    public void ResolveWorkspace_WithRootedOverride_Throws_EvenWithNoBaseConfigured()
    {
        // The "override must be relative" invariant is independent of whether a base is configured —
        // a rooted value is never a valid workspace leaf.
        var options = new SandboxGatewayOptions();

        var rooted = Path.Combine(Path.GetTempPath(), "elsewhere");
        var act = () => options.ResolveWorkspace(rooted);

        // Pin WHICH guard fires: the rooted check must run ahead of the no-base return (pre-PR the
        // no-base guard threw first with a different message), so this stays sensitive to the reorder.
        act.Should().Throw<InvalidOperationException>().WithMessage("*must be relative to the workspace base*");
    }

    [Fact]
    public void ResolveWorkspace_WithTraversalOverride_Throws_EvenWithNoBaseConfigured()
    {
        // Defense-in-depth (PR #165 review): a '..' traversal segment is never a valid workspace
        // identifier, so it is rejected even with no base configured — the no-base path forwards the
        // leaf straight to the gateway, so this is the client's only containment guard there.
        var options = new SandboxGatewayOptions();

        var act = () => options.ResolveWorkspace(Path.Combine("..", "evil"));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ResolveWorkspace_WithEscapingOverride_Throws()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "ws-base");
        var options = new SandboxGatewayOptions { WorkspaceBasePath = basePath, Workspace = "proj" };

        var act = () => options.ResolveWorkspace(Path.Combine("..", "evil"));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ResolveWorkspace_WithRootedOverride_Throws()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "ws-base");
        var options = new SandboxGatewayOptions { WorkspaceBasePath = basePath, Workspace = "proj" };

        // An absolute path under the temp root is rooted, so it must be rejected regardless of where
        // it points.
        var rooted = Path.Combine(Path.GetTempPath(), "elsewhere");
        var act = () => options.ResolveWorkspace(rooted);

        act.Should().Throw<InvalidOperationException>();
    }
}
