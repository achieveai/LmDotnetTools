namespace LmStreaming.Sample.Tests.Persistence;

/// <summary>
/// Persistence and CRUD-boundary tests for the mode-level <c>SubAgentRequiredTools</c> property
/// (#623): legacy chat-modes.json files load unchanged (frozen literal fixture, not a round-trip
/// self-check), the store round-trips the list through create/update/copy, omitted update fields
/// preserve it, explicit null clears it, and Prompts.yaml binding carries it.
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
    public async Task Update_WithoutTheField_PreservesEnforcement()
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

        updated.SubAgentRequiredTools.Should().Equal("tasks:*");
    }

    [Fact]
    public async Task Update_ExplicitNull_ClearsEnforcement()
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
        const string Json = """
            {
              "name": "Clearable",
              "systemPrompt": "primary",
              "subAgentRequiredTools": null
            }
            """;
        var update = JsonSerializer.Deserialize<ChatModeCreateUpdate>(
            Json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
        )!;

        var updated = await store.UpdateModeAsync(created.Id, update);

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

    [Fact]
    public async Task CreateUpdateCopy_RoundTripTheChildReasoningAndRoutingPolicy()
    {
        var store = CreateStoreWithFile(null);
        var created = await store.CreateModeAsync(
            new ChatModeCreateUpdate
            {
                Name = "Review Mode",
                SystemPrompt = "primary",
                SubAgentReasoningEffort = "xhigh",
                SubAgentModelIntelligenceByType = new Dictionary<string, int>
                {
                    ["code-reviewer:architecture-review"] = 5,
                    ["code-reviewer:duplicate-code-detector"] = 1,
                },
                DefaultSubAgentModelIntelligence = 3,
            }
        );

        created.SubAgentReasoningEffort.Should().Be("xhigh");
        created.SubAgentModelIntelligenceByType.Should().Contain("code-reviewer:architecture-review", 5);
        created.DefaultSubAgentModelIntelligence.Should().Be(3);

        var reloaded = await store.GetModeAsync(created.Id);
        reloaded!.SubAgentReasoningEffort.Should().Be("xhigh");
        reloaded.SubAgentModelIntelligenceByType.Should().Contain("code-reviewer:duplicate-code-detector", 1);
        reloaded.DefaultSubAgentModelIntelligence.Should().Be(3);

        var updated = await store.UpdateModeAsync(
            created.Id,
            new ChatModeCreateUpdate
            {
                Name = "Review Mode",
                SystemPrompt = "primary",
                SubAgentReasoningEffort = "high",
                SubAgentModelIntelligenceByType = new Dictionary<string, int>
                {
                    ["code-reviewer:test-coverage-review"] = 3,
                },
                DefaultSubAgentModelIntelligence = 1,
            }
        );
        var copy = await store.CopyModeAsync(updated.Id, "Copied Review Mode");

        copy.SubAgentReasoningEffort.Should().Be("high");
        copy.SubAgentModelIntelligenceByType.Should().ContainSingle();
        copy.SubAgentModelIntelligenceByType!["code-reviewer:test-coverage-review"].Should().Be(3);
        copy.DefaultSubAgentModelIntelligence.Should().Be(1);
    }

    [Fact]
    public async Task Update_WithoutDescriptionOrEnabledTools_PreservesBoth()
    {
        var store = CreateStoreWithFile(null);
        var created = await store.CreateModeAsync(
            new ChatModeCreateUpdate
            {
                Name = "Restricted Mode",
                Description = "keep me",
                SystemPrompt = "primary",
                EnabledTools = ["safe-tool"],
            }
        );

        var updated = await store.UpdateModeAsync(
            created.Id,
            new ChatModeCreateUpdate
            {
                Name = "Restricted Mode",
                SystemPrompt = "changed",
                SubAgentReasoningEffort = "high",
            }
        );

        updated.Description.Should().Be("keep me");
        updated.EnabledTools.Should().Equal("safe-tool");
    }

    [Fact]
    public async Task Update_ExplicitNull_ClearsDescriptionAndEnablesAllTools()
    {
        var store = CreateStoreWithFile(null);
        var created = await store.CreateModeAsync(
            new ChatModeCreateUpdate
            {
                Name = "Restricted Mode",
                Description = "clear me",
                SystemPrompt = "primary",
                EnabledTools = ["safe-tool"],
            }
        );
        const string Json = """
            {
              "name": "Restricted Mode",
              "description": null,
              "systemPrompt": "primary",
              "enabledTools": null
            }
            """;
        var update = JsonSerializer.Deserialize<ChatModeCreateUpdate>(
            Json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
        )!;

        var updated = await store.UpdateModeAsync(created.Id, update);

        updated.Description.Should().BeNull();
        updated.EnabledTools.Should().BeNull();
    }

    [Fact]
    public async Task Update_WithoutChildPolicyFields_PreservesTheStoredPolicy()
    {
        var store = CreateStoreWithFile(null);
        var created = await store.CreateModeAsync(
            new ChatModeCreateUpdate
            {
                Name = "Review Mode",
                SystemPrompt = "primary",
                SubAgentReasoningEffort = "xhigh",
                SubAgentModelIntelligenceByType = new Dictionary<string, int>
                {
                    ["code-reviewer:architecture-review"] = 5,
                },
                DefaultSubAgentModelIntelligence = 3,
            }
        );

        var updated = await store.UpdateModeAsync(
            created.Id,
            new ChatModeCreateUpdate { Name = "Renamed Review Mode", SystemPrompt = "changed" }
        );

        updated.SubAgentReasoningEffort.Should().Be("xhigh");
        updated.SubAgentModelIntelligenceByType.Should().Contain("code-reviewer:architecture-review", 5);
        updated.DefaultSubAgentModelIntelligence.Should().Be(3);
    }

    [Fact]
    public async Task Update_ExplicitNull_ClearsTheChildPolicy()
    {
        var store = CreateStoreWithFile(null);
        var created = await store.CreateModeAsync(
            new ChatModeCreateUpdate
            {
                Name = "Review Mode",
                SystemPrompt = "primary",
                SubAgentReasoningEffort = "xhigh",
                SubAgentModelIntelligenceByType = new Dictionary<string, int>
                {
                    ["code-reviewer:architecture-review"] = 5,
                },
                DefaultSubAgentModelIntelligence = 3,
            }
        );
        const string Json = """
            {
              "name": "Review Mode",
              "systemPrompt": "primary",
              "subAgentReasoningEffort": null,
              "subAgentModelIntelligenceByType": null,
              "defaultSubAgentModelIntelligence": null
            }
            """;
        var update = JsonSerializer.Deserialize<ChatModeCreateUpdate>(
            Json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
        )!;

        var updated = await store.UpdateModeAsync(created.Id, update);

        updated.SubAgentReasoningEffort.Should().BeNull();
        updated.SubAgentModelIntelligenceByType.Should().BeNull();
        updated.DefaultSubAgentModelIntelligence.Should().BeNull();
    }

    [Theory]
    [InlineData("max")]
    [InlineData("turbo")]
    [InlineData("")]
    [InlineData("2")]
    [InlineData("9")]
    [InlineData("Low,High")]
    public async Task Controller_Create_RefusesUnsupportedChildReasoningEffort(string effort)
    {
        var controller = new ChatModesController(CreateStoreWithFile(null));

        var result = await controller.Create(
            new ChatModeCreateUpdate
            {
                Name = "Bad Review Mode",
                SystemPrompt = "primary",
                SubAgentReasoningEffort = effort,
            }
        );

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Controller_Create_RefusesNegativeOrZeroChildRoutingTier(int tier)
    {
        var controller = new ChatModesController(CreateStoreWithFile(null));

        var result = await controller.Create(
            new ChatModeCreateUpdate
            {
                Name = "Bad Review Mode",
                SystemPrompt = "primary",
                SubAgentModelIntelligenceByType = new Dictionary<string, int>
                {
                    ["code-reviewer:architecture-review"] = tier,
                },
            }
        );

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Controller_Create_RefusesNegativeOrZeroDefaultChildRoutingTier(int tier)
    {
        var controller = new ChatModesController(CreateStoreWithFile(null));

        var result = await controller.Create(
            new ChatModeCreateUpdate
            {
                Name = "Bad Review Mode",
                SystemPrompt = "primary",
                DefaultSubAgentModelIntelligence = tier,
            }
        );

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Controller_Create_RefusesCaseVariantChildRoutingKeys()
    {
        var controller = new ChatModesController(CreateStoreWithFile(null));

        var result = await controller.Create(
            new ChatModeCreateUpdate
            {
                Name = "Ambiguous Review Mode",
                SystemPrompt = "primary",
                SubAgentModelIntelligenceByType = new Dictionary<string, int>
                {
                    ["code-reviewer:architecture-review"] = 5,
                    ["CODE-REVIEWER:ARCHITECTURE-REVIEW"] = 3,
                },
            }
        );

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void ParseModes_RefusesCaseVariantChildRoutingKeys()
    {
        const string Yaml = """
            chatModes:
              - id: bad
                name: Bad Mode
                systemPrompt: Bad.
                subAgentModelIntelligenceByType:
                  code-reviewer:architecture-review: 5
                  CODE-REVIEWER:ARCHITECTURE-REVIEW: 3
            """;

        var act = () => SystemChatModes.ParseModes(Yaml);

        var thrown = act.Should().Throw<InvalidOperationException>();
        thrown.Which.Message.Should().Contain("case-insensitive");
        thrown.Which.Message.Should().ContainEquivalentOf("architecture-review");
    }

    [Fact]
    public void ParseModes_RefusesUnsupportedChildReasoningEffort()
    {
        const string Yaml = """
            chatModes:
              - id: bad
                name: Bad Mode
                systemPrompt: Bad.
                subAgentReasoningEffort: turbo
            """;

        var act = () => SystemChatModes.ParseModes(Yaml);

        act.Should().Throw<InvalidOperationException>().WithMessage("*subAgentReasoningEffort*turbo*");
    }

    [Fact]
    public void ParseModes_BindsChildReasoningAndRoutingPolicyFromYaml()
    {
        const string Yaml = """
            chatModes:
              - id: review
                name: Review Mode
                systemPrompt: Review.
                subAgentReasoningEffort: xhigh
                subAgentModelIntelligenceByType:
                  code-reviewer:architecture-review: 5
                  code-reviewer:duplicate-code-detector: 1
                defaultSubAgentModelIntelligence: 3
              - id: plain
                name: Plain Mode
                systemPrompt: Plain.
            """;

        var modes = SystemChatModes.ParseModes(Yaml);

        modes[0].SubAgentReasoningEffort.Should().Be("xhigh");
        modes[0].SubAgentModelIntelligenceByType.Should().Contain("code-reviewer:architecture-review", 5);
        modes[0].DefaultSubAgentModelIntelligence.Should().Be(3);
        modes[1].SubAgentReasoningEffort.Should().BeNull();
        modes[1].SubAgentModelIntelligenceByType.Should().BeNull();
        modes[1].DefaultSubAgentModelIntelligence.Should().BeNull();
    }
}
