namespace LmStreaming.Sample.Tests.Persistence;

/// <summary>
/// Persistence and CRUD-boundary tests for the per-mode sub-agent prompt fragment (#610):
/// legacy chat-modes.json files load unchanged (frozen literal fixture, not a round-trip
/// self-check — the #590 lesson), the store round-trips the two new fields, and the controller
/// refuses an invalid placement with 400.
/// </summary>
public sealed class ChatModeSubAgentPromptPersistenceTests : IDisposable
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
    /// A chat-modes.json exactly as the store wrote it BEFORE #610 — a frozen literal, so this
    /// keeps failing if a rename/requirement on the new fields ever breaks old files.
    /// </summary>
    private const string LegacyChatModesJson = """
        [
          {
            "id": "legacy-1",
            "name": "Legacy Mode",
            "description": "Written before subAgentPrompt existed.",
            "systemPrompt": "You are a legacy mode.",
            "enabledTools": ["add-task"],
            "enabledBuiltInTools": ["web_search"],
            "enabledCapabilityTools": ["subagents:Agent"],
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
    public async Task LegacyChatModesJson_LoadsUnchanged_WithNullFragmentFields()
    {
        var store = CreateStoreWithFile(LegacyChatModesJson);

        var mode = await store.GetModeAsync("legacy-1");

        mode.Should().NotBeNull();
        mode!.Name.Should().Be("Legacy Mode");
        mode.SystemPrompt.Should().Be("You are a legacy mode.");
        mode.EnabledTools.Should().BeEquivalentTo(["add-task"]);
        mode.EnabledBuiltInTools.Should().BeEquivalentTo(["web_search"]);
        mode.EnabledCapabilityTools.Should().BeEquivalentTo(["subagents:Agent"]);
        mode.SubAgentPrompt.Should().BeNull();
        mode.SubAgentPromptPlacement.Should().BeNull();
    }

    [Fact]
    public async Task CreateUpdateCopy_RoundTripTheFragmentFields()
    {
        var store = CreateStoreWithFile(null);

        var created = await store.CreateModeAsync(
            new ChatModeCreateUpdate
            {
                Name = "With Fragment",
                SystemPrompt = "primary",
                SubAgentPrompt = "Fragment for children.",
                SubAgentPromptPlacement = "prepend",
            }
        );

        created.SubAgentPrompt.Should().Be("Fragment for children.");
        created.SubAgentPromptPlacement.Should().Be("prepend");

        var reloaded = await store.GetModeAsync(created.Id);
        reloaded!.SubAgentPrompt.Should().Be("Fragment for children.");
        reloaded.SubAgentPromptPlacement.Should().Be("prepend");

        var updated = await store.UpdateModeAsync(
            created.Id,
            new ChatModeCreateUpdate
            {
                Name = "With Fragment",
                SystemPrompt = "primary",
                SubAgentPrompt = "Changed fragment.",
                SubAgentPromptPlacement = "append",
            }
        );
        updated.SubAgentPrompt.Should().Be("Changed fragment.");
        updated.SubAgentPromptPlacement.Should().Be("append");

        var copy = await store.CopyModeAsync(created.Id, "Copied Mode");
        copy.SubAgentPrompt.Should().Be("Changed fragment.");
        copy.SubAgentPromptPlacement.Should().Be("append");
    }

    [Fact]
    public async Task Update_CanClearTheFragment()
    {
        var store = CreateStoreWithFile(null);
        var created = await store.CreateModeAsync(
            new ChatModeCreateUpdate
            {
                Name = "Clearable",
                SystemPrompt = "primary",
                SubAgentPrompt = "temp",
            }
        );

        var updated = await store.UpdateModeAsync(
            created.Id,
            new ChatModeCreateUpdate { Name = "Clearable", SystemPrompt = "primary" }
        );

        updated.SubAgentPrompt.Should().BeNull();
        updated.SubAgentPromptPlacement.Should().BeNull();
    }

    [Theory]
    [InlineData("before")]
    [InlineData("Append")]
    [InlineData("PREPEND")]
    [InlineData("")]
    public async Task Controller_Create_RefusesInvalidPlacementWith400(string placement)
    {
        var controller = new ChatModesController(CreateStoreWithFile(null));

        var result = await controller.Create(
            new ChatModeCreateUpdate
            {
                Name = "Bad",
                SystemPrompt = "p",
                SubAgentPrompt = "frag",
                SubAgentPromptPlacement = placement,
            }
        );

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Controller_Update_RefusesInvalidPlacementWith400()
    {
        var store = CreateStoreWithFile(null);
        var created = await store.CreateModeAsync(new ChatModeCreateUpdate { Name = "Ok", SystemPrompt = "p" });
        var controller = new ChatModesController(store);

        var result = await controller.Update(
            created.Id,
            new ChatModeCreateUpdate
            {
                Name = "Ok",
                SystemPrompt = "p",
                SubAgentPrompt = "frag",
                SubAgentPromptPlacement = "sideways",
            }
        );

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Controller_Create_AcceptsBothValidPlacementsAndAbsent()
    {
        var store = CreateStoreWithFile(null);
        var controller = new ChatModesController(store);

        foreach (var placement in new[] { "prepend", "append", null })
        {
            var result = await controller.Create(
                new ChatModeCreateUpdate
                {
                    Name = $"Mode {placement ?? "default"}",
                    SystemPrompt = "p",
                    SubAgentPrompt = "frag",
                    SubAgentPromptPlacement = placement,
                }
            );

            var created = result.Should().BeOfType<CreatedResult>().Subject.Value.Should().BeOfType<ChatMode>().Subject;
            created.SubAgentPrompt.Should().Be("frag");
            created.SubAgentPromptPlacement.Should().Be(placement);
        }
    }
}
