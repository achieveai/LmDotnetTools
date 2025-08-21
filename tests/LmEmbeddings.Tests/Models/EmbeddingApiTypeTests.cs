using AchieveAi.LmDotnetTools.LmEmbeddings.Models;
using LmEmbeddings.Models;
using System.Diagnostics;
using Xunit;

namespace LmEmbeddings.Tests.Models;

/// <summary>
/// Tests for EmbeddingApiType enum and related functionality
/// </summary>
public class EmbeddingApiTypeTests
{
    [Theory]
    [MemberData(nameof(ApiTypeTestCases))]
    public void EmbeddingApiType_Values_ShouldHaveCorrectValues(EmbeddingApiType apiType, int expectedValue, string description)
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
        bool expectedCompatibility)
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
        }
    }

    public static IEnumerable<object[]> ApiTypeTestCases => new List<object[]>
    {
        new object[] { EmbeddingApiType.Default, 0, "OpenAI-compatible default format" },
        new object[] { EmbeddingApiType.Jina, 1, "Jina AI specific format" }
    };

    public static IEnumerable<object[]> ApiTypeStringTestCases => new List<object[]>
    {
        new object[] { EmbeddingApiType.Default, "Default" },
        new object[] { EmbeddingApiType.Jina, "Jina" }
    };

    public static IEnumerable<object[]> ApiTypeParsingTestCases => new List<object[]>
    {
        // Valid cases
        new object[] { "Default", EmbeddingApiType.Default, true },
        new object[] { "default", EmbeddingApiType.Default, true },
        new object[] { "DEFAULT", EmbeddingApiType.Default, true },
        new object[] { "Jina", EmbeddingApiType.Jina, true },
        new object[] { "jina", EmbeddingApiType.Jina, true },
        new object[] { "JINA", EmbeddingApiType.Jina, true },
        new object[] { "0", EmbeddingApiType.Default, true },
        new object[] { "1", EmbeddingApiType.Jina, true },
        
        // Invalid cases
        new object[] { "OpenAI", EmbeddingApiType.Default, false },
        new object[] { "Invalid", EmbeddingApiType.Default, false },
        new object[] { "", EmbeddingApiType.Default, false },
        new object[] { "2", EmbeddingApiType.Default, false },
        new object[] { "-1", EmbeddingApiType.Default, false }
    };

    public static IEnumerable<object[]> ApiTypeCompatibilityTestCases => new List<object[]>
    {
        // OpenAI compatibility
        new object[] { EmbeddingApiType.Default, "OpenAI", true },
        new object[] { EmbeddingApiType.Jina, "OpenAI", false },
        
        // Jina compatibility
        new object[] { EmbeddingApiType.Default, "Jina", false },
        new object[] { EmbeddingApiType.Jina, "Jina", true },
        
        // Default checks
        new object[] { EmbeddingApiType.Default, "Default", true },
        new object[] { EmbeddingApiType.Jina, "Default", false }
    };
}