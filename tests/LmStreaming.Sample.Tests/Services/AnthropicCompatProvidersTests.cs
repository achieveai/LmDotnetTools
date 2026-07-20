using LmStreaming.Sample.Services;
using Microsoft.Extensions.Logging;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Tests for <see cref="AnthropicCompatProviders.DiscoverFromEnv" /> — the env-var-driven discovery
/// of generic Anthropic-compatible provider families (e.g. DeepSeek). Mirrors the "degrade to empty
/// list, never throw" contract of the Copilot model discovery path.
/// </summary>
[Collection("EnvironmentVariables")]
public class AnthropicCompatProvidersTests
{
    [Fact]
    public void DiscoverFromEnv_ReturnsEmpty_WhenProvidersListUnset()
    {
        using var _ = EnvScope.Set("ANTHROPIC_COMPAT_PROVIDERS", null);

        var models = AnthropicCompatProviders.DiscoverFromEnv(NullLoggerFactory.Instance);

        models.Should().BeEmpty();
    }

    [Fact]
    public void DiscoverFromEnv_ReturnsEmpty_WhenProvidersListBlank()
    {
        using var _ = EnvScope.Set("ANTHROPIC_COMPAT_PROVIDERS", "   ");

        var models = AnthropicCompatProviders.DiscoverFromEnv(NullLoggerFactory.Instance);

        models.Should().BeEmpty();
    }

    [Fact]
    public void DiscoverFromEnv_ParsesSingleFamily_WithMultipleModels()
    {
        using var _ = EnvScope.Set("ANTHROPIC_COMPAT_PROVIDERS", "DEEPSEEK");
        using var __ = EnvScope.Set("DEEPSEEK_ANTHROPIC_URL", "https://api.deepseek.com/anthropic");
        using var ___ = EnvScope.Set("DEEPSEEK_APIKEY", "sk-deepseek");
        using var ____ = EnvScope.Set("DEEPSEEK_MODELS", "deepseek-v4-pro,deepseek-v4-flash");

        var models = AnthropicCompatProviders.DiscoverFromEnv(NullLoggerFactory.Instance);

        models.Should().BeEquivalentTo(
            [
                new AnthropicCompatModel(
                    "deepseek-v4-pro", "deepseek-v4-pro", "deepseek-v4-pro",
                    "https://api.deepseek.com/anthropic", "sk-deepseek", "DEEPSEEK"),
                new AnthropicCompatModel(
                    "deepseek-v4-flash", "deepseek-v4-flash", "deepseek-v4-flash",
                    "https://api.deepseek.com/anthropic", "sk-deepseek", "DEEPSEEK"),
            ],
            options => options.WithStrictOrdering());
    }

    [Fact]
    public void DiscoverFromEnv_ParsesMultipleFamilies()
    {
        using var _ = EnvScope.Set("ANTHROPIC_COMPAT_PROVIDERS", "DEEPSEEK,KIMI");
        using var __ = EnvScope.Set("DEEPSEEK_ANTHROPIC_URL", "https://api.deepseek.com/anthropic");
        using var ___ = EnvScope.Set("DEEPSEEK_APIKEY", "sk-deepseek");
        using var ____ = EnvScope.Set("DEEPSEEK_MODELS", "deepseek-v4-pro");
        using var _____ = EnvScope.Set("KIMI_ANTHROPIC_URL", "https://api.kimi.com/coding");
        using var ______ = EnvScope.Set("KIMI_APIKEY", "sk-kimi");
        using var _______ = EnvScope.Set("KIMI_MODELS", "kimi-2.5");

        var models = AnthropicCompatProviders.DiscoverFromEnv(NullLoggerFactory.Instance);

        models.Select(m => m.Id).Should().BeEquivalentTo(["deepseek-v4-pro", "kimi-2-5"]);
        models.Single(m => m.FamilyKey == "DEEPSEEK").BaseUrl.Should().Be("https://api.deepseek.com/anthropic");
        models.Single(m => m.FamilyKey == "KIMI").BaseUrl.Should().Be("https://api.kimi.com/coding");
    }

    [Theory]
    [InlineData(null, "sk-key", "model-a")]
    [InlineData("https://example.com/anthropic", null, "model-a")]
    [InlineData("https://example.com/anthropic", "sk-key", null)]
    public void DiscoverFromEnv_SkipsFamily_WhenAnyOfItsThreeVarsIsMissing(
        string? baseUrl,
        string? apiKey,
        string? modelsRaw)
    {
        using var _ = EnvScope.Set("ANTHROPIC_COMPAT_PROVIDERS", "ACME");
        using var __ = EnvScope.Set("ACME_ANTHROPIC_URL", baseUrl);
        using var ___ = EnvScope.Set("ACME_APIKEY", apiKey);
        using var ____ = EnvScope.Set("ACME_MODELS", modelsRaw);

        var models = AnthropicCompatProviders.DiscoverFromEnv(NullLoggerFactory.Instance);

        models.Should().BeEmpty();
    }

    [Fact]
    public void DiscoverFromEnv_SkipsOnlyTheIncompleteFamily_KeepsOthers()
    {
        using var _ = EnvScope.Set("ANTHROPIC_COMPAT_PROVIDERS", "DEEPSEEK,BROKEN");
        using var __ = EnvScope.Set("DEEPSEEK_ANTHROPIC_URL", "https://api.deepseek.com/anthropic");
        using var ___ = EnvScope.Set("DEEPSEEK_APIKEY", "sk-deepseek");
        using var ____ = EnvScope.Set("DEEPSEEK_MODELS", "deepseek-v4-pro");
        using var _____ = EnvScope.Set("BROKEN_ANTHROPIC_URL", null);
        using var ______ = EnvScope.Set("BROKEN_APIKEY", "sk-broken");
        using var _______ = EnvScope.Set("BROKEN_MODELS", "broken-model");

        var models = AnthropicCompatProviders.DiscoverFromEnv(NullLoggerFactory.Instance);

        models.Select(m => m.Id).Should().BeEquivalentTo(["deepseek-v4-pro"]);
    }

    [Fact]
    public void DiscoverFromEnv_SlugifiesNonAlphanumericCharacters_ForId_ButKeepsModelNameVerbatim()
    {
        using var _ = EnvScope.Set("ANTHROPIC_COMPAT_PROVIDERS", "DEEPSEEK");
        using var __ = EnvScope.Set("DEEPSEEK_ANTHROPIC_URL", "https://api.deepseek.com/anthropic");
        using var ___ = EnvScope.Set("DEEPSEEK_APIKEY", "sk-deepseek");
        using var ____ = EnvScope.Set("DEEPSEEK_MODELS", "DeepSeek V4.Pro!");

        var models = AnthropicCompatProviders.DiscoverFromEnv(NullLoggerFactory.Instance);

        var model = models.Single();
        model.Id.Should().Be("deepseek-v4-pro-");
        model.ModelName.Should().Be("DeepSeek V4.Pro!");
        model.DisplayName.Should().Be("DeepSeek V4.Pro!");
    }

    [Fact]
    public void DiscoverFromEnv_TrimsAndIgnoresEmptyEntries_InModelsList()
    {
        using var _ = EnvScope.Set("ANTHROPIC_COMPAT_PROVIDERS", " DEEPSEEK , ");
        using var __ = EnvScope.Set("DEEPSEEK_ANTHROPIC_URL", "https://api.deepseek.com/anthropic");
        using var ___ = EnvScope.Set("DEEPSEEK_APIKEY", "sk-deepseek");
        using var ____ = EnvScope.Set("DEEPSEEK_MODELS", " deepseek-v4-pro ,, deepseek-v4-flash ");

        var models = AnthropicCompatProviders.DiscoverFromEnv(NullLoggerFactory.Instance);

        models.Select(m => m.Id).Should().BeEquivalentTo(["deepseek-v4-pro", "deepseek-v4-flash"]);
    }

    [Fact]
    public void DiscoverFromEnv_DropsSameFamilySlugCollision_KeepingFirstAndWarning()
    {
        // "kimi-2.5" and "kimi-2-5" both slugify to "kimi-2-5" — a same-family collision that would
        // otherwise let ProviderRegistry silently drop the second entry.
        using var _ = EnvScope.Set("ANTHROPIC_COMPAT_PROVIDERS", "KIMI");
        using var __ = EnvScope.Set("KIMI_ANTHROPIC_URL", "https://api.kimi.com/coding");
        using var ___ = EnvScope.Set("KIMI_APIKEY", "sk-kimi");
        using var ____ = EnvScope.Set("KIMI_MODELS", "kimi-2.5,kimi-2-5");

        var factory = new CapturingLoggerFactory();
        var models = AnthropicCompatProviders.DiscoverFromEnv(factory);

        // First configured entry wins; the colliding second is dropped, not silently overwritten.
        models.Select(m => m.ModelName).Should().BeEquivalentTo(["kimi-2.5"]);
        models.Single().Id.Should().Be("kimi-2-5");
        factory.Entries
            .Should()
            .ContainSingle(e => e.Level == LogLevel.Warning && e.Message.Contains("collides") && e.Message.Contains("kimi-2-5"));
    }

    [Fact]
    public void DiscoverFromEnv_DropsCrossFamilyIdCollision_KeepingFirstAndWarning()
    {
        // The same model name configured under two families generates the same id — a cross-family
        // collision. Keep the first family's entry and warn rather than silently routing the shared id
        // through the wrong family.
        using var _ = EnvScope.Set("ANTHROPIC_COMPAT_PROVIDERS", "DEEPSEEK,KIMI");
        using var __ = EnvScope.Set("DEEPSEEK_ANTHROPIC_URL", "https://api.deepseek.com/anthropic");
        using var ___ = EnvScope.Set("DEEPSEEK_APIKEY", "sk-deepseek");
        using var ____ = EnvScope.Set("DEEPSEEK_MODELS", "shared-model");
        using var _____ = EnvScope.Set("KIMI_ANTHROPIC_URL", "https://api.kimi.com/coding");
        using var ______ = EnvScope.Set("KIMI_APIKEY", "sk-kimi");
        using var _______ = EnvScope.Set("KIMI_MODELS", "shared-model");

        var factory = new CapturingLoggerFactory();
        var models = AnthropicCompatProviders.DiscoverFromEnv(factory);

        var model = models.Should().ContainSingle().Subject;
        model.Id.Should().Be("shared-model");
        model.FamilyKey.Should().Be("DEEPSEEK");
        factory.Entries
            .Should()
            .ContainSingle(e => e.Level == LogLevel.Warning && e.Message.Contains("collides") && e.Message.Contains("KIMI"));
    }

    /// <summary>
    /// Minimal <see cref="ILoggerFactory"/> capturing every entry across all created (named) loggers,
    /// so a test can assert the discovery path logs a clear collision warning. <c>DiscoverFromEnv</c>
    /// resolves a non-generic named logger, which the shared <c>CapturingLogger&lt;T&gt;</c> test double
    /// (an <c>ILogger&lt;T&gt;</c>) does not cover, so this small factory bridges the gap.
    /// </summary>
    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_entries);

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }

        private sealed class CapturingLogger(List<(LogLevel Level, string Message)> entries) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state)
                where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var message = formatter(state, exception);
                lock (entries)
                {
                    entries.Add((logLevel, message));
                }
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();

                public void Dispose() { }
            }
        }
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

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _previous);
        }
    }
}
