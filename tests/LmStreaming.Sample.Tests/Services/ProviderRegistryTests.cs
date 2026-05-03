using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.TestDoubles;

namespace LmStreaming.Sample.Tests.Services;

[Collection("EnvironmentVariables")]
public class ProviderRegistryTests
{
    [Fact]
    public void DefaultProviderId_FallsBackToTest_WhenEnvVarUnset()
    {
        using var _ = EnvScope.Set("LM_PROVIDER_MODE", null);

        var registry = new ProviderRegistry(new FakeFileSystemProbe());

        registry.DefaultProviderId.Should().Be("test");
    }

    [Fact]
    public void DefaultProviderId_NormalizesEnvVar()
    {
        using var _ = EnvScope.Set("LM_PROVIDER_MODE", "  OpenAI  ");

        var registry = new ProviderRegistry(new FakeFileSystemProbe());

        registry.DefaultProviderId.Should().Be("openai");
    }

    [Fact]
    public void IsAvailable_OpenAi_RequiresApiKey()
    {
        using var _ = EnvScope.Set("LM_PROVIDER_MODE", "test");
        using var __ = EnvScope.Set("OPENAI_API_KEY", null);

        var registry = new ProviderRegistry(new FakeFileSystemProbe());

        registry.IsAvailable("openai").Should().BeFalse();
    }

    [Fact]
    public void IsAvailable_OpenAi_TrueWhenApiKeyPresent()
    {
        using var _ = EnvScope.Set("LM_PROVIDER_MODE", "test");
        using var __ = EnvScope.Set("OPENAI_API_KEY", "sk-test");

        var registry = new ProviderRegistry(new FakeFileSystemProbe());

        registry.IsAvailable("openai").Should().BeTrue();
    }

    [Fact]
    public void IsAvailable_Anthropic_RequiresApiKey()
    {
        using var _ = EnvScope.Set("LM_PROVIDER_MODE", "test");
        using var __ = EnvScope.Set("ANTHROPIC_API_KEY", null);

        var registry = new ProviderRegistry(new FakeFileSystemProbe());

        registry.IsAvailable("anthropic").Should().BeFalse();
    }

    [Fact]
    public void IsAvailable_Claude_TrueWhenCliOnPath()
    {
        using var _ = EnvScope.Set("LM_PROVIDER_MODE", "test");
        using var __ = EnvScope.Set("CLAUDE_CLI_PATH", null);
        var probe = new FakeFileSystemProbe(executablesOnPath: ["claude"]);

        var registry = new ProviderRegistry(probe);

        registry.IsAvailable("claude").Should().BeTrue();
    }

    [Fact]
    public void IsAvailable_Claude_FalseWhenCliMissing()
    {
        using var _ = EnvScope.Set("LM_PROVIDER_MODE", "test");
        using var __ = EnvScope.Set("CLAUDE_CLI_PATH", null);

        var registry = new ProviderRegistry(new FakeFileSystemProbe());

        registry.IsAvailable("claude").Should().BeFalse();
    }

    [Fact]
    public void IsAvailable_Claude_TrueWhenExplicitPathExists()
    {
        using var _ = EnvScope.Set("LM_PROVIDER_MODE", "test");
        using var __ = EnvScope.Set("CLAUDE_CLI_PATH", "/usr/local/bin/claude");
        var probe = new FakeFileSystemProbe(existingFiles: ["/usr/local/bin/claude"]);

        var registry = new ProviderRegistry(probe);

        registry.IsAvailable("claude").Should().BeTrue();
    }

    [Fact]
    public void IsAvailable_Claude_FalseWhenExplicitPathMissing()
    {
        using var _ = EnvScope.Set("LM_PROVIDER_MODE", "test");
        using var __ = EnvScope.Set("CLAUDE_CLI_PATH", "/nope/claude");
        var probe = new FakeFileSystemProbe(executablesOnPath: ["claude"]);

        var registry = new ProviderRegistry(probe);

        registry.IsAvailable("claude").Should().BeFalse();
    }

    [Fact]
    public void IsAvailable_Copilot_TrueWhenCliOnPath()
    {
        using var _ = EnvScope.Set("LM_PROVIDER_MODE", "test");
        using var __ = EnvScope.Set("COPILOT_CLI_PATH", null);
        var probe = new FakeFileSystemProbe(executablesOnPath: ["copilot"]);

        var registry = new ProviderRegistry(probe);

        registry.IsAvailable("copilot").Should().BeTrue();
    }

    [Fact]
    public void IsAvailable_TestProviders_AlwaysAvailable()
    {
        using var _ = EnvScope.Set("LM_PROVIDER_MODE", "test");

        var registry = new ProviderRegistry(new FakeFileSystemProbe());

        registry.IsAvailable("test").Should().BeTrue();
        registry.IsAvailable("test-anthropic").Should().BeTrue();
    }

    [Fact]
    public void IsAvailable_Codex_AlwaysAvailable()
    {
        using var _ = EnvScope.Set("LM_PROVIDER_MODE", "test");

        var registry = new ProviderRegistry(new FakeFileSystemProbe());

        registry.IsAvailable("codex").Should().BeTrue();
    }

    [Fact]
    public void IsAvailable_NormalizesIdCasing()
    {
        using var _ = EnvScope.Set("LM_PROVIDER_MODE", "test");

        var registry = new ProviderRegistry(new FakeFileSystemProbe());

        registry.IsAvailable("  CODEX  ").Should().BeTrue();
    }

    [Fact]
    public void IsAvailable_FalseForUnknownProvider()
    {
        using var _ = EnvScope.Set("LM_PROVIDER_MODE", "test");

        var registry = new ProviderRegistry(new FakeFileSystemProbe());

        registry.IsAvailable("does-not-exist").Should().BeFalse();
    }

    [Fact]
    public void IsKnown_TrueForCatalogEntry_FalseForUnknown()
    {
        using var _ = EnvScope.Set("LM_PROVIDER_MODE", "test");

        var registry = new ProviderRegistry(new FakeFileSystemProbe());

        registry.IsKnown("openai").Should().BeTrue();
        registry.IsKnown("does-not-exist").Should().BeFalse();
        registry.IsKnown("").Should().BeFalse();
        registry.IsKnown(null!).Should().BeFalse();
    }

    [Fact]
    public void ListAll_ReturnsCompleteCatalogSortedById()
    {
        using var _ = EnvScope.Set("LM_PROVIDER_MODE", "test");

        var registry = new ProviderRegistry(new FakeFileSystemProbe());
        var catalog = registry.ListAll();

        catalog.Select(p => p.Id).Should().BeEquivalentTo(
            [
                "anthropic", "claude", "claude-mock", "codex", "codex-mock",
                "copilot", "copilot-mock", "openai", "test", "test-anthropic",
            ],
            options => options.WithStrictOrdering());
    }

    [Fact]
    public void IsAvailable_ClaudeMock_RequiresCliAndRunningHost()
    {
        using var _ = EnvScope.Set("LM_PROVIDER_MODE", "test");
        using var __ = EnvScope.Set("CLAUDE_CLI_PATH", null);
        var probe = new FakeFileSystemProbe(executablesOnPath: ["claude"]);

        var withHost = new ProviderRegistry(probe, mockHostIsRunning: () => true);
        var withoutHost = new ProviderRegistry(probe, mockHostIsRunning: () => false);

        withHost.IsAvailable("claude-mock").Should().BeTrue();
        withoutHost.IsAvailable("claude-mock").Should().BeFalse();
    }

    [Fact]
    public void IsAvailable_ClaudeMock_FalseWhenCliMissing()
    {
        using var _ = EnvScope.Set("LM_PROVIDER_MODE", "test");
        using var __ = EnvScope.Set("CLAUDE_CLI_PATH", null);

        var registry = new ProviderRegistry(new FakeFileSystemProbe(), mockHostIsRunning: () => true);

        registry.IsAvailable("claude-mock").Should().BeFalse();
    }

    [Fact]
    public void IsAvailable_CopilotMock_RequiresCliAndRunningHost()
    {
        using var _ = EnvScope.Set("LM_PROVIDER_MODE", "test");
        using var __ = EnvScope.Set("COPILOT_CLI_PATH", null);
        var probe = new FakeFileSystemProbe(executablesOnPath: ["copilot"]);

        var withHost = new ProviderRegistry(probe, mockHostIsRunning: () => true);
        var withoutHost = new ProviderRegistry(probe, mockHostIsRunning: () => false);

        withHost.IsAvailable("copilot-mock").Should().BeTrue();
        withoutHost.IsAvailable("copilot-mock").Should().BeFalse();
    }

    [Fact]
    public void IsAvailable_CopilotMock_FalseWhenCliMissing()
    {
        using var _ = EnvScope.Set("LM_PROVIDER_MODE", "test");
        using var __ = EnvScope.Set("COPILOT_CLI_PATH", null);

        var registry = new ProviderRegistry(new FakeFileSystemProbe(), mockHostIsRunning: () => true);

        registry.IsAvailable("copilot-mock").Should().BeFalse();
    }

    [Fact]
    public void IsAvailable_CodexMock_TracksHostState()
    {
        using var _ = EnvScope.Set("LM_PROVIDER_MODE", "test");

        var withHost = new ProviderRegistry(new FakeFileSystemProbe(), mockHostIsRunning: () => true);
        var withoutHost = new ProviderRegistry(new FakeFileSystemProbe(), mockHostIsRunning: () => false);

        withHost.IsAvailable("codex-mock").Should().BeTrue();
        withoutHost.IsAvailable("codex-mock").Should().BeFalse();
    }

    [Fact]
    public void Get_MockProvider_ReflectsLiveHostState()
    {
        using var _ = EnvScope.Set("LM_PROVIDER_MODE", "test");
        var hostRunning = false;

        // Constructed once with a delegate that closes over the local. Flipping the local
        // after construction must propagate to subsequent Get() calls — that's what proves the
        // mock-host signal is re-evaluated rather than frozen at startup.
        var registry = new ProviderRegistry(new FakeFileSystemProbe(), mockHostIsRunning: () => hostRunning);
        registry.Get("codex-mock")!.Available.Should().BeFalse();

        hostRunning = true;

        registry.Get("codex-mock")!.Available.Should().BeTrue();
    }

    [Fact]
    public void ListAll_FlagsMockProvidersUnavailable_WhenHostNotRunning()
    {
        using var _ = EnvScope.Set("LM_PROVIDER_MODE", "test");
        using var __ = EnvScope.Set("CLAUDE_CLI_PATH", null);
        using var ___ = EnvScope.Set("COPILOT_CLI_PATH", null);
        var probe = new FakeFileSystemProbe(executablesOnPath: ["claude", "copilot"]);

        var registry = new ProviderRegistry(probe, mockHostIsRunning: () => false);
        var catalog = registry.ListAll().ToDictionary(p => p.Id, p => p.Available);

        catalog["claude-mock"].Should().BeFalse();
        catalog["codex-mock"].Should().BeFalse();
        catalog["copilot-mock"].Should().BeFalse();
    }

    [Fact]
    public void ListAll_FlagsUnavailableProviders()
    {
        using var _ = EnvScope.Set("LM_PROVIDER_MODE", "test");
        using var __ = EnvScope.Set("OPENAI_API_KEY", null);
        using var ___ = EnvScope.Set("ANTHROPIC_API_KEY", null);

        var registry = new ProviderRegistry(new FakeFileSystemProbe());
        var catalog = registry.ListAll().ToDictionary(p => p.Id, p => p.Available);

        catalog["openai"].Should().BeFalse();
        catalog["anthropic"].Should().BeFalse();
        catalog["test"].Should().BeTrue();
    }

    [Fact]
    public void Get_ReturnsDescriptor_WithAvailability()
    {
        using var _ = EnvScope.Set("LM_PROVIDER_MODE", "test");
        using var __ = EnvScope.Set("OPENAI_API_KEY", "sk-test");

        var registry = new ProviderRegistry(new FakeFileSystemProbe());
        var descriptor = registry.Get("openai");

        descriptor.Should().NotBeNull();
        descriptor!.Id.Should().Be("openai");
        descriptor.DisplayName.Should().Be("OpenAI");
        descriptor.Available.Should().BeTrue();
    }

    [Fact]
    public void KnownLimitation_TaggedOnBrokenMocks_UnsetOnHealthyOnes()
    {
        // Surfaces the UX banner that points users at the follow-up issues for the broken
        // mock variants (#28 codex, #29 claude). When those land, this assertion flips.
        using var _ = EnvScope.Set("LM_PROVIDER_MODE", "test");
        var probe = new FakeFileSystemProbe(executablesOnPath: ["claude", "copilot"]);

        var registry = new ProviderRegistry(probe, () => true);
        var byId = registry.ListAll().ToDictionary(p => p.Id);

        byId["codex-mock"].KnownLimitation.Should().NotBeNullOrWhiteSpace()
            .And.Subject.Should().Contain("#28");
        byId["claude-mock"].KnownLimitation.Should().NotBeNullOrWhiteSpace()
            .And.Subject.Should().Contain("#29");

        // copilot-mock works end-to-end against the mock host and must NOT carry a caveat.
        byId["copilot-mock"].KnownLimitation.Should().BeNull();

        // Non-mock entries inherit the default null.
        byId["openai"].KnownLimitation.Should().BeNull();
        byId["claude"].KnownLimitation.Should().BeNull();

        // Get() must propagate KnownLimitation just like ListAll() — guards against future
        // refactors that recompose descriptors without preserving the caveat.
        registry.Get("codex-mock")!.KnownLimitation.Should().Contain("#28");
        registry.Get("claude-mock")!.KnownLimitation.Should().Contain("#29");
        registry.Get("copilot-mock")!.KnownLimitation.Should().BeNull();
    }

    [Fact]
    public void Get_ReturnsNull_ForUnknownProvider()
    {
        using var _ = EnvScope.Set("LM_PROVIDER_MODE", "test");

        var registry = new ProviderRegistry(new FakeFileSystemProbe());

        registry.Get("does-not-exist").Should().BeNull();
        registry.Get(null!).Should().BeNull();
        registry.Get("").Should().BeNull();
    }

    /// <summary>
    /// Snapshots the current value of an environment variable and restores it on dispose.
    /// Tests that mutate env vars must use this scope to avoid polluting other tests.
    /// </summary>
    private sealed class EnvScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        private EnvScope(string name, string? previous)
        {
            _name = name;
            _previous = previous;
        }

        public static EnvScope Set(string name, string? value)
        {
            var previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
            return new EnvScope(name, previous);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}

/// <summary>
/// All tests that mutate process-wide environment variables must opt into this collection
/// so xUnit serialises them — env vars are global state and concurrent mutation flakes.
/// </summary>
[CollectionDefinition("EnvironmentVariables", DisableParallelization = true)]
public class EnvironmentVariablesCollection
{
}
