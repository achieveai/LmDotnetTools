namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Mode-layer sandbox env (spec §5): <c>Prompts.yaml</c> binding, the store's presence-aware
/// create/update semantics, and that an invalid env is refused and never persisted.
/// </summary>
public sealed class ChatModeEnvTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "lmstreaming-chatmode-env-tests",
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

    private FileChatModeStore CreateStore() => new(_dir);

    [Fact]
    public void ParseModes_ReadsEnvFromYaml()
    {
        var modes = SystemChatModes.ParseModes(
            """
            chatModes:
              - id: envd
                name: Envd
                systemPrompt: primary
                env:
                  FOO: bar
                  BAZ: qux
            """
        );

        var mode = modes.Should().ContainSingle().Subject;
        mode.Env.Should().Equal(new Dictionary<string, string> { ["FOO"] = "bar", ["BAZ"] = "qux" });
    }

    [Fact]
    public void ParseModes_NoEnvKey_LeavesEnvNull()
    {
        var modes = SystemChatModes.ParseModes(
            """
            chatModes:
              - id: plain
                name: Plain
                systemPrompt: primary
            """
        );

        modes.Single().Env.Should().BeNull();
    }

    /// <summary>
    /// A protected key in <c>Prompts.yaml</c> fails the boot loudly (the "fail closed" contract for
    /// the required system modes), naming the mode id and the offending key — and NEVER the value,
    /// which is deliberately not attacker-observable through server logs/exception telemetry.
    /// </summary>
    [Fact]
    public void ParseModes_ProtectedEnvKey_ThrowsNamingModeAndKeyNotValue()
    {
        var act = () =>
            SystemChatModes.ParseModes(
                """
                chatModes:
                  - id: bad-env
                    name: Bad Env
                    systemPrompt: primary
                    env:
                      HTTP_PROXY: http://attacker.example:8080
                """
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .Where(ex => ex.Message.Contains("bad-env") && ex.Message.Contains("HTTP_PROXY"))
            .Where(ex => !ex.Message.Contains("attacker.example"));
    }

    [Fact]
    public async Task Store_Create_PersistsEnv()
    {
        var store = CreateStore();

        var created = await store.CreateModeAsync(
            new ChatModeCreateUpdate
            {
                Name = "Envd",
                SystemPrompt = "p",
                Env = new Dictionary<string, string> { ["FOO"] = "bar" },
            }
        );

        created.Env.Should().Equal(new Dictionary<string, string> { ["FOO"] = "bar" });

        var reloaded = await store.GetModeAsync(created.Id);
        reloaded!.Env.Should().Equal(new Dictionary<string, string> { ["FOO"] = "bar" });
    }

    [Fact]
    public async Task Store_Update_EnvOmitted_PreservesExisting()
    {
        var store = CreateStore();
        var created = await store.CreateModeAsync(
            new ChatModeCreateUpdate
            {
                Name = "Envd",
                SystemPrompt = "p",
                Env = new Dictionary<string, string> { ["FOO"] = "bar" },
            }
        );

        var updated = await store.UpdateModeAsync(
            created.Id,
            new ChatModeCreateUpdate { Name = "Envd", SystemPrompt = "p2" }
        );

        updated.Env.Should().Equal(new Dictionary<string, string> { ["FOO"] = "bar" });
    }

    [Fact]
    public async Task Store_Update_EnvPresent_ReplacesExisting()
    {
        var store = CreateStore();
        var created = await store.CreateModeAsync(
            new ChatModeCreateUpdate
            {
                Name = "Envd",
                SystemPrompt = "p",
                Env = new Dictionary<string, string> { ["FOO"] = "bar" },
            }
        );

        var updated = await store.UpdateModeAsync(
            created.Id,
            new ChatModeCreateUpdate
            {
                Name = "Envd",
                SystemPrompt = "p",
                Env = new Dictionary<string, string> { ["BAZ"] = "qux" },
            }
        );

        updated.Env.Should().Equal(new Dictionary<string, string> { ["BAZ"] = "qux" });
    }

    [Fact]
    public async Task Store_Update_ExplicitNullEnv_ClearsExisting()
    {
        var store = CreateStore();
        var created = await store.CreateModeAsync(
            new ChatModeCreateUpdate
            {
                Name = "Envd",
                SystemPrompt = "p",
                Env = new Dictionary<string, string> { ["FOO"] = "bar" },
            }
        );

        var updated = await store.UpdateModeAsync(
            created.Id,
            new ChatModeCreateUpdate
            {
                Name = "Envd",
                SystemPrompt = "p",
                Env = null,
            }
        );

        updated.Env.Should().BeNull();
    }

    [Fact]
    public async Task Store_Create_InvalidEnv_ThrowsAndStoresNothing()
    {
        var store = CreateStore();

        var act = async () =>
            await store.CreateModeAsync(
                new ChatModeCreateUpdate
                {
                    Name = "Bad",
                    SystemPrompt = "p",
                    Env = new Dictionary<string, string> { ["NO_PROXY"] = "example.com" },
                }
            );

        await act.Should().ThrowAsync<SandboxEnvValidationException>();
        (await store.GetAllModesAsync()).Should().OnlyContain(m => m.IsSystemDefined, "nothing must be persisted");
    }

    [Fact]
    public async Task Store_Update_InvalidEnv_ThrowsAndLeavesExistingUnchanged()
    {
        var store = CreateStore();
        var created = await store.CreateModeAsync(
            new ChatModeCreateUpdate
            {
                Name = "Envd",
                SystemPrompt = "p",
                Env = new Dictionary<string, string> { ["FOO"] = "bar" },
            }
        );

        var act = async () =>
            await store.UpdateModeAsync(
                created.Id,
                new ChatModeCreateUpdate
                {
                    Name = "Envd",
                    SystemPrompt = "p",
                    Env = new Dictionary<string, string> { ["NO_PROXY"] = "example.com" },
                }
            );

        await act.Should().ThrowAsync<SandboxEnvValidationException>();
        var reloaded = await store.GetModeAsync(created.Id);
        reloaded!.Env.Should().Equal(new Dictionary<string, string> { ["FOO"] = "bar" });
    }

    [Theory]
    [InlineData("HTTP_PROXY")]
    [InlineData("NO_PROXY")]
    public async Task Controller_Create_InvalidEnv_Returns400WithCodeAndKeyNotValue(string key)
    {
        var controller = new ChatModesController(CreateStore(), new NoOpSandboxEnvApplier());

        var result = await controller.Create(
            new ChatModeCreateUpdate
            {
                Name = "Bad",
                SystemPrompt = "p",
                Env = new Dictionary<string, string> { [key] = "secret-value" },
            }
        );

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var payload = JsonSerializer.Serialize(bad.Value);
        payload.Should().Contain("invalid_env").And.Contain(key).And.NotContain("secret-value");
    }

    /// <summary>
    /// A GATEWAY rejection during the post-save reapply must answer 400 <c>invalid_env</c>, exactly as
    /// the workspace route already did.
    /// <para>
    /// This is a different failure from the validator rejection above: the mode HAS been written by
    /// the time <c>ReapplyForModeAsync</c> runs, so the store and the gateway now disagree. Without
    /// the catch, the identical user action returned 400 on the workspace route and 500 here — and a
    /// 500 tells the caller nothing about which keys were refused, nor that the save partly landed.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Controller_Update_GatewayRejectsEnv_Returns400WithKeys_AndSaysTheModeWasSaved()
    {
        var store = CreateStore();
        var created = await store.CreateModeAsync(new ChatModeCreateUpdate { Name = "M", SystemPrompt = "p" });
        var controller = new ChatModesController(store, new GatewayRejectsEnvApplier("HTTP_PROXY"));

        var result = await controller.Update(
            created.Id,
            new ChatModeCreateUpdate
            {
                Name = "M",
                SystemPrompt = "p",
                Env = new Dictionary<string, string> { ["FOO"] = "secret-value" },
            }
        );

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var payload = JsonSerializer.Serialize(bad.Value);
        payload.Should().Contain("invalid_env");
        payload.Should().Contain("HTTP_PROXY", "the caller needs to know WHICH key the gateway refused");
        payload.Should().Contain("was saved", "a partial success must not read as a rejected write");
        payload.Should().NotContain("secret-value", "a value must never reach a response body");
    }
}
