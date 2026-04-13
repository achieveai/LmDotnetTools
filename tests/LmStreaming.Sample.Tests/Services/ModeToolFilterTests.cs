using AchieveAi.LmDotnetTools.AnthropicProvider.Models;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

public class ModeToolFilterTests
{
    [Fact]
    public void FilterBuiltInTools_ReturnsAll_WhenEnabledToolsIsNull()
    {
        var allTools = new List<object>
        {
            new AnthropicWebSearchTool(),
        };

        var filtered = ModeToolFilter.FilterBuiltInTools(allTools, enabledTools: null);

        filtered.Should().NotBeNull();
        var nonNullFiltered = Assert.IsAssignableFrom<List<object>>(filtered);
        nonNullFiltered.Should().HaveCount(1);
        nonNullFiltered.OfType<AnthropicBuiltInTool>().Select(t => t.Name).Should().Contain("web_search");
    }

    [Fact]
    public void FilterBuiltInTools_ReturnsNull_WhenEnabledToolsExcludesBuiltIns()
    {
        var allTools = new List<object>
        {
            new AnthropicWebSearchTool(),
        };

        var filtered = ModeToolFilter.FilterBuiltInTools(allTools, ["calculate"]);

        filtered.Should().BeNull();
    }

    [Fact]
    public void FilterBuiltInTools_ReturnsMatchingBuiltIns_WhenEnabledToolsIncludesBuiltIn()
    {
        var allTools = new List<object>
        {
            new AnthropicWebSearchTool(),
        };

        var filtered = ModeToolFilter.FilterBuiltInTools(allTools, ["calculate", "web_search"]);

        filtered.Should().NotBeNull();
        var nonNullFiltered = Assert.IsAssignableFrom<List<object>>(filtered);
        nonNullFiltered.Should().HaveCount(1);
        nonNullFiltered.OfType<AnthropicBuiltInTool>().Select(t => t.Name).Should().Contain("web_search");
    }
}
