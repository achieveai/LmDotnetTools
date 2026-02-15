using System.Diagnostics;
using LmEmbeddings.Models;
using Xunit;

namespace LmEmbeddings.Tests.Models;

/// <summary>
///     Tests for EmbeddingApiType enum and related functionality
/// </summary>
public class EmbeddingApiTypeTests
{
    public static IEnumerable<object[]> ApiTypeTestCases =>
        [
            [EmbeddingApiType.Default, 0, "OpenAI-compatible default format"],
            [EmbeddingApiType.Jina, 1, "Jina AI specific format"],
        ];

    public static IEnumerable<object[]> ApiTypeStringTestCases =>
        [
            [EmbeddingApiType.Default, "Default"],
            [EmbeddingApiType.Jina, "Jina"],
        ];

    public static IEnumerable<object[]> ApiTypeParsingTestCases =>
        [
            // Valid cases
            ["Default", EmbeddingApiType.Default, true],
            ["default", EmbeddingApiType.Default, true],
            ["DEFAULT", EmbeddingApiType.Default, true],
            ["Jina", EmbeddingApiType.Jina, true],
            ["jina", EmbeddingApiType.Jina, true],
            ["JINA", EmbeddingApiType.Jina, true],
            ["0", EmbeddingApiType.Default, true],
            ["1", EmbeddingApiType.Jina, true],
            // Invalid cases
            ["OpenAI", EmbeddingApiType.Default, false],
            ["Invalid", EmbeddingApiType.Default, false],
            ["", EmbeddingApiType.Default, false],
            ["2", EmbeddingApiType.Default, false],
            ["-1", EmbeddingApiType.Default, false],
        ];

    public static IEnumerable<object[]> ApiTypeCompatibilityTestCases =>
        [
            // OpenAI compatibility
            [EmbeddingApiType.Default, "OpenAI", true],
            [EmbeddingApiType.Jina, "OpenAI", false],
            // Jina compatibility
            [EmbeddingApiType.Default, "Jina", false],
            [EmbeddingApiType.Jina, "Jina", true],
            // Default checks
            [EmbeddingApiType.Default, "Default", true],
            [EmbeddingApiType.Jina, "Default", false],
        ];

    [Theory]
    [MemberData(nameof(ApiTypeTestCases))]
    public void EmbeddingApiType_Values_ShouldHaveCorrectValues(
        EmbeddingApiType apiType,
        int expectedValue,
        string description
    )
    {
        Debug.WriteLine($"Testing API type: {apiType} = {expectedValue} ({description})");

        // Act & Assert
        Assert.Equal(expectedValue, (int)apiType);
        Debug.WriteLine($"✓ API type {apiType} has correct value {expectedValue}");
    }

    [Theory]
    [MemberData(nameof(ApiTypeStringTestCases))]
    public void EmbeddingApiType_ToString_ShouldReturnCorrectString(EmbeddingApiType apiType, string expectedString)
    {
        Debug.WriteLine($"Testing API type string representation: {apiType}");

        // Act
        var result = apiType.ToString();

        // Assert
        Assert.Equal(expectedString, result);
        Debug.WriteLine($"✓ API type {apiType} string representation: '{result}'");
    }

    [Theory]
    [MemberData(nameof(ApiTypeParsingTestCases))]
    public void EmbeddingApiType_Parse_ShouldParseCorrectly(string input, EmbeddingApiType expected, bool shouldSucceed)
    {
        Debug.WriteLine($"Testing API type parsing: '{input}' -> {expected} (should succeed: {shouldSucceed})");

        // Act & Assert
        if (shouldSucceed)
        {
            var success = Enum.TryParse<EmbeddingApiType>(input, true, out var result);
            Assert.True(success);
            Assert.Equal(expected, result);
            Debug.WriteLine($"✓ Successfully parsed '{input}' to {result}");
        }
        else
        {
            var success = Enum.TryParse<EmbeddingApiType>(input, true, out var result);

            // For numeric values outside the defined range, TryParse succeeds but we should check if it's a defined value
            if (success && int.TryParse(input, out var numericValue))
            {
                var isDefined = Enum.IsDefined(typeof(EmbeddingApiType), numericValue);
                Assert.False(isDefined, $"Numeric value {input} should not be a defined enum value");
                Debug.WriteLine($"✓ Correctly identified '{input}' as undefined enum value");
            }
            else
            {
                Assert.False(success);
                Debug.WriteLine($"✓ Correctly failed to parse '{input}'");
            }
        }
    }

    [Fact]
    public void EmbeddingApiType_DefaultValue_ShouldBeDefault()
    {
        Debug.WriteLine("Testing default value of EmbeddingApiType");

        // Act
        var defaultValue = default(EmbeddingApiType);

        // Assert
        Assert.Equal(EmbeddingApiType.Default, defaultValue);
        Assert.Equal(0, (int)defaultValue);
        Debug.WriteLine($"✓ Default value is {defaultValue} (0)");
    }

    [Fact]
    public void EmbeddingApiType_AllValues_ShouldBeUnique()
    {
        Debug.WriteLine("Testing that all EmbeddingApiType values are unique");

        // Arrange
        var allValues = Enum.GetValues<EmbeddingApiType>();
        var intValues = allValues.Select(v => (int)v).ToArray();

        Debug.WriteLine($"All API type values: {string.Join(", ", allValues.Select(v => $"{v}({(int)v})"))}");

        // Act & Assert
        var uniqueValues = intValues.Distinct().ToArray();
        Assert.Equal(intValues.Length, uniqueValues.Length);
        Debug.WriteLine($"✓ All {intValues.Length} values are unique");
    }

    [Theory]
    [MemberData(nameof(ApiTypeCompatibilityTestCases))]
    public void EmbeddingApiType_Compatibility_ShouldWorkWithDifferentScenarios(
        EmbeddingApiType apiType,
        string scenario,
        bool expectedCompatibility
    )
    {
        Debug.WriteLine($"Testing API type compatibility: {apiType} with {scenario}");

        // Act & Assert based on scenario
        switch (scenario)
        {
            case "OpenAI":
                var isOpenAICompatible = apiType == EmbeddingApiType.Default;
                Assert.Equal(expectedCompatibility, isOpenAICompatible);
                Debug.WriteLine($"✓ {apiType} OpenAI compatibility: {isOpenAICompatible}");
                break;

            case "Jina":
                var isJinaCompatible = apiType == EmbeddingApiType.Jina;
                Assert.Equal(expectedCompatibility, isJinaCompatible);
                Debug.WriteLine($"✓ {apiType} Jina compatibility: {isJinaCompatible}");
                break;

            case "Default":
                var isDefault = apiType == EmbeddingApiType.Default;
                Assert.Equal(expectedCompatibility, isDefault);
                Debug.WriteLine($"✓ {apiType} is default: {isDefault}");
                break;

            default:
                throw new NotSupportedException($"Unknown scenario: {scenario}");
        }
    }
}
