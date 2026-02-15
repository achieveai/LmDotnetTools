using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.OpenAIProvider.Utils;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Tests.Utils;

public class UsageCacheTests : IDisposable
{
    private readonly UsageCache _cache;

    public UsageCacheTests()
    {
        _cache = new UsageCache(1); // 1 second TTL for fast testing
    }

    public void Dispose()
    {
        _cache?.Dispose();
    }

    [Fact]
    public void TryGetUsage_WhenEmpty_ReturnsNull()
    {
        // Act
        var result = _cache.TryGetUsage("test-completion-id");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void SetUsage_AndTryGetUsage_ReturnsUsageWithCachedFlag()
    {
        // Arrange
        var completionId = "test-completion-id";
        var usage = new Usage
        {
            PromptTokens = 100,
            CompletionTokens = 50,
            TotalTokens = 150,
            TotalCost = 0.01,
            ExtraProperties = ImmutableDictionary<string, object?>.Empty.Add("model", "gpt-4"),
        };

        // Act
        _cache.SetUsage(completionId, usage);
        var cachedUsage = _cache.TryGetUsage(completionId);

        // Assert
        Assert.NotNull(cachedUsage);
        Assert.Equal(usage.PromptTokens, cachedUsage.PromptTokens);
        Assert.Equal(usage.CompletionTokens, cachedUsage.CompletionTokens);
        Assert.Equal(usage.TotalTokens, cachedUsage.TotalTokens);
        Assert.Equal(usage.TotalCost, cachedUsage.TotalCost);

        // Verify the cached flag is set
        Assert.True(cachedUsage.GetExtraProperty<bool>("is_cached"));

        // Verify original model info is preserved
        Assert.Equal("gpt-4", cachedUsage.GetExtraProperty<string>("model"));
    }

    [Fact]
    public async Task TryGetUsage_AfterTtlExpires_ReturnsNull()
    {
        // Arrange
        var completionId = "test-completion-id";
        var usage = new Usage
        {
            PromptTokens = 100,
            CompletionTokens = 50,
            TotalTokens = 150,
        };

        // Act
        _cache.SetUsage(completionId, usage);

        // Verify it's cached initially
        var cachedUsage = _cache.TryGetUsage(completionId);
        Assert.NotNull(cachedUsage);

        // Wait for TTL to expire (cache is set to 1 second)
        await Task.Delay(1500);

        // Try to get again - should be null
        var expiredUsage = _cache.TryGetUsage(completionId);

        // Assert
        Assert.Null(expiredUsage);
    }

    [Fact]
    public void SetUsage_WithNullOrEmptyCompletionId_DoesNotCrash()
    {
        // Arrange
        var usage = new Usage
        {
            PromptTokens = 100,
            CompletionTokens = 50,
            TotalTokens = 150,
        };

        // Act & Assert - should not crash
        _cache.SetUsage(null!, usage);
        _cache.SetUsage(string.Empty, usage);
        _cache.SetUsage(" ", usage);

        // Verify nothing was cached
        Assert.Null(_cache.TryGetUsage("anything"));
    }

    [Fact]
    public void RemoveUsage_RemovesFromCache()
    {
        // Arrange
        var completionId = "test-completion-id";
        var usage = new Usage
        {
            PromptTokens = 100,
            CompletionTokens = 50,
            TotalTokens = 150,
        };

        // Act
        _cache.SetUsage(completionId, usage);
        Assert.NotNull(_cache.TryGetUsage(completionId)); // Verify it's cached

        _cache.RemoveUsage(completionId);
        var result = _cache.TryGetUsage(completionId);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetStatistics_ReturnsValidStatistics()
    {
        // Act
        var stats = _cache.GetStatistics();

        // Assert
        Assert.Equal(1, stats.TtlSeconds); // TTL set in constructor
        Assert.False(stats.IsDisposed);
    }

    [Fact]
    public void Dispose_MarksAsDisposed()
    {
        // Act
        _cache.Dispose();
        var stats = _cache.GetStatistics();

        // Assert
        Assert.True(stats.IsDisposed);

        // Verify operations are safe after disposal
        Assert.Null(_cache.TryGetUsage("anything"));
    }

    [Fact]
    public void Constructor_WithCustomTtl_UsesCorrectTtl()
    {
        // Arrange & Act
        using var customCache = new UsageCache(600);
        var stats = customCache.GetStatistics();

        // Assert
        Assert.Equal(600, stats.TtlSeconds);
    }
}
