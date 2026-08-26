using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmWorkflow.Tools;
using LmStreaming.Sample.Services;
using Microsoft.Extensions.Time.Testing;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Pins what the Modes editor is offered, which is the half of the reported defect that made the
/// other half invisible: sandbox, sub-agent and workflow tools were never in <c>/api/tools</c> at
/// all, so there was nothing to tick even once gating stopped keying on the mode id.
/// </summary>
public class ToolCatalogTests
{
    private static readonly SandboxToolCatalog LiveSandbox = new(
        [("Bash", "Run a shell command."), ("Read", "Read a file."), ("PluginTool", "From a plugin.")],
        IsLive: true,
        Warning: null
    );

    private static ToolCatalog Create(SandboxToolCatalog sandbox, params string[] builtInNames)
    {
        var builtIns = builtInNames
            .Select(n => new ToolDefinition { Name = n, Description = n })
            .ToList();

        return new ToolCatalog(
            new FunctionRegistry(),
            builtIns,
            new StubProbe(sandbox),
            new FakeTimeProvider()
        );
    }

    [Fact]
    public async Task Catalog_IncludesTheThreeFamiliesTheEditorUsedToBeBlindTo()
    {
        var catalog = await Create(LiveSandbox).GetAsync();

        catalog.Select(t => t.Group).Should().Contain([ToolGroups.Sandbox, ToolGroups.SubAgents, ToolGroups.Workflow]);
    }

    [Fact]
    public async Task QualifiedGroups_UseGroupPrefixedIdsButKeepBareNames()
    {
        var catalog = await Create(LiveSandbox).GetAsync();

        var bash = catalog.Single(t => t.Id == ToolGroups.Qualify(ToolGroups.Sandbox, "Bash"));

        // The prefix is a selection id. The Name is what the model would see, and must stay bare or
        // the editor would be advertising a tool that does not exist under that name.
        bash.Name.Should().Be("Bash");
        bash.Group.Should().Be(ToolGroups.Sandbox);
        bash.RequiresSandbox.Should().BeTrue();
    }

    [Fact]
    public async Task UnqualifiedGroups_KeepBareIdsSoExistingModesNeedNoMigration()
    {
        var catalog = await Create(LiveSandbox, "web_search").GetAsync();

        var builtIn = catalog.Single(t => t.Group == ToolGroups.BuiltIn);
        builtIn.Id.Should().Be("web_search");
        builtIn.Name.Should().Be("web_search");

        // Every non-qualified row addresses itself by bare name, which is the form already sitting in
        // persisted modes' EnabledTools.
        catalog
            .Where(t => !ToolGroups.IsQualified(t.Group))
            .Should()
            .OnlyContain(t => t.Id == t.Name);
    }

    [Fact]
    public async Task EveryQualifiedGroup_OffersAWildcardRow()
    {
        var catalog = await Create(LiveSandbox).GetAsync();

        foreach (var group in ToolGroups.Qualified)
        {
            var wildcard = catalog.Single(t => t.Id == ToolGroups.Wildcard(group));
            wildcard.IsWildcard.Should().BeTrue();
            wildcard.Group.Should().Be(group);
        }
    }

    [Fact]
    public async Task WildcardRows_AreTheOnlyOnesFlaggedAsSuch()
    {
        var catalog = await Create(LiveSandbox).GetAsync();

        catalog
            .Where(t => t.IsWildcard)
            .Select(t => t.Id)
            .Should()
            .BeEquivalentTo(ToolGroups.Qualified.Select(ToolGroups.Wildcard));
    }

    [Fact]
    public async Task SubAgentGroup_ListsBothSurfaceShapes()
    {
        // A conversation gets the legacy set or the collaboration set, never both; the editor has to
        // offer both, because choosing between them is how a mode picks its surface.
        var catalog = await Create(LiveSandbox).GetAsync();

        var subAgentNames = catalog
            .Where(t => t.Group == ToolGroups.SubAgents && !t.IsWildcard)
            .Select(t => t.Name);

        subAgentNames.Should().BeEquivalentTo(SubAgentToolProvider.AllToolNames);
    }

    [Fact]
    public async Task SubAgentGroup_MarksExactlyTheLegacySurfaceAsTheDefault()
    {
        var catalog = await Create(LiveSandbox).GetAsync();

        var legacyDefaults = catalog
            .Where(t => t.Group == ToolGroups.SubAgents && t.IsLegacyDefault)
            .Select(t => t.Name)
            .ToList();

        legacyDefaults
            .Should()
            .BeEquivalentTo(
                SubAgentToolProvider.AllToolNames.Except(ModeCapabilities.CollaborationToolNames),
                "the editor pre-ticks these for a mode with no capability selection, and ticking a "
                    + "collaboration tool would silently upgrade its surface"
            );
    }

    [Fact]
    public async Task WorkflowGroup_ListsAuthoringAndLaunchFamilies()
    {
        var catalog = await Create(LiveSandbox).GetAsync();

        var workflowNames = catalog
            .Where(t => t.Group == ToolGroups.Workflow && !t.IsWildcard)
            .Select(t => t.Name)
            .ToList();

        workflowNames.Should().Contain(WorkflowToolProvider.AllToolNames);
        workflowNames.Should().Contain(StartWorkflowToolProvider.ToolNames);
    }

    [Fact]
    public async Task TaskTools_AreEnumeratedFromTheRealTaskManager()
    {
        var catalog = await Create(LiveSandbox).GetAsync();

        // Not asserted by name list: the point is that the group is populated from the same object
        // the conversation registry uses, so adding a task tool cannot skip the editor.
        catalog.Where(t => t.Group == ToolGroups.Tasks).Should().NotBeEmpty();
        catalog.Should().Contain(t => t.Group == ToolGroups.Tasks && t.Name == "add-task");
    }

    [Fact]
    public async Task SandboxTools_ComeFromTheLiveGatewayListing()
    {
        var catalog = await Create(LiveSandbox).GetAsync();

        // Including a plugin-provided tool that no static list could know about.
        catalog
            .Should()
            .Contain(t => t.Id == ToolGroups.Qualify(ToolGroups.Sandbox, "PluginTool"));
        catalog.Where(t => t.Group == ToolGroups.Sandbox).Should().OnlyContain(t => t.CatalogWarning == null);
    }

    [Fact]
    public async Task WhenTheGatewayIsDown_TheBaselineIsLabelledRatherThanPassedOffAsComplete()
    {
        var down = new SandboxToolCatalog(
            [.. SandboxToolCatalogProbe.StaticBaseline.Select(n => (n, (string?)null))],
            IsLive: false,
            Warning: "gateway unreachable"
        );

        var catalog = await Create(down).GetAsync();

        var sandboxRows = catalog.Where(t => t.Group == ToolGroups.Sandbox).ToList();
        sandboxRows.Should().NotBeEmpty();
        // Every row carries the warning, including the wildcard: an unlabelled fallback would have
        // the user build an allow-list that silently omits tools their workspace really has.
        sandboxRows.Should().OnlyContain(t => t.CatalogWarning == "gateway unreachable");
        // The wildcard is still offered, which is the escape hatch from an incomplete listing.
        sandboxRows.Should().Contain(t => t.Id == ToolGroups.Wildcard(ToolGroups.Sandbox));
    }

    [Fact]
    public async Task EveryRow_CarriesAGroupLabelForTheEditorsSectionHeadings()
    {
        var catalog = await Create(LiveSandbox, "web_search").GetAsync();

        catalog.Should().OnlyContain(t => !string.IsNullOrWhiteSpace(t.GroupLabel));
        catalog
            .Should()
            .OnlyContain(t => t.GroupLabel == ToolGroups.LabelFor(t.Group));
    }

    [Fact]
    public async Task Ids_AreUnique()
    {
        // Two rows sharing an id would make a mode's stored selection ambiguous.
        var catalog = await Create(LiveSandbox, "web_search").GetAsync();

        catalog.Select(t => t.Id).Should().OnlyHaveUniqueItems();
    }

    private sealed class StubProbe(SandboxToolCatalog result) : ISandboxToolCatalogProbe
    {
        public Task<SandboxToolCatalog> GetAsync(TimeProvider timeProvider, CancellationToken ct = default) =>
            Task.FromResult(result);
    }
}
