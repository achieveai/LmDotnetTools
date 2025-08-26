using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AchieveAi.LmDotnetTools.LmCore.Configuration;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Middleware;

/// <summary>
/// Comprehensive test suite for FunctionFilter class
/// </summary>
public class FunctionFilterTests
{
    private readonly Mock<ILogger> _mockLogger;

    public FunctionFilterTests()
    {
        _mockLogger = new Mock<ILogger>();
    }

    #region Helper Methods

    private static FunctionDescriptor CreateTestDescriptor(
        string functionName,
        string providerName = "TestProvider"
    )
    {
        return new FunctionDescriptor
        {
            Contract = new FunctionContract
            {
                Name = functionName,
                Description = $"Test function {functionName} from {providerName}",
            },
            Handler = _ => Task.FromResult($"Result from {functionName}"),
            ProviderName = providerName,
        };
    }

    #endregion

    #region Basic Functionality Tests

    [Fact]
    public void ShouldFilterFunctionWithReason_WithNullConfig_ReturnsFilteringDisabled()
    {
        // Arrange
        var filter = new FunctionFilter(null, _mockLogger.Object);
        var descriptor = CreateTestDescriptor("testFunction");

        // Act
        var result = filter.ShouldFilterFunctionWithReason(descriptor, "testFunction");

        // Assert
        Assert.False(result.IsFiltered);
        Assert.Equal(FilterRuleType.None, result.RuleType);
        Assert.Contains("disabled", result.Reason);
    }

    [Fact]
    public void ShouldFilterFunctionWithReason_WithDisabledFiltering_ReturnsFilteringDisabled()
    {
        // Arrange
        var config = new FunctionFilterConfig { EnableFiltering = false };
        var filter = new FunctionFilter(config, _mockLogger.Object);
        var descriptor = CreateTestDescriptor("testFunction");

        // Act
        var result = filter.ShouldFilterFunctionWithReason(descriptor, "testFunction");

        // Assert
        Assert.False(result.IsFiltered);
        Assert.Equal(FilterRuleType.None, result.RuleType);
        Assert.Contains("disabled", result.Reason);
    }

    #endregion

    #region Provider Disabled Tests

    [Fact]
    public void ShouldFilterFunctionWithReason_WithDisabledProvider_FiltersFunction()
    {
        // Arrange
        var config = new FunctionFilterConfig
        {
            EnableFiltering = true,
            ProviderConfigs = new Dictionary<string, ProviderFilterConfig>
            {
                ["TestProvider"] = new ProviderFilterConfig { Enabled = false },
            },
        };
        var filter = new FunctionFilter(config, _mockLogger.Object);
        var descriptor = CreateTestDescriptor("testFunction", "TestProvider");

        // Act
        var result = filter.ShouldFilterFunctionWithReason(descriptor, "testFunction");

        // Assert
        Assert.True(result.IsFiltered);
        Assert.Equal(FilterRuleType.ProviderDisabled, result.RuleType);
        Assert.Contains("TestProvider", result.Reason);
        Assert.Contains("disabled", result.Reason);
    }

    #endregion

    #region Provider Block List Tests

    [Fact]
    public void ShouldFilterFunctionWithReason_WithProviderBlockList_FiltersMatchingFunction()
    {
        // Arrange
        var config = new FunctionFilterConfig
        {
            EnableFiltering = true,
            ProviderConfigs = new Dictionary<string, ProviderFilterConfig>
            {
                ["TestProvider"] = new ProviderFilterConfig
                {
                    Enabled = true,
                    BlockedFunctions = new List<string> { "blockedFunc", "anotherBlocked" },
                },
            },
        };
        var filter = new FunctionFilter(config, _mockLogger.Object);
        var descriptor = CreateTestDescriptor("blockedFunc", "TestProvider");

        // Act
        var result = filter.ShouldFilterFunctionWithReason(descriptor, "blockedFunc");

        // Assert
        Assert.True(result.IsFiltered);
        Assert.Equal(FilterRuleType.ProviderBlockList, result.RuleType);
        Assert.Equal("blockedFunc", result.MatchedPattern);
        Assert.Contains("blocked", result.Reason);
        Assert.Contains("TestProvider", result.Reason);
    }

    [Fact]
    public void ShouldFilterFunctionWithReason_WithProviderBlockListWildcard_FiltersMatchingPattern()
    {
        // Arrange
        var config = new FunctionFilterConfig
        {
            EnableFiltering = true,
            ProviderConfigs = new Dictionary<string, ProviderFilterConfig>
            {
                ["TestProvider"] = new ProviderFilterConfig
                {
                    Enabled = true,
                    BlockedFunctions = new List<string> { "blocked*" },
                },
            },
        };
        var filter = new FunctionFilter(config, _mockLogger.Object);
        var descriptor = CreateTestDescriptor("blockedFunction", "TestProvider");

        // Act
        var result = filter.ShouldFilterFunctionWithReason(descriptor, "blockedFunction");

        // Assert
        Assert.True(result.IsFiltered);
        Assert.Equal(FilterRuleType.ProviderBlockList, result.RuleType);
        Assert.Equal("blocked*", result.MatchedPattern);
    }

    #endregion

    #region Provider Allow List Tests

    [Fact]
    public void ShouldFilterFunctionWithReason_WithProviderAllowList_FiltersNonMatchingFunction()
    {
        // Arrange
        var config = new FunctionFilterConfig
        {
            EnableFiltering = true,
            ProviderConfigs = new Dictionary<string, ProviderFilterConfig>
            {
                ["TestProvider"] = new ProviderFilterConfig
                {
                    Enabled = true,
                    AllowedFunctions = new List<string> { "allowedFunc", "anotherAllowed" },
                },
            },
        };
        var filter = new FunctionFilter(config, _mockLogger.Object);
        var descriptor = CreateTestDescriptor("notAllowed", "TestProvider");

        // Act
        var result = filter.ShouldFilterFunctionWithReason(descriptor, "notAllowed");

        // Assert
        Assert.True(result.IsFiltered);
        Assert.Equal(FilterRuleType.ProviderAllowList, result.RuleType);
        Assert.Contains("not in", result.Reason);
        Assert.Contains("allow list", result.Reason);
    }

    [Fact]
    public void ShouldFilterFunctionWithReason_WithProviderAllowList_AllowsMatchingFunction()
    {
        // Arrange
        var config = new FunctionFilterConfig
        {
            EnableFiltering = true,
            ProviderConfigs = new Dictionary<string, ProviderFilterConfig>
            {
                ["TestProvider"] = new ProviderFilterConfig
                {
                    Enabled = true,
                    AllowedFunctions = new List<string> { "allowedFunc", "anotherAllowed" },
                },
            },
        };
        var filter = new FunctionFilter(config, _mockLogger.Object);
        var descriptor = CreateTestDescriptor("allowedFunc", "TestProvider");

        // Act
        var result = filter.ShouldFilterFunctionWithReason(descriptor, "allowedFunc");

        // Assert
        Assert.False(result.IsFiltered);
        Assert.Equal(FilterRuleType.None, result.RuleType);
        Assert.Contains("passed", result.Reason);
    }

    #endregion

    #region Global Block List Tests

    [Fact]
    public void ShouldFilterFunctionWithReason_WithGlobalBlockList_FiltersMatchingFunction()
    {
        // Arrange
        var config = new FunctionFilterConfig
        {
            EnableFiltering = true,
            GlobalBlockedFunctions = new List<string> { "globalBlocked", "anotherBlocked" },
        };
        var filter = new FunctionFilter(config, _mockLogger.Object);
        var descriptor = CreateTestDescriptor("globalBlocked");

        // Act
        var result = filter.ShouldFilterFunctionWithReason(descriptor, "globalBlocked");

        // Assert
        Assert.True(result.IsFiltered);
        Assert.Equal(FilterRuleType.GlobalBlockList, result.RuleType);
        Assert.Equal("globalBlocked", result.MatchedPattern);
        Assert.Contains("global", result.Reason);
        Assert.Contains("deny list", result.Reason);
    }

    [Fact]
    public void ShouldFilterFunctionWithReason_WithGlobalBlockList_ChecksProviderPrefixedPattern()
    {
        // Arrange
        var config = new FunctionFilterConfig
        {
            EnableFiltering = true,
            GlobalBlockedFunctions = new List<string> { "TestProvider__*" },
        };
        var filter = new FunctionFilter(config, _mockLogger.Object);
        var descriptor = CreateTestDescriptor("anyFunction", "TestProvider");

        // Act
        var result = filter.ShouldFilterFunctionWithReason(descriptor, "prefixed_anyFunction");

        // Assert
        Assert.True(result.IsFiltered);
        Assert.Equal(FilterRuleType.GlobalBlockList, result.RuleType);
        Assert.Equal("TestProvider__*", result.MatchedPattern);
    }

    #endregion

    #region Global Allow List Tests

    [Fact]
    public void ShouldFilterFunctionWithReason_WithGlobalAllowList_FiltersNonMatchingFunction()
    {
        // Arrange
        var config = new FunctionFilterConfig
        {
            EnableFiltering = true,
            GlobalAllowedFunctions = new List<string> { "globalAllowed", "anotherAllowed" },
        };
        var filter = new FunctionFilter(config, _mockLogger.Object);
        var descriptor = CreateTestDescriptor("notAllowed");

        // Act
        var result = filter.ShouldFilterFunctionWithReason(descriptor, "notAllowed");

        // Assert
        Assert.True(result.IsFiltered);
        Assert.Equal(FilterRuleType.GlobalAllowList, result.RuleType);
        Assert.Contains("not in", result.Reason);
        Assert.Contains("global allow list", result.Reason);
    }

    [Fact]
    public void ShouldFilterFunctionWithReason_WithGlobalAllowList_AllowsMatchingFunction()
    {
        // Arrange
        var config = new FunctionFilterConfig
        {
            EnableFiltering = true,
            GlobalAllowedFunctions = new List<string> { "globalAllowed", "anotherAllowed" },
        };
        var filter = new FunctionFilter(config, _mockLogger.Object);
        var descriptor = CreateTestDescriptor("globalAllowed");

        // Act
        var result = filter.ShouldFilterFunctionWithReason(descriptor, "globalAllowed");

        // Assert
        Assert.False(result.IsFiltered);
        Assert.Equal(FilterRuleType.None, result.RuleType);
        Assert.Contains("passed", result.Reason);
    }

    #endregion

    #region Priority Order Tests

    [Fact]
    public void ShouldFilterFunctionWithReason_WithConflictingRules_ProviderBlockTakesPriority()
    {
        // Arrange
        var config = new FunctionFilterConfig
        {
            EnableFiltering = true,
            GlobalAllowedFunctions = new List<string> { "testFunc" },
            ProviderConfigs = new Dictionary<string, ProviderFilterConfig>
            {
                ["TestProvider"] = new ProviderFilterConfig
                {
                    Enabled = true,
                    BlockedFunctions = new List<string> { "testFunc" },
                },
            },
        };
        var filter = new FunctionFilter(config, _mockLogger.Object);
        var descriptor = CreateTestDescriptor("testFunc", "TestProvider");

        // Act
        var result = filter.ShouldFilterFunctionWithReason(descriptor, "testFunc");

        // Assert
        Assert.True(result.IsFiltered);
        Assert.Equal(FilterRuleType.ProviderBlockList, result.RuleType);
    }

    [Fact]
    public void ShouldFilterFunctionWithReason_WithConflictingRules_ProviderAllowOverridesGlobalBlock()
    {
        // Arrange
        var config = new FunctionFilterConfig
        {
            EnableFiltering = true,
            GlobalBlockedFunctions = new List<string> { "testFunc" },
            ProviderConfigs = new Dictionary<string, ProviderFilterConfig>
            {
                ["TestProvider"] = new ProviderFilterConfig
                {
                    Enabled = true,
                    AllowedFunctions = new List<string> { "testFunc" },
                },
            },
        };
        var filter = new FunctionFilter(config, _mockLogger.Object);
        var descriptor = CreateTestDescriptor("testFunc", "TestProvider");

        // Act
        var result = filter.ShouldFilterFunctionWithReason(descriptor, "testFunc");

        // Assert
        // Provider allow list passes, so we check global block list next
        Assert.True(result.IsFiltered);
        Assert.Equal(FilterRuleType.GlobalBlockList, result.RuleType);
    }

    #endregion

    #region Wildcard Pattern Tests

    [Theory]
    [InlineData("test*", "testFunction", true)]
    [InlineData("test*", "testingStuff", true)]
    [InlineData("test*", "notest", false)]
    [InlineData("*test", "endtest", true)]
    [InlineData("*test", "teststart", false)]
    [InlineData("*test*", "containstestword", true)]
    [InlineData("*test*", "notmatching", false)]
    [InlineData("*", "anything", true)]
    [InlineData("exact", "exact", true)]
    [InlineData("exact", "Exact", true)] // Case insensitive
    [InlineData("exact", "notexact", false)]
    public void MatchesPattern_VariousPatterns_ReturnsExpectedResult(
        string pattern,
        string text,
        bool expectedMatch
    )
    {
        // Arrange
        var config = new FunctionFilterConfig
        {
            EnableFiltering = true,
            GlobalBlockedFunctions = new List<string> { pattern },
        };
        var filter = new FunctionFilter(config, _mockLogger.Object);
        var descriptor = CreateTestDescriptor(text);

        // Act
        var result = filter.ShouldFilterFunctionWithReason(descriptor, text);

        // Assert
        Assert.Equal(expectedMatch, result.IsFiltered);
        if (expectedMatch)
        {
            Assert.Equal(pattern, result.MatchedPattern);
        }
    }

    #endregion

    #region FilterFunctions Collection Tests

    [Fact]
    public void FilterFunctions_WithMixedFunctions_FiltersCorrectly()
    {
        // Arrange
        var config = new FunctionFilterConfig
        {
            EnableFiltering = true,
            GlobalBlockedFunctions = new List<string> { "blocked*" },
            GlobalAllowedFunctions = new List<string> { "*", "!blockedSpecial" },
        };
        var filter = new FunctionFilter(config, _mockLogger.Object);

        var descriptors = new[]
        {
            CreateTestDescriptor("allowedFunc"),
            CreateTestDescriptor("blockedFunc"),
            CreateTestDescriptor("blockedSpecial"),
            CreateTestDescriptor("anotherAllowed"),
        };

        // Act
        var filtered = filter.FilterFunctions(descriptors).ToList();

        // Assert
        Assert.Equal(2, filtered.Count);
        Assert.Contains(filtered, d => d.Contract.Name == "allowedFunc");
        Assert.Contains(filtered, d => d.Contract.Name == "anotherAllowed");
        Assert.DoesNotContain(filtered, d => d.Contract.Name.StartsWith("blocked"));
    }

    [Fact]
    public void FilterFunctions_WithNamingMap_UsesRegisteredNames()
    {
        // Arrange
        var config = new FunctionFilterConfig
        {
            EnableFiltering = true,
            GlobalBlockedFunctions = new List<string> { "prefixed-*" },
        };
        var filter = new FunctionFilter(config, _mockLogger.Object);

        var descriptor = CreateTestDescriptor("originalName");
        var descriptors = new[] { descriptor };
        var namingMap = new Dictionary<string, string>
        {
            [descriptor.Key] = "prefixed-originalName",
        };

        // Act
        var filtered = filter.FilterFunctions(descriptors, namingMap).ToList();

        // Assert
        Assert.Empty(filtered);
    }

    #endregion

    #region Edge Cases and Error Handling

    [Fact]
    public void ShouldFilterFunctionWithReason_WithEmptyLists_AllowsAllFunctions()
    {
        // Arrange
        var config = new FunctionFilterConfig
        {
            EnableFiltering = true,
            GlobalBlockedFunctions = new List<string>(),
            GlobalAllowedFunctions = new List<string>(),
        };
        var filter = new FunctionFilter(config, _mockLogger.Object);
        var descriptor = CreateTestDescriptor("anyFunction");

        // Act
        var result = filter.ShouldFilterFunctionWithReason(descriptor, "anyFunction");

        // Assert
        Assert.False(result.IsFiltered);
        Assert.Contains("passed", result.Reason);
    }

    [Fact]
    public void ShouldFilterFunctionWithReason_WithNullProviderName_HandlesGracefully()
    {
        // Arrange
        var config = new FunctionFilterConfig
        {
            EnableFiltering = true,
            GlobalBlockedFunctions = new List<string> { "blocked" },
        };
        var filter = new FunctionFilter(config, _mockLogger.Object);
        var descriptor = new FunctionDescriptor
        {
            Contract = new FunctionContract { Name = "blocked" },
            Handler = _ => Task.FromResult("result"),
            ProviderName = string.Empty,
        };

        // Act
        var result = filter.ShouldFilterFunctionWithReason(descriptor, "blocked");

        // Assert
        Assert.True(result.IsFiltered);
        Assert.Equal(FilterRuleType.GlobalBlockList, result.RuleType);
    }

    [Fact]
    public void ShouldFilterFunctionWithReason_WithComplexConfiguration_AppliesAllRulesCorrectly()
    {
        // Arrange
        var config = new FunctionFilterConfig
        {
            EnableFiltering = true,
            GlobalBlockedFunctions = new List<string> { "*debug*" },
            GlobalAllowedFunctions = new List<string> { "get*", "list*", "create*" },
            ProviderConfigs = new Dictionary<string, ProviderFilterConfig>
            {
                ["Provider1"] = new ProviderFilterConfig
                {
                    Enabled = true,
                    BlockedFunctions = new List<string> { "createDangerous" },
                    AllowedFunctions = new List<string> { "get*", "list*", "create*", "special" },
                },
                ["Provider2"] = new ProviderFilterConfig { Enabled = false },
            },
        };
        var filter = new FunctionFilter(config, _mockLogger.Object);

        // Test various scenarios
        var testCases = new[]
        {
            (CreateTestDescriptor("getUser", "Provider1"), "getUser", false),
            (CreateTestDescriptor("createDangerous", "Provider1"), "createDangerous", true),
            (CreateTestDescriptor("special", "Provider1"), "special", false),
            (CreateTestDescriptor("debugFunction", "Provider1"), "debugFunction", true),
            (CreateTestDescriptor("anything", "Provider2"), "anything", true),
            (CreateTestDescriptor("unauthorized", "Provider3"), "unauthorized", true),
        };

        // Act & Assert
        foreach (var (descriptor, registeredName, expectedFiltered) in testCases)
        {
            var result = filter.ShouldFilterFunctionWithReason(descriptor, registeredName);
            Assert.Equal(expectedFiltered, result.IsFiltered);
        }
    }

    #endregion

    #region Obsolete Method Tests

    [Fact]
    public void ShouldFilterFunction_ObsoleteMethod_StillWorks()
    {
        // Arrange
        var config = new FunctionFilterConfig
        {
            EnableFiltering = true,
            GlobalBlockedFunctions = new List<string> { "blocked" },
        };
        var filter = new FunctionFilter(config, _mockLogger.Object);
        var descriptor = CreateTestDescriptor("blocked");

        // Act
#pragma warning disable CS0618 // Type or member is obsolete
        var isFiltered = filter.ShouldFilterFunction(descriptor, "blocked");
#pragma warning restore CS0618

        // Assert
        Assert.True(isFiltered);
    }

    #endregion
}
