namespace LmStreaming.Sample.Tests.Models;

public class WorkspaceTests
{
    [Fact]
    public void Workspace_DefaultsPluginSelectionToNull_AndRevisionToZero()
    {
        var workspace = new Workspace
        {
            Id = "id",
            Name = "name",
            DirectoryRelPath = "dir",
            IsSystemDefined = false,
            CreatedAt = 0,
            UpdatedAt = 0,
        };

        workspace.PluginSelection.Should().BeNull();
        workspace.PluginsRevision.Should().Be(0);
    }

    [Fact]
    public void WorkspaceUpdate_DefaultsPluginSelectionToUnset()
    {
        var update = new WorkspaceUpdate();

        update.PluginSelection.IsSet.Should().BeFalse();
    }

    [Fact]
    public void WorkspaceUpdate_Deserialize_OmittedPluginSelection_StaysUnset()
    {
        var update = JsonSerializer.Deserialize<WorkspaceUpdate>(
            """{"marketplaces":["a"]}""",
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
        );

        update!.PluginSelection.IsSet.Should().BeFalse();
    }

    [Fact]
    public void WorkspaceUpdate_Deserialize_ExplicitNullPluginSelection_IsSetToNull()
    {
        var update = JsonSerializer.Deserialize<WorkspaceUpdate>(
            """{"marketplaces":["a"],"pluginSelection":null}""",
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
        );

        update!.PluginSelection.IsSet.Should().BeTrue();
        update.PluginSelection.Value.Should().BeNull();
    }

    [Fact]
    public void WorkspaceUpdate_Deserialize_ExplicitPluginList_IsSetToList()
    {
        var update = JsonSerializer.Deserialize<WorkspaceUpdate>(
            """{"marketplaces":["a"],"pluginSelection":[{"marketplace":"official","plugin":"code-review"}]}""",
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
        );

        update!.PluginSelection.IsSet.Should().BeTrue();
        update.PluginSelection.Value.Should().ContainSingle(p => p.Plugin == "code-review");
    }
}
