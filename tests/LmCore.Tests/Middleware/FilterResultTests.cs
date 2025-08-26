using AchieveAi.LmDotnetTools.LmCore.Middleware;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Middleware;

public class FilterResultTests
{
    [Fact]
    public void Include_CreatesIncludedResult()
    {
        // Act
        var result = FilterResult.Include();

        // Assert
        Assert.False(result.IsFiltered);
        Assert.Equal("Function passed all filters", result.Reason);
        Assert.Equal(FilterRuleType.None, result.RuleType);
        Assert.Null(result.MatchedPattern);
    }

    [Fact]
    public void Include_WithCustomReason_CreatesIncludedResult()
    {
        // Arrange
        var customReason = "Custom inclusion reason";

        // Act
        var result = FilterResult.Include(customReason);

        // Assert
        Assert.False(result.IsFiltered);
        Assert.Equal(customReason, result.Reason);
        Assert.Equal(FilterRuleType.None, result.RuleType);
        Assert.Null(result.MatchedPattern);
    }

    [Fact]
    public void FilteredByDisabledProvider_CreatesFilteredResult()
    {
        // Arrange
        var providerName = "TestProvider";

        // Act
        var result = FilterResult.FilteredByDisabledProvider(providerName);

        // Assert
        Assert.True(result.IsFiltered);
        Assert.Equal("Provider 'TestProvider' is disabled", result.Reason);
        Assert.Equal(FilterRuleType.ProviderDisabled, result.RuleType);
        Assert.Null(result.MatchedPattern);
    }

    [Fact]
    public void FilteredByProviderBlockList_CreatesFilteredResult()
    {
        // Arrange
        var providerName = "TestProvider";
        var pattern = "dangerous_*";

        // Act
        var result = FilterResult.FilteredByProviderBlockList(providerName, pattern);

        // Assert
        Assert.True(result.IsFiltered);
        Assert.Equal(
            "Function blocked by provider 'TestProvider' deny list pattern: dangerous_*",
            result.Reason
        );
        Assert.Equal(FilterRuleType.ProviderBlockList, result.RuleType);
        Assert.Equal(pattern, result.MatchedPattern);
    }

    [Fact]
    public void FilteredByProviderAllowList_CreatesFilteredResult()
    {
        // Arrange
        var providerName = "TestProvider";

        // Act
        var result = FilterResult.FilteredByProviderAllowList(providerName);

        // Assert
        Assert.True(result.IsFiltered);
        Assert.Equal("Function not in provider 'TestProvider' allow list", result.Reason);
        Assert.Equal(FilterRuleType.ProviderAllowList, result.RuleType);
        Assert.Null(result.MatchedPattern);
    }

    [Fact]
    public void FilteredByGlobalBlockList_CreatesFilteredResult()
    {
        // Arrange
        var pattern = "unsafe_*";

        // Act
        var result = FilterResult.FilteredByGlobalBlockList(pattern);

        // Assert
        Assert.True(result.IsFiltered);
        Assert.Equal("Function blocked by global deny list pattern: unsafe_*", result.Reason);
        Assert.Equal(FilterRuleType.GlobalBlockList, result.RuleType);
        Assert.Equal(pattern, result.MatchedPattern);
    }

    [Fact]
    public void FilteredByGlobalAllowList_CreatesFilteredResult()
    {
        // Act
        var result = FilterResult.FilteredByGlobalAllowList();

        // Assert
        Assert.True(result.IsFiltered);
        Assert.Equal("Function not in global allow list", result.Reason);
        Assert.Equal(FilterRuleType.GlobalAllowList, result.RuleType);
        Assert.Null(result.MatchedPattern);
    }

    [Fact]
    public void FilteringDisabled_CreatesIncludedResult()
    {
        // Act
        var result = FilterResult.FilteringDisabled();

        // Assert
        Assert.False(result.IsFiltered);
        Assert.Equal("Function filtering is disabled", result.Reason);
        Assert.Equal(FilterRuleType.None, result.RuleType);
        Assert.Null(result.MatchedPattern);
    }

    [Fact]
    public void ToString_ForIncludedResult_FormatsCorrectly()
    {
        // Arrange
        var result = FilterResult.Include("Test reason");

        // Act
        var str = result.ToString();

        // Assert
        Assert.Equal("Included: Test reason (Rule: None)", str);
    }

    [Fact]
    public void ToString_ForFilteredResult_FormatsCorrectly()
    {
        // Arrange
        var result = FilterResult.FilteredByDisabledProvider("TestProvider");

        // Act
        var str = result.ToString();

        // Assert
        Assert.Equal("Filtered: Provider 'TestProvider' is disabled (Rule: ProviderDisabled)", str);
    }

    [Fact]
    public void ToString_WithMatchedPattern_IncludesPattern()
    {
        // Arrange
        var result = FilterResult.FilteredByProviderBlockList("TestProvider", "danger*");

        // Act
        var str = result.ToString();

        // Assert
        Assert.Equal(
            "Filtered: Function blocked by provider 'TestProvider' deny list pattern: danger* (Rule: ProviderBlockList, Pattern: danger*)",
            str
        );
    }

    [Theory]
    [InlineData(FilterRuleType.None, false)]
    [InlineData(FilterRuleType.ProviderDisabled, true)]
    [InlineData(FilterRuleType.ProviderBlockList, true)]
    [InlineData(FilterRuleType.ProviderAllowList, true)]
    [InlineData(FilterRuleType.GlobalBlockList, true)]
    [InlineData(FilterRuleType.GlobalAllowList, true)]
    public void FilterRuleType_CorrectlyIndicatesFiltering(
        FilterRuleType ruleType,
        bool expectedFiltered
    )
    {
        // Arrange & Act
        FilterResult result = ruleType switch
        {
            FilterRuleType.None => FilterResult.Include(),
            FilterRuleType.ProviderDisabled => FilterResult.FilteredByDisabledProvider("test"),
            FilterRuleType.ProviderBlockList => FilterResult.FilteredByProviderBlockList(
                "test",
                "*"
            ),
            FilterRuleType.ProviderAllowList => FilterResult.FilteredByProviderAllowList("test"),
            FilterRuleType.GlobalBlockList => FilterResult.FilteredByGlobalBlockList("*"),
            FilterRuleType.GlobalAllowList => FilterResult.FilteredByGlobalAllowList(),
            _ => FilterResult.Include(),
        };

        // Assert
        Assert.Equal(expectedFiltered, result.IsFiltered);
        Assert.Equal(ruleType, result.RuleType);
    }
}
