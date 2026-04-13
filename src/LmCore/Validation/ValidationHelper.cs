using System.Runtime.CompilerServices;

namespace AchieveAi.LmDotnetTools.LmCore.Validation;

/// <summary>
///     Provides standardized validation methods for consistent error handling across all LmDotnetTools libraries
///     Extracted from LmEmbeddings and enhanced for OpenAI, Anthropic, and other provider implementations
/// </summary>
public static class ValidationHelper
{
    /// <summary>
    ///     Validates that a string parameter is not null, empty, or whitespace
    /// </summary>
    /// <param name="value">The string value to validate</param>
    /// <param name="parameterName">The name of the parameter being validated</param>
    /// <exception cref="ArgumentException">Thrown when the value is null, empty, or whitespace</exception>
    public static void ValidateNotNullOrWhiteSpace(
        string? value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null
    )
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be null, empty, or whitespace", parameterName);
        }
    }

    /// <summary>
    ///     Validates that an object parameter is not null
    /// </summary>
    /// <typeparam name="T">The type of the object</typeparam>
    /// <param name="value">The object to validate</param>
    /// <param name="parameterName">The name of the parameter being validated</param>
    /// <exception cref="ArgumentNullException">Thrown when the value is null</exception>
    public static void ValidateNotNull<T>(
        T? value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null
    )
        where T : class
    {
        if (value == null)
        {
            throw new ArgumentNullException(parameterName);
        }
    }

    /// <summary>
    ///     Validates that a collection is not null or empty
    /// </summary>
    /// <typeparam name="T">The type of elements in the collection</typeparam>
    /// <param name="collection">The collection to validate</param>
    /// <param name="parameterName">The name of the parameter being validated</param>
    /// <exception cref="ArgumentNullException">Thrown when the collection is null</exception>
    /// <exception cref="ArgumentException">Thrown when the collection is empty</exception>
    public static void ValidateNotNullOrEmpty<T>(
        IEnumerable<T>? collection,
        [CallerArgumentExpression(nameof(collection))] string? parameterName = null
    )
    {
        if (collection == null)
        {
            throw new ArgumentNullException(parameterName);
        }

        if (!collection.Any())
        {
            throw new ArgumentException("Collection cannot be empty", parameterName);
        }
    }

    /// <summary>
    ///     Validates that a collection contains no null or empty string elements
    /// </summary>
    /// <param name="collection">The collection of strings to validate</param>
    /// <param name="parameterName">The name of the parameter being validated</param>
    /// <exception cref="ArgumentException">Thrown when any element is null or empty</exception>
    public static void ValidateStringCollectionElements(
        IEnumerable<string>? collection,
        [CallerArgumentExpression(nameof(collection))] string? parameterName = null
    )
    {
        ValidateNotNullOrEmpty(collection, parameterName);

        if (collection!.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Collection cannot contain null, empty, or whitespace elements", parameterName);
        }
    }

    /// <summary>
    ///     Validates that a numeric value is positive (greater than zero)
    /// </summary>
    /// <param name="value">The numeric value to validate</param>
    /// <param name="parameterName">The name of the parameter being validated</param>
    /// <exception cref="ArgumentException">Thrown when the value is not positive</exception>
    public static void ValidatePositive(
        int value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null
    )
    {
        if (value <= 0)
        {
            throw new ArgumentException("Value must be positive", parameterName);
        }
    }

    /// <summary>
    ///     Validates that a numeric value is positive (greater than zero)
    /// </summary>
    /// <param name="value">The numeric value to validate</param>
    /// <param name="parameterName">The name of the parameter being validated</param>
    /// <exception cref="ArgumentException">Thrown when the value is not positive</exception>
    public static void ValidatePositive(
        double value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null
    )
    {
        if (value <= 0)
        {
            throw new ArgumentException("Value must be positive", parameterName);
        }
    }

    /// <summary>
    ///     Validates that a numeric value is non-negative (greater than or equal to zero)
    /// </summary>
    /// <param name="value">The numeric value to validate</param>
    /// <param name="parameterName">The name of the parameter being validated</param>
    /// <exception cref="ArgumentException">Thrown when the value is negative</exception>
    public static void ValidateNonNegative(
        int value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null
    )
    {
        if (value < 0)
        {
            throw new ArgumentException("Value cannot be negative", parameterName);
        }
    }

    /// <summary>
    ///     Validates that a value is within a specified range (inclusive)
    /// </summary>
    /// <param name="value">The value to validate</param>
    /// <param name="min">The minimum allowed value (inclusive)</param>
    /// <param name="max">The maximum allowed value (inclusive)</param>
    /// <param name="parameterName">The name of the parameter being validated</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is outside the specified range</exception>
    public static void ValidateRange(
        int value,
        int min,
        int max,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null
    )
    {
        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"Value must be between {min} and {max} (inclusive)"
            );
        }
    }

    /// <summary>
    ///     Validates that a value is within a specified range (inclusive)
    /// </summary>
    /// <param name="value">The value to validate</param>
    /// <param name="min">The minimum allowed value (inclusive)</param>
    /// <param name="max">The maximum allowed value (inclusive)</param>
    /// <param name="parameterName">The name of the parameter being validated</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is outside the specified range</exception>
    public static void ValidateRange(
        double value,
        double min,
        double max,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null
    )
    {
        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"Value must be between {min} and {max} (inclusive)"
            );
        }
    }

    /// <summary>
    ///     Validates that an enum value is defined
    /// </summary>
    /// <typeparam name="TEnum">The enum type</typeparam>
    /// <param name="value">The enum value to validate</param>
    /// <param name="parameterName">The name of the parameter being validated</param>
    /// <exception cref="ArgumentException">Thrown when the enum value is not defined</exception>
    public static void ValidateEnumDefined<TEnum>(
        TEnum value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null
    )
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentException($"Invalid {typeof(TEnum).Name} value: {value}", parameterName);
        }
    }

    /// <summary>
    ///     Validates that a string matches one of the allowed values (case-insensitive)
    /// </summary>
    /// <param name="value">The string value to validate</param>
    /// <param name="allowedValues">The collection of allowed values</param>
    /// <param name="parameterName">The name of the parameter being validated</param>
    /// <exception cref="ArgumentException">Thrown when the value is not in the allowed values</exception>
    public static void ValidateAllowedValues(
        string? value,
        IEnumerable<string> allowedValues,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null
    )
    {
        ValidateNotNullOrWhiteSpace(value, parameterName);
        ValidateNotNullOrEmpty(allowedValues, nameof(allowedValues));

        var allowedList = allowedValues.ToList();
        if (!allowedList.Contains(value!, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Invalid value '{value}'. Allowed values: {string.Join(", ", allowedList)}",
                parameterName
            );
        }
    }

    /// <summary>
    ///     Validates that an object is not disposed by checking a disposed flag
    /// </summary>
    /// <param name="disposed">The disposed flag to check</param>
    /// <param name="objectName">The name of the object being checked</param>
    /// <exception cref="ObjectDisposedException">Thrown when the object is disposed</exception>
    public static void ValidateNotDisposed(bool disposed, string? objectName = null)
    {
        if (disposed)
        {
            throw new ObjectDisposedException(objectName ?? "Object");
        }
    }

    /// <summary>
    ///     Validates API key format and throws ArgumentException if invalid.
    ///     Added for provider-specific validation needs.
    /// </summary>
    /// <param name="apiKey">The API key to validate</param>
    /// <param name="parameterName">The parameter name for exception</param>
    public static void ValidateApiKey(
        string? apiKey,
        [CallerArgumentExpression(nameof(apiKey))] string? parameterName = null
    )
    {
        ValidateNotNullOrWhiteSpace(apiKey, parameterName);

        if (apiKey!.Length < 10)
        {
            throw new ArgumentException("API key appears to be too short", parameterName);
        }
    }

    /// <summary>
    ///     Validates base URL format for provider requests.
    ///     Added for provider-specific validation needs.
    /// </summary>
    /// <param name="baseUrl">The base URL to validate</param>
    /// <param name="parameterName">The parameter name for exception</param>
    public static void ValidateBaseUrl(
        string? baseUrl,
        [CallerArgumentExpression(nameof(baseUrl))] string? parameterName = null
    )
    {
        ValidateNotNullOrWhiteSpace(baseUrl, parameterName);

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            throw new ArgumentException("Base URL must be a valid HTTP or HTTPS URL", parameterName);
        }
    }

    /// <summary>
    ///     Validates chat messages array for provider requests.
    ///     Added for provider-specific validation needs.
    /// </summary>
    /// <param name="messages">The messages to validate</param>
    /// <param name="parameterName">The parameter name for exception</param>
    public static void ValidateMessages<T>(
        IEnumerable<T>? messages,
        [CallerArgumentExpression(nameof(messages))] string? parameterName = null
    )
        where T : class
    {
        ValidateNotNullOrEmpty(messages, parameterName);

        if (!messages!.Any())
        {
            throw new ArgumentException("At least one message is required", parameterName);
        }
    }

    /// <summary>
    ///     Creates a standardized validation exception for API-specific parameters
    /// </summary>
    /// <param name="apiType">The API type being validated</param>
    /// <param name="parameterName">The name of the invalid parameter</param>
    /// <param name="parameterValue">The invalid parameter value</param>
    /// <param name="allowedValues">The allowed values for this parameter</param>
    /// <returns>A standardized ArgumentException</returns>
    public static ArgumentException CreateApiValidationException(
        string apiType,
        string parameterName,
        string? parameterValue,
        IEnumerable<string> allowedValues
    )
    {
        var allowedList = string.Join(", ", allowedValues);
        return new ArgumentException(
            $"Invalid {parameterName} for {apiType} API: '{parameterValue}'. Valid values: {allowedList}",
            parameterName
        );
    }

    /// <summary>
    ///     Validates an embedding request object using reflection for common requirements.
    ///     This method works with any object that has Model, Inputs, and optionally Dimensions properties.
    /// </summary>
    /// <param name="request">The embedding request to validate</param>
    /// <exception cref="ArgumentNullException">Thrown when request is null</exception>
    /// <exception cref="ArgumentException">Thrown when request properties are invalid</exception>
    public static void ValidateEmbeddingRequest(object? request)
    {
        ValidateNotNull(request);

        var requestType = request!.GetType();

        // Validate Model property
        var modelProperty = requestType.GetProperty("Model");
        if (modelProperty != null)
        {
            var modelValue = modelProperty.GetValue(request) as string;
            ValidateNotNullOrWhiteSpace(modelValue, "Model");
        }

        // Validate Inputs property
        var inputsProperty = requestType.GetProperty("Inputs");
        if (inputsProperty != null)
        {
            var inputsValue = inputsProperty.GetValue(request) as IEnumerable<string>;
            ValidateStringCollectionElements(inputsValue, "Inputs");
        }

        // Validate Dimensions property if present and has value
        var dimensionsProperty = requestType.GetProperty("Dimensions");
        if (dimensionsProperty != null)
        {
            var dimensionsValue = dimensionsProperty.GetValue(request);
            if (dimensionsValue is int dimensions)
            {
                ValidatePositive(dimensions, "Dimensions");
            }
            else if (
                dimensionsValue != null
                && dimensionsValue.GetType().IsGenericType
                && dimensionsValue.GetType().GetGenericTypeDefinition() == typeof(Nullable<>)
                && dimensionsValue.GetType().GetGenericArguments()[0] == typeof(int)
            )
            {
                var nullableInt = (int?)dimensionsValue;
                if (nullableInt.HasValue)
                {
                    ValidatePositive(nullableInt.Value, "Dimensions");
                }
            }
        }
    }

    /// <summary>
    ///     Validates a rerank request object using reflection for common requirements.
    ///     This method works with any object that has Query, Model, Documents, and optionally TopN properties.
    /// </summary>
    /// <param name="request">The rerank request to validate</param>
    /// <exception cref="ArgumentNullException">Thrown when request is null</exception>
    /// <exception cref="ArgumentException">Thrown when request properties are invalid</exception>
    public static void ValidateRerankRequest(object? request)
    {
        ValidateNotNull(request);

        var requestType = request!.GetType();

        // Validate Query property
        var queryProperty = requestType.GetProperty("Query");
        if (queryProperty != null)
        {
            var queryValue = queryProperty.GetValue(request) as string;
            ValidateNotNullOrWhiteSpace(queryValue, "Query");
        }

        // Validate Model property
        var modelProperty = requestType.GetProperty("Model");
        if (modelProperty != null)
        {
            var modelValue = modelProperty.GetValue(request) as string;
            ValidateNotNullOrWhiteSpace(modelValue, "Model");
        }

        // Validate Documents property
        var documentsProperty = requestType.GetProperty("Documents");
        if (documentsProperty != null)
        {
            var documentsValue = documentsProperty.GetValue(request) as IEnumerable<string>;
            ValidateStringCollectionElements(documentsValue, "Documents");
        }

        // Validate TopN property if present and has value
        var topNProperty = requestType.GetProperty("TopN");
        if (topNProperty != null)
        {
            var topNValue = topNProperty.GetValue(request);
            if (topNValue is int topN)
            {
                ValidatePositive(topN, "TopN");
            }
            else if (
                topNValue != null
                && topNValue.GetType().IsGenericType
                && topNValue.GetType().GetGenericTypeDefinition() == typeof(Nullable<>)
                && topNValue.GetType().GetGenericArguments()[0] == typeof(int)
            )
            {
                var nullableInt = (int?)topNValue;
                if (nullableInt.HasValue)
                {
                    ValidatePositive(nullableInt.Value, "TopN");
                }
            }
        }
    }
}
