using AchieveAi.LmDotnetTools.CopilotSdkProvider.Agents;

namespace AchieveAi.LmDotnetTools.CopilotSdkProvider.Tests.Agents;

public class CopilotVersionCheckerTests
{
    [Theory]
    [InlineData("copilot 0.0.410", "0.0.410")]
    [InlineData("Copilot CLI 1.2.3", "1.2.3")]
    [InlineData("version: 10.20.30", "10.20.30")]
    [InlineData("some prefix 3.4.5 some suffix", "3.4.5")]
    public void ExtractVersion_ValidInput_ReturnsVersion(string input, string expected)
    {
        CopilotVersionChecker.ExtractVersion(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no version here")]
    [InlineData("1.2")]
    public void ExtractVersion_InvalidInput_ReturnsNull(string? input)
    {
        CopilotVersionChecker.ExtractVersion(input!).Should().BeNull();
    }

    [Fact]
    public void ParseVersion_ValidString_ReturnsThreeParts()
    {
        var parts = CopilotVersionChecker.ParseVersion("1.2.3");
        parts.Should().Equal(1, 2, 3);
    }

    [Fact]
    public void ParseVersion_LargeNumbers_ParsesCorrectly()
    {
        var parts = CopilotVersionChecker.ParseVersion("100.200.300");
        parts.Should().Equal(100, 200, 300);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.2")]
    public void ParseVersion_InvalidString_Throws(string? input)
    {
        var act = () => CopilotVersionChecker.ParseVersion(input!);
        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("2.0.0", "1.0.0", 1)]
    [InlineData("1.0.0", "2.0.0", -1)]
    [InlineData("0.0.410", "0.0.410", 0)]
    [InlineData("0.0.411", "0.0.410", 1)]
    [InlineData("0.0.409", "0.0.410", -1)]
    [InlineData("1.2.0", "1.1.0", 1)]
    [InlineData("1.1.0", "1.2.0", -1)]
    public void CompareVersion_ReturnsCorrectOrdering(
        string left, string right, int expectedSign)
    {
        var result = CopilotVersionChecker.CompareVersion(left, right);
        result.Should().Be(expectedSign);
    }
}
