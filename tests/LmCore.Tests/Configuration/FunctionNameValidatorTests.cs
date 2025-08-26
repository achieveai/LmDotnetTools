using AchieveAi.LmDotnetTools.LmCore.Configuration;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Configuration;

public class FunctionNameValidatorTests
{
    [Theory]
    [InlineData("validFunction")]
    [InlineData("valid_function")]
    [InlineData("valid-function")]
    [InlineData("validFunction123")]
    [InlineData("VALID_FUNCTION")]
    [InlineData("a")]
    [InlineData("A1")]
    [InlineData("function_with_numbers_123")]
    public void IsValidFunctionName_WithValidNames_ReturnsTrue(string functionName)
    {
        // Act
        var result = FunctionNameValidator.IsValidFunctionName(functionName);

        // Assert
        Assert.True(result, $"Function name '{functionName}' should be valid");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    [InlineData("invalid function")] // Contains space
    [InlineData("invalid.function")] // Contains dot
    [InlineData("invalid@function")] // Contains @
    [InlineData("invalid#function")] // Contains #
    [InlineData("invalid$function")] // Contains $
    [InlineData("invalid%function")] // Contains %
    [InlineData("invalid^function")] // Contains ^
    [InlineData("invalid&function")] // Contains &
    [InlineData("invalid*function")] // Contains *
    [InlineData("invalid(function")] // Contains (
    [InlineData("invalid)function")] // Contains )
    [InlineData("invalid+function")] // Contains +
    [InlineData("invalid=function")] // Contains =
    [InlineData("invalid[function")] // Contains [
    [InlineData("invalid]function")] // Contains ]
    [InlineData("invalid{function")] // Contains {
    [InlineData("invalid}function")] // Contains }
    [InlineData("invalid|function")] // Contains |
    [InlineData("invalid\\function")] // Contains backslash
    [InlineData("invalid/function")] // Contains forward slash
    [InlineData("invalid:function")] // Contains colon
    [InlineData("invalid;function")] // Contains semicolon
    [InlineData("invalid\"function")] // Contains quote
    [InlineData("invalid'function")] // Contains single quote
    [InlineData("invalid<function")] // Contains <
    [InlineData("invalid>function")] // Contains >
    [InlineData("invalid,function")] // Contains comma
    [InlineData("invalid?function")] // Contains question mark
    [InlineData("invalid`function")] // Contains backtick
    [InlineData("invalid~function")] // Contains tilde
    [InlineData("this_is_a_really_long_function_name_that_exceeds_the_maximum_allowed_length_of_64_characters")] // Too long
    public void IsValidFunctionName_WithInvalidNames_ReturnsFalse(string? functionName)
    {
        // Act
        var result = FunctionNameValidator.IsValidFunctionName(functionName);

        // Assert
        Assert.False(result, $"Function name '{functionName}' should be invalid");
    }

    [Theory]
    [InlineData("validPrefix")]
    [InlineData("valid_prefix")]
    [InlineData("valid-prefix")]
    [InlineData("prefix123")]
    [InlineData("MCP")]
    [InlineData("github")]
    [InlineData("weather_api")]
    [InlineData("task-manager")]
    public void IsValidPrefix_WithValidPrefixes_ReturnsTrue(string prefix)
    {
        // Act
        var result = FunctionNameValidator.IsValidPrefix(prefix);

        // Assert
        Assert.True(result, $"Prefix '{prefix}' should be valid");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    [InlineData("invalid prefix")] // Contains space
    [InlineData("invalid.prefix")] // Contains dot
    [InlineData("invalid@prefix")] // Contains @
    [InlineData("this_is_a_really_long_prefix_that_exceeds_32")] // Too long (> 32 chars)
    public void IsValidPrefix_WithInvalidPrefixes_ReturnsFalse(string? prefix)
    {
        // Act
        var result = FunctionNameValidator.IsValidPrefix(prefix);

        // Assert
        Assert.False(result, $"Prefix '{prefix}' should be invalid");
    }

    [Fact]
    public void GetFunctionNameValidationError_WithNullName_ReturnsAppropriateMessage()
    {
        // Act
        var error = FunctionNameValidator.GetFunctionNameValidationError(null);

        // Assert
        Assert.Equal("Function name cannot be null or empty.", error);
    }

    [Fact]
    public void GetFunctionNameValidationError_WithEmptyName_ReturnsAppropriateMessage()
    {
        // Act
        var error = FunctionNameValidator.GetFunctionNameValidationError("");

        // Assert
        Assert.Equal("Function name cannot be null or empty.", error);
    }

    [Fact]
    public void GetFunctionNameValidationError_WithTooLongName_ReturnsAppropriateMessage()
    {
        // Arrange
        var longName = new string('a', 65);

        // Act
        var error = FunctionNameValidator.GetFunctionNameValidationError(longName);

        // Assert
        Assert.Contains("exceeds maximum length of 64 characters", error);
    }

    [Fact]
    public void GetFunctionNameValidationError_WithInvalidCharacters_ReturnsAppropriateMessage()
    {
        // Act
        var error = FunctionNameValidator.GetFunctionNameValidationError("invalid@function");

        // Assert
        Assert.Contains("contains invalid characters", error);
        Assert.Contains("Only letters (a-z, A-Z), numbers (0-9), underscores (_), and hyphens (-) are allowed", error);
    }

    [Fact]
    public void GetPrefixValidationError_WithNullPrefix_ReturnsAppropriateMessage()
    {
        // Act
        var error = FunctionNameValidator.GetPrefixValidationError(null);

        // Assert
        Assert.Equal("Prefix cannot be null or empty.", error);
    }

    [Fact]
    public void GetPrefixValidationError_WithEmptyPrefix_ReturnsAppropriateMessage()
    {
        // Act
        var error = FunctionNameValidator.GetPrefixValidationError("");

        // Assert
        Assert.Equal("Prefix cannot be null or empty.", error);
    }

    [Fact]
    public void GetPrefixValidationError_WithTooLongPrefix_ReturnsAppropriateMessage()
    {
        // Arrange
        var longPrefix = new string('a', 33);

        // Act
        var error = FunctionNameValidator.GetPrefixValidationError(longPrefix);

        // Assert
        Assert.Contains("exceeds recommended maximum length of 32 characters", error);
        Assert.Contains("total limit is 64 characters", error);
    }

    [Fact]
    public void GetPrefixValidationError_WithInvalidCharacters_ReturnsAppropriateMessage()
    {
        // Act
        var error = FunctionNameValidator.GetPrefixValidationError("invalid@prefix");

        // Assert
        Assert.Contains("contains invalid characters", error);
        Assert.Contains("Only letters (a-z, A-Z), numbers (0-9), underscores (_), and hyphens (-) are allowed", error);
    }

    [Theory]
    [InlineData("mcp", "tool", "__", true)]
    [InlineData("github", "search", "__", true)]
    [InlineData("weather", "get_current", "__", true)]
    [InlineData("task", "create", "_", true)]
    [InlineData("a", "b", "__", true)]
    [InlineData("very_long_prefix", "very_long_function_name_that_when_combined_exceeds_limit", "__", false)]
    [InlineData("invalid prefix", "function", "__", false)]
    [InlineData("prefix", "invalid function", "__", false)]
    public void IsValidPrefixedFunctionName_TestVariousCombinations(string prefix, string functionName, string separator, bool expectedValid)
    {
        // Act
        var result = FunctionNameValidator.IsValidPrefixedFunctionName(prefix, functionName, separator);

        // Assert
        Assert.Equal(expectedValid, result);
    }
}