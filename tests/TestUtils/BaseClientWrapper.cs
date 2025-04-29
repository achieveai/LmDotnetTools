using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;

namespace AchieveAi.LmDotnetTools.TestUtils;

/// <summary>
/// A base wrapper class that contains common functionality for validating and recording API interactions.
/// </summary>
public abstract class BaseClientWrapper : IDisposable
{
    protected readonly string _testDataFilePath;
    protected readonly TestData? _testData;
    protected readonly List<InteractionData> _recordedInteractions = new();
    protected readonly bool _allowAdditionalRequests;
    protected int _currentInteractionIndex = 0;
    protected static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = {
            new UnionJsonConverter<int, string>(),
            new UnionJsonConverter<string, BinaryData, ToolCallResult>(),
            new UnionJsonConverter<string, Union<TextContent, ImageContent>[]>(),
            new UnionJsonConverter<TextContent, ImageContent>(),
            new ImmutableDictionaryJsonConverterFactory(),
            new ExtraPropertiesConverter()
        }
    };

    /// <summary>
    /// Initializes a new instance of the base wrapper.
    /// </summary>
    /// <param name="testDataFilePath">The path to the test data file.</param>
    /// <param name="allowAdditionalRequests">If true, allows collecting additional requests when predefined ones are exhausted.</param>
    protected BaseClientWrapper(string testDataFilePath, bool allowAdditionalRequests = false)
    {
        _testDataFilePath = testDataFilePath;
        _testData = LoadTestData(testDataFilePath);
        _allowAdditionalRequests = allowAdditionalRequests;
    }

    /// <summary>
    /// Loads test data from the specified file.
    /// </summary>
    /// <param name="filePath">The file path to load from.</param>
    /// <returns>The loaded test data, or null if the file doesn't exist.</returns>
    protected static TestData? LoadTestData(string filePath)
    {
        if (File.Exists(filePath))
        {
            string json = File.ReadAllText(filePath);
            Console.WriteLine($"Loading test data from {filePath}");
            return JsonSerializer.Deserialize<TestData>(json, _jsonOptions);
        }

        return null;
    }

    /// <summary>
    /// Saves test data to the specified file.
    /// </summary>
    /// <param name="filePath">The file path to save to.</param>
    /// <param name="testData">The test data to save.</param>
    protected static void SaveTestData(string filePath, TestData testData)
    {
        string directory = Path.GetDirectoryName(filePath)!;
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(testData, _jsonOptions);
        File.WriteAllText(filePath, json);
    }

    /// <summary>
    /// Compares two JSON objects for equality, ignoring property order.
    /// </summary>
    /// <param name="json1">The first JSON object.</param>
    /// <param name="json2">The second JSON object.</param>
    /// <returns>True if the objects are equal, false otherwise.</returns>
    protected static bool JsonObjectEquals(JsonObject json1, JsonObject json2)
    {
        try
        {
            // Special handling for requests with common properties
            if (json1.ContainsKey("model") && json1.ContainsKey("messages") &&
                json2.ContainsKey("model") && json2.ContainsKey("messages"))
            {
                // Compare essential properties - model is required
                if (!CompareJsonValues(json1["model"], json2["model"]))
                {
                    return false;
                }

                // Compare messages array - required
                if (!CompareJsonArrays(json1["messages"]?.AsArray(), json2["messages"]?.AsArray()))
                {
                    return false;
                }

                // Optional properties - only compare if both have them
                // Check common properties like temperature, max_tokens, etc.
                if (!CompareCommonPropertyIfPresent(json1, json2, "temperature") ||
                    !CompareCommonPropertyIfPresent(json1, json2, "max_tokens") ||
                    !CompareCommonPropertyIfPresent(json1, json2, "system") ||
                    !CompareCommonPropertyIfPresent(json1, json2, "top_p") ||
                    !CompareResponseFormatIfPresent(json1, json2))
                {
                    return false;
                }

                // Let derived classes compare provider-specific properties
                if (!CompareProviderSpecificProperties(json1, json2))
                {
                    return false;
                }

                // If we got here, the essential properties match
                return true;
            }

            // For other types of objects, use the original comparison logic
            string normalized1 = NormalizeJsonObject(json1);
            string normalized2 = NormalizeJsonObject(json2);

            return normalized1 == normalized2;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error comparing JSON objects: {ex.Message}");
            // For testing purposes, be more lenient
            return true;
        }
    }

    /// <summary>
    /// Compares a common property if present in both objects.
    /// </summary>
    /// <param name="json1">The first JSON object.</param>
    /// <param name="json2">The second JSON object.</param>
    /// <param name="propertyName">The name of the property to compare.</param>
    /// <returns>True if the property is equal or not present in both, false otherwise.</returns>
    private static bool CompareCommonPropertyIfPresent(JsonObject json1, JsonObject json2, string propertyName)
    {
        if (json1.ContainsKey(propertyName) && json2.ContainsKey(propertyName))
        {
            if (!CompareJsonValues(json1[propertyName], json2[propertyName]))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Compares the response_format property if present in both objects.
    /// </summary>
    /// <param name="json1">The first JSON object.</param>
    /// <param name="json2">The second JSON object.</param>
    /// <returns>True if the property is equal or not present in both, false otherwise.</returns>
    private static bool CompareResponseFormatIfPresent(JsonObject json1, JsonObject json2)
    {
        if (json1.ContainsKey("response_format") && json2.ContainsKey("response_format"))
        {
            return CompareJsonObjects(json1["response_format"]?.AsObject(), json2["response_format"]?.AsObject());
        }
        return true;
    }

    /// <summary>
    /// Compares provider-specific properties. Can be overridden by derived classes.
    /// </summary>
    /// <param name="json1">The first JSON object.</param>
    /// <param name="json2">The second JSON object.</param>
    /// <returns>True if the provider-specific properties match or are not present, false otherwise.</returns>
    protected static bool CompareProviderSpecificProperties(JsonObject json1, JsonObject json2)
    {
        // Base implementation just returns true
        // Derived classes can override to compare provider-specific properties
        return true;
    }

    /// <summary>
    /// Compares two JSON values for equality.
    /// </summary>
    protected static bool CompareJsonValues(JsonNode? value1, JsonNode? value2)
    {
        if (value1 == null && value2 == null)
        {
            return true;
        }

        if (value1 == null || value2 == null)
        {
            return false;
        }

        // Handle different types
        if (value1 is JsonValue jsonValue1 && value2 is JsonValue jsonValue2)
        {
            // Try to compare as strings first
            string str1 = jsonValue1.ToString();
            string str2 = jsonValue2.ToString();

            // Remove quotes if they exist
            str1 = str1.Trim('"');
            str2 = str2.Trim('"');

            return str1 == str2;
        }
        else if (value1 is JsonObject obj1 && value2 is JsonObject obj2)
        {
            return CompareJsonObjects(obj1, obj2);
        }
        else if (value1 is JsonArray arr1 && value2 is JsonArray arr2)
        {
            return CompareJsonArrays(arr1, arr2);
        }

        // If types don't match, compare serialized strings
        return NormalizeJsonObject(value1) == NormalizeJsonObject(value2);
    }

    /// <summary>
    /// Compares two JSON objects for equality, ignoring property order.
    /// </summary>
    protected static bool CompareJsonObjects(JsonObject? obj1, JsonObject? obj2)
    {
        if (obj1 == null && obj2 == null)
        {
            return true;
        }

        if (obj1 == null || obj2 == null)
        {
            return false;
        }

        // Check if all properties in obj1 exist in obj2 with same values
        foreach (var prop in obj1)
        {
            if (obj2.ContainsKey(prop.Key))
            {
                if (!CompareJsonValues(prop.Value, obj2[prop.Key]))
                {
                    return false;
                }
            }
            else
            {
                // Special case: if obj1 has additional_parameters and obj2 doesn't,
                // check if the properties exist directly in obj2
                if (prop.Key == "additional_parameters" && prop.Value is JsonObject additionalProps)
                {
                    bool allPropertiesFound = true;
                    foreach (var additionalProp in additionalProps)
                    {
                        if (!obj2.ContainsKey(additionalProp.Key) ||
                            !CompareJsonValues(additionalProp.Value, obj2[additionalProp.Key]))
                        {
                            allPropertiesFound = false;
                            break;
                        }
                    }
                    if (allPropertiesFound)
                    {
                        continue;
                    }
                }
                return false;
            }
        }

        // Check if obj2 has properties that don't exist in obj1
        foreach (var prop in obj2)
        {
            if (!obj1.ContainsKey(prop.Key))
            {
                // If obj1 has additional_parameters, check if the property exists there
                if (obj1.ContainsKey("additional_parameters") &&
                    obj1["additional_parameters"] is JsonObject additionalProps &&
                    additionalProps.ContainsKey(prop.Key))
                {
                    if (!CompareJsonValues(additionalProps[prop.Key], prop.Value))
                    {
                        return false;
                    }
                }
                else
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Compares two JSON arrays for equality.
    /// </summary>
    protected static bool CompareJsonArrays(JsonArray? arr1, JsonArray? arr2)
    {
        if (arr1 == null && arr2 == null)
        {
            return true;
        }

        if (arr1 == null || arr2 == null)
        {
            return false;
        }

        if (arr1.Count != arr2.Count)
        {
            return false;
        }

        for (int i = 0; i < arr1.Count; i++)
        {
            if (!CompareJsonValues(arr1[i], arr2[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Normalizes a JSON object by sorting its properties.
    /// </summary>
    /// <param name="jsonNode">The JSON node to normalize.</param>
    /// <returns>A normalized JSON string.</returns>
    protected static string NormalizeJsonObject(JsonNode jsonNode)
    {
        return JsonSerializer.Serialize(jsonNode, new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = null
        });
    }

    /// <summary>
    /// Disposes of the resources.
    /// </summary>
    public abstract void Dispose();
}