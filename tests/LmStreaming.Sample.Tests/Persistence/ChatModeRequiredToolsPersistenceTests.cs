namespace LmStreaming.Sample.Tests.Persistence;

/// <summary>
/// Persistence and CRUD-boundary tests for the mode-level <c>SubAgentRequiredTools</c> property
/// (#623): legacy chat-modes.json files load unchanged (frozen literal fixture, not a round-trip
/// self-check), the store round-trips the list through create/update/copy, an update without the
/// field clears it back to "not enforced", and Prompts.yaml binding carries it.
/// </summary>
public sealed class ChatModeRequiredToolsPersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "lmstreaming-chatmodes-tests",
        Guid.NewGuid().ToString("N")
    );

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// A chat-modes.json exactly as the store wrote it BEFORE #623 — a frozen literal, so this
    /// keeps failing if a rename/requirement on the new field ever breaks old files.
    /// </summary>
    private const string LegacyChatModesJson = """
        [
          {
            "id": "legacy-1",
            "name": "Legacy Mode",
            "description": "Written before subAgentRequiredTools existed.",
            "systemPrompt": "You are a legacy mode.",
            "enabledTools": ["add-task"],
            "isSystemDefined": false,
            "createdAt": 1700000000000,
            "updatedAt": 1700000000001
          }
        ]
        """;

    private FileChatModeStore CreateStoreWithFile(string? json)
    {
        _ = Directory.CreateDirectory(_dir);
        if (json is not null)
        {
            File.WriteAllText(Path.Combine(_dir, "chat-modes.json"), json);
        }

        return new FileChatModeStore(_dir);
    }

    [Fact]
    public async Task LegacyChatModesJson_LoadsUnchanged_WithNullRequiredTools()
    {
        var store = CreateStoreWithFile(LegacyChatModesJson);

        var mode = await store.GetModeAsync("legacy-1");

        mode.Should().NotBeNull();
        mode!.Name.Should().Be("Legacy Mode");
        mode.SubAgentRequiredTools.Should().BeNull("absent in old files must stay absent, the 'not enforced' shape");
    }

    [Fact]
    public async Task CreateUpdateCopy_RoundTripTheRequiredToolsList()
    {
        var store = CreateStoreWithFile(null);

        var created = await store.CreateModeAsync(
            new ChatModeCreateUpdate
            {
                Name = "Board Mode",
                SystemPrompt = "primary",
                SubAgentRequiredTools = ["tasks:*", "SendMessage"],
            }
        );

        created.SubAgentRequiredTools.Should().Equal("tasks:*", "SendMessage");

        var reloaded = await store.GetModeAsync(created.Id);
        reloaded!.SubAgentRequiredTools.Should().Equal("tasks:*", "SendMessage");

        var updated = await store.UpdateModeAsync(
            created.Id,
            new ChatModeCreateUpdate
            {
                Name = "Board Mode",
                SystemPrompt = "primary",
                SubAgentRequiredTools = ["claim-task"],
            }
        );
        updated.SubAgentRequiredTools.Should().Equal("claim-task");

        // A copy of a board-centric mode keeps its guarantee. (No because-string on Equal here:
        // the params overload would treat it as another expected element.)
        var copy = await store.CopyModeAsync(created.Id, "Copied Board Mode");
        copy.SubAgentRequiredTools.Should().Equal("claim-task");
    }

    [Fact]
    public async Task Update_WithoutTheField_ClearsItBackToNotEnforced()
    {
        var store = CreateStoreWithFile(null);
        var created = await store.CreateModeAsync(
            new ChatModeCreateUpdate
            {
                Name = "Clearable",
                SystemPrompt = "primary",
                SubAgentRequiredTools = ["tasks:*"],
            }
        );

        var updated = await store.UpdateModeAsync(
            created.Id,
            new ChatModeCreateUpdate { Name = "Clearable", SystemPrompt = "primary" }
        );

        updated.SubAgentRequiredTools.Should().BeNull();
    }

    [Fact]
    public void ParseModes_BindsSubAgentRequiredToolsFromYaml()
    {
        const string Yaml = """
            chatModes:
              - id: board
                name: Board Mode
                systemPrompt: You run the board.
                subAgentRequiredTools:
                  - "tasks:*"
                  - claim-task
              - id: plain
                name: Plain Mode
                systemPrompt: No enforcement here.
            """;

        var modes = SystemChatModes.ParseModes(Yaml);

        modes[0].SubAgentRequiredTools.Should().Equal("tasks:*", "claim-task");
        modes[1].SubAgentRequiredTools.Should().BeNull("a yaml mode without the key binds to null, not empty");
    }
}
