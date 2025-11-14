using System.Collections.Immutable;
using System.Diagnostics;
using AchieveAi.LmDotnetTools.LmCore.Validation;
using AchieveAi.LmDotnetTools.LmEmbeddings.Models;
using AchieveAi.LmDotnetTools.LmTestUtils;
using LmEmbeddings.Models;
using LmEmbeddings.Tests.TestUtilities;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LmEmbeddings.Tests.Core.Utils;

/// <summary>
/// Tests for ValidationHelper utility class ensuring consistent error handling patterns
/// </summary>
public class ValidationHelperTests
{
    private readonly ILogger<ValidationHelperTests> _logger;

    public ValidationHelperTests()
    {
        _logger = TestLoggerFactory.CreateLogger<ValidationHelperTests>();
    }

    #region ValidateNotNullOrWhiteSpace Tests

    [Theory]
    [MemberData(nameof(ValidStringTestCases))]
    public void ValidateNotNullOrWhiteSpace_ValidStrings_DoesNotThrow(string value, string description)
    {
        Debug.WriteLine($"Testing ValidateNotNullOrWhiteSpace with valid string: {description}");
        Debug.WriteLine($"Value: '{value}'");

        // Act & Assert
        var exception = Record.Exception(() => ValidationHelper.ValidateNotNullOrWhiteSpace(value));

        Assert.Null(exception);
        Debug.WriteLine("✓ ValidateNotNullOrWhiteSpace passed for valid string");
    }

    [Theory]
    [MemberData(nameof(InvalidStringTestCases))]
    public void ValidateNotNullOrWhiteSpace_InvalidStrings_ThrowsArgumentException(string? value, string description)
    {
        Debug.WriteLine($"Testing ValidateNotNullOrWhiteSpace with invalid string: {description}");
        Debug.WriteLine($"Value: '{value}'");

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => ValidationHelper.ValidateNotNullOrWhiteSpace(value));

        Assert.Contains("cannot be null, empty, or whitespace", exception.Message);
        Debug.WriteLine($"✓ ValidateNotNullOrWhiteSpace correctly threw ArgumentException: {exception.Message}");
    }

    #endregion

    #region ValidateNotNull Tests

    [Theory]
    [MemberData(nameof(ValidObjectTestCases))]
    public void ValidateNotNull_ValidObjects_DoesNotThrow(object value, string description)
    {
        Debug.WriteLine($"Testing ValidateNotNull with valid object: {description}");
        Debug.WriteLine($"Object type: {value?.GetType().Name ?? "null"}");

        // Act & Assert
        var exception = Record.Exception(() => ValidationHelper.ValidateNotNull(value));

        Assert.Null(exception);
        Debug.WriteLine("✓ ValidateNotNull passed for valid object");
    }

    [Fact]
    public void ValidateNotNull_NullObject_ThrowsArgumentNullException()
    {
        Debug.WriteLine("Testing ValidateNotNull with null object");

        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() => ValidationHelper.ValidateNotNull<object>(null));

        Debug.WriteLine($"✓ ValidateNotNull correctly threw ArgumentNullException: {exception.Message}");
    }

    #endregion

    #region ValidateNotNullOrEmpty Tests

    [Theory]
    [MemberData(nameof(ValidCollectionTestCases))]
    public void ValidateNotNullOrEmpty_ValidCollections_DoesNotThrow(
        IEnumerable<string> collection,
        int expectedCount,
        string description
    )
    {
        Debug.WriteLine($"Testing ValidateNotNullOrEmpty with valid collection: {description}");
        Debug.WriteLine($"Collection count: {expectedCount}");

        // Act & Assert
        var exception = Record.Exception(() => ValidationHelper.ValidateNotNullOrEmpty(collection));

        Assert.Null(exception);
        Debug.WriteLine("✓ ValidateNotNullOrEmpty passed for valid collection");
    }

    [Theory]
    [MemberData(nameof(InvalidCollectionTestCases))]
    public void ValidateNotNullOrEmpty_InvalidCollections_ThrowsException(
        IEnumerable<string>? collection,
        Type expectedExceptionType,
        string description
    )
    {
        Debug.WriteLine($"Testing ValidateNotNullOrEmpty with invalid collection: {description}");
        Debug.WriteLine($"Expected exception: {expectedExceptionType.Name}");

        // Act & Assert
        var exception = Assert.Throws(expectedExceptionType, () => ValidationHelper.ValidateNotNullOrEmpty(collection));

        Debug.WriteLine($"✓ ValidateNotNullOrEmpty correctly threw {expectedExceptionType.Name}: {exception.Message}");
    }

    #endregion

    #region ValidateStringCollectionElements Tests

    [Theory]
    [MemberData(nameof(ValidStringCollectionTestCases))]
    public void ValidateStringCollectionElements_ValidCollections_DoesNotThrow(
        IEnumerable<string> collection,
        string description
    )
    {
        Debug.WriteLine($"Testing ValidateStringCollectionElements with valid collection: {description}");
        Debug.WriteLine($"Collection: [{string.Join(", ", collection.Select(s => $"'{s}'"))}]");

        // Act & Assert
        var exception = Record.Exception(() => ValidationHelper.ValidateStringCollectionElements(collection));

        Assert.Null(exception);
        Debug.WriteLine("✓ ValidateStringCollectionElements passed for valid collection");
    }

    [Theory]
    [MemberData(nameof(InvalidStringCollectionTestCases))]
    public void ValidateStringCollectionElements_InvalidCollections_ThrowsArgumentException(
        IEnumerable<string>? collection,
        string description
    )
    {
        Debug.WriteLine($"Testing ValidateStringCollectionElements with invalid collection: {description}");

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() =>
            ValidationHelper.ValidateStringCollectionElements(collection)
        );

        Debug.WriteLine($"✓ ValidateStringCollectionElements correctly threw ArgumentException: {exception.Message}");
    }

    #endregion

    #region ValidatePositive Tests

    [Theory]
    [MemberData(nameof(PositiveNumberTestCases))]
    public void ValidatePositive_PositiveNumbers_DoesNotThrow(int value, string description)
    {
        Debug.WriteLine($"Testing ValidatePositive with positive number: {description}");
        Debug.WriteLine($"Value: {value}");

        // Act & Assert
        var exception = Record.Exception(() => ValidationHelper.ValidatePositive(value));

        Assert.Null(exception);
        Debug.WriteLine("✓ ValidatePositive passed for positive number");
    }

    [Theory]
    [MemberData(nameof(NonPositiveNumberTestCases))]
    public void ValidatePositive_NonPositiveNumbers_ThrowsArgumentException(int value, string description)
    {
        Debug.WriteLine($"Testing ValidatePositive with non-positive number: {description}");
        Debug.WriteLine($"Value: {value}");

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => ValidationHelper.ValidatePositive(value));

        Assert.Contains("must be positive", exception.Message);
        Debug.WriteLine($"✓ ValidatePositive correctly threw ArgumentException: {exception.Message}");
    }

    #endregion

    #region ValidateRange Tests

    [Theory]
    [MemberData(nameof(ValidRangeTestCases))]
    public void ValidateRange_ValuesInRange_DoesNotThrow(int value, int min, int max, string description)
    {
        Debug.WriteLine($"Testing ValidateRange with value in range: {description}");
        Debug.WriteLine($"Value: {value}, Range: [{min}, {max}]");

        // Act & Assert
        var exception = Record.Exception(() => ValidationHelper.ValidateRange(value, min, max));

        Assert.Null(exception);
        Debug.WriteLine("✓ ValidateRange passed for value in range");
    }

    [Theory]
    [MemberData(nameof(InvalidRangeTestCases))]
    public void ValidateRange_ValuesOutOfRange_ThrowsArgumentOutOfRangeException(
        int value,
        int min,
        int max,
        string description
    )
    {
        Debug.WriteLine($"Testing ValidateRange with value out of range: {description}");
        Debug.WriteLine($"Value: {value}, Range: [{min}, {max}]");

        // Act & Assert
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            ValidationHelper.ValidateRange(value, min, max)
        );

        Debug.WriteLine($"✓ ValidateRange correctly threw ArgumentOutOfRangeException: {exception.Message}");
    }

    #endregion

    #region ValidateEnumDefined Tests

    [Theory]
    [MemberData(nameof(ValidEnumTestCases))]
    public void ValidateEnumDefined_ValidEnumValues_DoesNotThrow(EmbeddingApiType value, string description)
    {
        Debug.WriteLine($"Testing ValidateEnumDefined with valid enum: {description}");
        Debug.WriteLine($"Enum value: {value}");

        // Act & Assert
        var exception = Record.Exception(() => ValidationHelper.ValidateEnumDefined(value));

        Assert.Null(exception);
        Debug.WriteLine("✓ ValidateEnumDefined passed for valid enum value");
    }

    [Theory]
    [MemberData(nameof(InvalidEnumTestCases))]
    public void ValidateEnumDefined_InvalidEnumValues_ThrowsArgumentException(
        EmbeddingApiType value,
        string description
    )
    {
        Debug.WriteLine($"Testing ValidateEnumDefined with invalid enum: {description}");
        Debug.WriteLine($"Enum value: {value} ({(int)value})");

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => ValidationHelper.ValidateEnumDefined(value));

        Assert.Contains("Invalid EmbeddingApiType value", exception.Message);
        Debug.WriteLine($"✓ ValidateEnumDefined correctly threw ArgumentException: {exception.Message}");
    }

    #endregion

    #region ValidateAllowedValues Tests

    [Theory]
    [MemberData(nameof(ValidAllowedValuesTestCases))]
    public void ValidateAllowedValues_ValidValues_DoesNotThrow(string value, string[] allowedValues, string description)
    {
        Debug.WriteLine($"Testing ValidateAllowedValues with valid value: {description}");
        Debug.WriteLine($"Value: '{value}', Allowed: [{string.Join(", ", allowedValues)}]");

        // Act & Assert
        var exception = Record.Exception(() => ValidationHelper.ValidateAllowedValues(value, allowedValues));

        Assert.Null(exception);
        Debug.WriteLine("✓ ValidateAllowedValues passed for valid value");
    }

    [Theory]
    [MemberData(nameof(InvalidAllowedValuesTestCases))]
    public void ValidateAllowedValues_InvalidValues_ThrowsArgumentException(
        string? value,
        string[] allowedValues,
        string description
    )
    {
        Debug.WriteLine($"Testing ValidateAllowedValues with invalid value: {description}");
        Debug.WriteLine($"Value: '{value}', Allowed: [{string.Join(", ", allowedValues)}]");

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() =>
            ValidationHelper.ValidateAllowedValues(value, allowedValues)
        );

        Debug.WriteLine($"✓ ValidateAllowedValues correctly threw ArgumentException: {exception.Message}");
    }

    #endregion

    #region ValidateEmbeddingRequest Tests

    [Theory]
    [MemberData(nameof(ValidEmbeddingRequestTestCases))]
    public void ValidateEmbeddingRequest_ValidRequests_DoesNotThrow(EmbeddingRequest request, string description)
    {
        Debug.WriteLine($"Testing ValidateEmbeddingRequest with valid request: {description}");
        Debug.WriteLine($"Model: {request.Model}, Inputs: {request.Inputs.Count}");

        // Act & Assert
        var exception = Record.Exception(() => ValidationHelper.ValidateEmbeddingRequest(request));

        Assert.Null(exception);
        Debug.WriteLine("✓ ValidateEmbeddingRequest passed for valid request");
    }

    [Theory]
    [MemberData(nameof(InvalidEmbeddingRequestTestCases))]
    public void ValidateEmbeddingRequest_InvalidRequests_ThrowsException(
        EmbeddingRequest? request,
        Type expectedExceptionType,
        string description
    )
    {
        Debug.WriteLine($"Testing ValidateEmbeddingRequest with invalid request: {description}");
        Debug.WriteLine($"Expected exception: {expectedExceptionType.Name}");

        // Act & Assert
        var exception = Assert.Throws(expectedExceptionType, () => ValidationHelper.ValidateEmbeddingRequest(request));

        Debug.WriteLine(
            $"✓ ValidateEmbeddingRequest correctly threw {expectedExceptionType.Name}: {exception.Message}"
        );
    }

    #endregion

    #region Test Data

    public static IEnumerable<object[]> ValidStringTestCases =>
        new List<object[]>
        {
            new object[] { "valid string", "Non-empty string" },
            new object[] { "a", "Single character" },
            new object[] { "  text  ", "String with surrounding whitespace" },
            new object[] { "123", "Numeric string" },
            new object[] { "special!@#$%^&*()", "String with special characters" },
        };

    public static IEnumerable<object[]> InvalidStringTestCases =>
        new List<object[]>
        {
            new object[] { null!, "Null string" },
            new object[] { "", "Empty string" },
            new object[] { "   ", "Whitespace only string" },
            new object[] { "\t\n\r", "Tab and newline characters" },
        };

    public static IEnumerable<object[]> ValidObjectTestCases =>
        new List<object[]>
        {
            new object[] { "string object", "String object" },
            new object[] { 42, "Integer object" },
            new object[] { new List<string>(), "Empty list object" },
            new object[] { DateTime.Now, "DateTime object" },
        };

    public static IEnumerable<object[]> ValidCollectionTestCases =>
        new List<object[]>
        {
            new object[] { item, 1, "Single item collection" },
            new object[] { itemArray, 3, "Multiple item collection" },
            new object[]
            {
                new List<string> { "test" },
                1,
                "List with one item",
            },
        };

    public static IEnumerable<object[]> InvalidCollectionTestCases =>
        new List<object[]>
        {
            new object[] { null!, typeof(ArgumentNullException), "Null collection" },
            new object[] { Array.Empty<string>(), typeof(ArgumentException), "Empty array" },
            new object[] { new List<string>(), typeof(ArgumentException), "Empty list" },
        };

    public static IEnumerable<object[]> ValidStringCollectionTestCases =>
        new List<object[]>
        {
            new object[] { itemArray0, "Collection with valid strings" },
            new object[] { itemArray1, "Single valid string" },
            new object[] { itemArray2, "Multiple single characters" },
        };

    public static IEnumerable<object[]> InvalidStringCollectionTestCases =>
        new List<object[]>
        {
            new object[] { new[] { "valid", null!, "valid" }, "Collection with null element" },
            new object[] { itemArray3, "Collection with empty element" },
            new object[] { itemArray4, "Collection with whitespace element" },
        };

    public static IEnumerable<object[]> PositiveNumberTestCases =>
        new List<object[]>
        {
            new object[] { 1, "Minimum positive integer" },
            new object[] { 100, "Standard positive integer" },
            new object[] { int.MaxValue, "Maximum positive integer" },
        };

    public static IEnumerable<object[]> NonPositiveNumberTestCases =>
        new List<object[]>
        {
            new object[] { 0, "Zero value" },
            new object[] { -1, "Negative integer" },
            new object[] { int.MinValue, "Minimum negative integer" },
        };

    public static IEnumerable<object[]> ValidRangeTestCases =>
        new List<object[]>
        {
            new object[] { 5, 1, 10, "Value in middle of range" },
            new object[] { 1, 1, 10, "Value at minimum boundary" },
            new object[] { 10, 1, 10, "Value at maximum boundary" },
            new object[] { 0, -5, 5, "Zero in negative to positive range" },
        };

    public static IEnumerable<object[]> InvalidRangeTestCases =>
        new List<object[]>
        {
            new object[] { 0, 1, 10, "Value below minimum" },
            new object[] { 11, 1, 10, "Value above maximum" },
            new object[] { -10, 0, 5, "Negative value in positive range" },
        };

    public static IEnumerable<object[]> ValidEnumTestCases =>
        new List<object[]>
        {
            new object[] { EmbeddingApiType.Default, "Default API type" },
            new object[] { EmbeddingApiType.Jina, "Jina API type" },
        };

    public static IEnumerable<object[]> InvalidEnumTestCases =>
        new List<object[]>
        {
            new object[] { (EmbeddingApiType)999, "Undefined enum value" },
            new object[] { (EmbeddingApiType)(-1), "Negative enum value" },
        };

    public static IEnumerable<object[]> ValidAllowedValuesTestCases =>
        new List<object[]>
        {
            new object[] { "float", itemArray5, "Exact match" },
            new object[] { "FLOAT", itemArray5, "Case insensitive match" },
            new object[] { "base64", itemArray6, "Match in multiple options" },
        };

    public static IEnumerable<object[]> InvalidAllowedValuesTestCases =>
        new List<object[]>
        {
            new object[] { "invalid", itemArray7, "Value not in allowed list" },
            new object[] { null!, itemArray7, "Null value" },
            new object[] { "", itemArray8, "Empty value" },
        };

    public static IEnumerable<object[]> ValidEmbeddingRequestTestCases =>
        new List<object[]>
        {
            new object[]
            {
                new EmbeddingRequest { Inputs = itemArray9, Model = "test-model" },
                "Basic valid request",
            },
            new object[]
            {
                new EmbeddingRequest
                {
                    Inputs = itemArray10,
                    Model = "test-model",
                    Dimensions = 512,
                },
                "Request with dimensions",
            },
        };

    public static IEnumerable<object[]> InvalidEmbeddingRequestTestCases =>
        new List<object[]>
        {
            new object[] { null!, typeof(ArgumentNullException), "Null request" },
            new object[]
            {
                new EmbeddingRequest { Inputs = itemArray11, Model = "" },
                typeof(ArgumentException),
                "Empty model",
            },
            new object[]
            {
                new EmbeddingRequest { Inputs = Array.Empty<string>(), Model = "test-model" },
                typeof(ArgumentException),
                "Empty inputs",
            },
        };

    private static readonly string[] item = new[] { "item1" };
    private static readonly string[] itemArray = new[] { "item1", "item2", "item3" };
    private static readonly string[] itemArray0 = new[] { "valid1", "valid2" };
    private static readonly string[] itemArray1 = new[] { "single" };
    private static readonly string[] itemArray2 = new[] { "a", "b", "c" };
    private static readonly string[] itemArray3 = new[] { "valid", "", "valid" };
    private static readonly string[] itemArray4 = new[] { "valid", "   ", "valid" };
    private static readonly string[] itemArray5 = new[] { "float", "base64" };
    private static readonly string[] itemArray6 = new[] { "float", "base64", "binary" };
    private static readonly string[] itemArray7 = new[] { "float", "base64" };
    private static readonly string[] itemArray8 = new[] { "float", "base64" };
    private static readonly string[] itemArray9 = new[] { "test input" };
    private static readonly string[] itemArray10 = new[] { "input1", "input2" };
    private static readonly string[] itemArray11 = new[] { "test" };

    #endregion
}
