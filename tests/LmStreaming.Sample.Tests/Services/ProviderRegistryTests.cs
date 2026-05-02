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
            ["anthropic", "claude", "codex", "copilot", "openai", "test", "test-anthropic"],
            options => options.WithStrictOrdering());
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
