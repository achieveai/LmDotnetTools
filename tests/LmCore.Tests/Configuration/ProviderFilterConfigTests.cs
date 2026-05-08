using System.Reflection;
using AchieveAi.LmDotnetTools.LmCore.Configuration;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Configuration;

public class ProviderFilterConfigTests
{
    [Fact]
    public void CustomPrefix_WithValidPrefix_SetsSuccessfully()
    {
        // Arrange
        var config = new ProviderFilterConfig
        {
            // Act
            CustomPrefix = "valid_prefix",
        };

        // Assert
        Assert.Equal("valid_prefix", config.CustomPrefix);
    }

    [Fact]
    public void CustomPrefix_WithNull_SetsSuccessfully()
    {
        // Arrange
        var config = new ProviderFilterConfig { CustomPrefix = "initial" };

        // Act
        config.CustomPrefix = null;

        // Assert
        Assert.Null(config.CustomPrefix);
    }

    [Theory]
    [InlineData("invalid prefix")] // Contains space
    [InlineData("invalid.prefix")] // Contains dot
    [InlineData("invalid@prefix")] // Contains @
    [InlineData("prefix#123")] // Contains #
    [InlineData("prefix$")] // Contains $
    [InlineData("prefix%")] // Contains %
    [InlineData("prefix^")] // Contains ^
    [InlineData("prefix&")] // Contains &
    [InlineData("prefix*")] // Contains *
    [InlineData("prefix(")] // Contains (
    [InlineData("prefix)")] // Contains )
    [InlineData("prefix+")] // Contains +
    [InlineData("prefix=")] // Contains =
    [InlineData("prefix[")] // Contains [
    [InlineData("prefix]")] // Contains ]
    [InlineData("prefix{")] // Contains {
    [InlineData("prefix}")] // Contains }
    [InlineData("prefix|")] // Contains |
    [InlineData("prefix\\")] // Contains backslash
    [InlineData("prefix/")] // Contains forward slash
    [InlineData("prefix:")] // Contains colon
    [InlineData("prefix;")] // Contains semicolon
    [InlineData("prefix\"")] // Contains quote
    [InlineData("prefix'")] // Contains single quote
    [InlineData("prefix<")] // Contains <
    [InlineData("prefix>")] // Contains >
    [InlineData("prefix,")] // Contains comma
    [InlineData("prefix?")] // Contains question mark
    [InlineData("prefix`")] // Contains backtick
    [InlineData("prefix~")] // Contains tilde
    public void CustomPrefix_WithInvalidCharacters_ThrowsArgumentException(string invalidPrefix)
    {
        // Arrange
        var config = new ProviderFilterConfig();

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => config.CustomPrefix = invalidPrefix);
        Assert.Equal("CustomPrefix", exception.ParamName);
        Assert.Contains("invalid characters", exception.Message);
    }

    [Fact]
    public void CustomPrefix_WithTooLongPrefix_ThrowsArgumentException()
    {
        // Arrange
        var config = new ProviderFilterConfig();
        var longPrefix = new string('a', 33); // 33 characters, exceeds recommended 32

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => config.CustomPrefix = longPrefix);
        Assert.Equal("CustomPrefix", exception.ParamName);
        Assert.Contains("exceeds recommended maximum length", exception.Message);
    }

    [Fact]
    public void CustomPrefix_WithEmptyString_ThrowsArgumentException()
    {
        // Arrange
        var config = new ProviderFilterConfig();

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => config.CustomPrefix = "");
        Assert.Equal("CustomPrefix", exception.ParamName);
        Assert.Contains("cannot be null or empty", exception.Message);
    }

    [Fact]
    public void CustomPrefix_WithWhitespace_ThrowsArgumentException()
    {
        // Arrange
        var config = new ProviderFilterConfig();

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => config.CustomPrefix = "   ");
        Assert.Equal("CustomPrefix", exception.ParamName);
        Assert.Contains("cannot be null or empty", exception.Message);
    }

    [Fact]
    public void Validate_WithValidConfiguration_DoesNotThrow()
    {
        // Arrange
        var config = new ProviderFilterConfig
        {
            Enabled = true,
            CustomPrefix = "valid_prefix",
            AllowedFunctions = ["func1", "func2"],
            BlockedFunctions = ["dangerous*"],
        };

        // Act & Assert (should not throw)
        config.Validate();
    }

    [Fact]
    public void Validate_WithInvalidCustomPrefix_ThrowsArgumentException()
    {
        // Arrange
        var config = new ProviderFilterConfig { Enabled = true };

        // Force set an invalid prefix using reflection (simulating deserialization)
        var field = typeof(ProviderFilterConfig).GetField(
            "_customPrefix",
            BindingFlags.NonPublic | BindingFlags.Instance
        );
        field?.SetValue(config, "invalid prefix");

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(config.Validate);
        Assert.Equal("CustomPrefix", exception.ParamName);
        Assert.Contains("invalid characters", exception.Message);
    }

    [Fact]
    public void Validate_WithNullCustomPrefix_DoesNotThrow()
    {
        // Arrange
        var config = new ProviderFilterConfig { Enabled = true, CustomPrefix = null };

        // Act & Assert (should not throw)
        config.Validate();
    }

    [Theory]
    [InlineData("mcp")]
    [InlineData("MCP")]
    [InlineData("github")]
    [InlineData("weather_api")]
    [InlineData("task-manager")]
    [InlineData("tool_123")]
    [InlineData("a")]
    [InlineData("A1")]
    [InlineData("_underscore")]
    [InlineData("-hyphen")]
    [InlineData("mix_of-chars_123")]
    public void CustomPrefix_WithVariousValidPrefixes_SetsSuccessfully(string validPrefix)
    {
        // Arrange
        var config = new ProviderFilterConfig
        {
            // Act
            CustomPrefix = validPrefix,
        };

        // Assert
        Assert.Equal(validPrefix, config.CustomPrefix);
    }

    [Fact]
    public void ProviderFilterConfig_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var config = new ProviderFilterConfig();

        // Assert
        Assert.True(config.Enabled);
        Assert.Null(config.CustomPrefix);
        Assert.Null(config.AllowedFunctions);
        Assert.Null(config.BlockedFunctions);
    }
}
