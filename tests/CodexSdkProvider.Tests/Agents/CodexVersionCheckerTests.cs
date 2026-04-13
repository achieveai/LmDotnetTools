using AchieveAi.LmDotnetTools.CodexSdkProvider.Agents;

namespace AchieveAi.LmDotnetTools.CodexSdkProvider.Tests.Agents;

public class CodexVersionCheckerTests
{
    [Theory]
    [InlineData("codex 1.2.3", "1.2.3")]
    [InlineData("Codex CLI 0.9.1", "0.9.1")]
    [InlineData("version: 10.20.30", "10.20.30")]
    [InlineData("some prefix 3.4.5 some suffix", "3.4.5")]
    public void ExtractVersion_ValidInput_ReturnsVersion(string input, string expected)
    {
        CodexVersionChecker.ExtractVersion(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no version here")]
    [InlineData("1.2")]
    public void ExtractVersion_InvalidInput_ReturnsNull(string? input)
    {
        CodexVersionChecker.ExtractVersion(input!).Should().BeNull();
    }

    [Fact]
    public void ParseVersion_ValidString_ReturnsThreeParts()
    {
        var parts = CodexVersionChecker.ParseVersion("1.2.3");
        parts.Should().Equal(1, 2, 3);
    }

    [Fact]
    public void ParseVersion_LargeNumbers_ParsesCorrectly()
    {
        var parts = CodexVersionChecker.ParseVersion("100.200.300");
        parts.Should().Equal(100, 200, 300);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.2")]
    public void ParseVersion_InvalidString_Throws(string? input)
    {
        var act = () => CodexVersionChecker.ParseVersion(input!);
        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("2.0.0", "1.0.0", 1)]
    [InlineData("1.0.0", "2.0.0", -1)]
    [InlineData("1.2.0", "1.1.0", 1)]
    [InlineData("1.1.0", "1.2.0", -1)]
    [InlineData("1.0.2", "1.0.1", 1)]
    [InlineData("1.0.1", "1.0.2", -1)]
    [InlineData("0.9.1", "0.9.0", 1)]
    public void CompareVersion_ReturnsCorrectOrdering(
        string left, string right, int expectedSign)
    {
        var result = CodexVersionChecker.CompareVersion(left, right);
        result.Should().Be(expectedSign);
    }
}
