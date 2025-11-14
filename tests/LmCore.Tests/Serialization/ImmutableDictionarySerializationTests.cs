using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit.Abstractions;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Serialization;

/// <summary>
/// Tests for serialization and deserialization of ImmutableDictionary.
/// </summary>
public class ImmutableDictionarySerializationTests
{
    private readonly ITestOutputHelper _output;

    public ImmutableDictionarySerializationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private class TestClassWithExtensionDataConverter : ShadowPropertiesJsonConverter<TestClassWithExtensionData>
    {
        protected override TestClassWithExtensionData CreateInstance()
        {
            return new TestClassWithExtensionData();
        }
    }

    // Test class with JsonExtensionData for testing inline extra properties
    [JsonConverter(typeof(TestClassWithExtensionDataConverter))]
    private record TestClassWithExtensionData
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("value")]
        public int Value { get; init; }

        [JsonExtensionData]
        private Dictionary<string, object?> ExtraPropertiesInternal
        {
            get => ExtraProperties.ToDictionary();
            init => ExtraProperties = value.ToImmutableDictionary();
        }

        [JsonIgnore]
        public ImmutableDictionary<string, object?> ExtraProperties { get; init; } =
            ImmutableDictionary<string, object?>.Empty;

        public TestClassWithExtensionData SetExtraProperty<T>(string key, T value)
        {
            if (ExtraProperties == null)
            {
                return this with { ExtraProperties = ImmutableDictionary<string, object?>.Empty.Add(key, value) };
            }

            return this with
            {
                ExtraProperties = ExtraProperties.Add(key, value),
            };
        }

        public T? GetExtraProperty<T>(string key)
        {
            if (ExtraProperties == null)
            {
                return default;
            }

            if (ExtraProperties.TryGetValue(key, out var value) && value is T typedValue)
            {
                return typedValue;
            }

            return default;
        }
    }

    // Test class with JsonPropertyName for testing nested extra properties
    private record TestClassWithNestedProperties
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("value")]
        public int Value { get; init; }

        [JsonPropertyName("extra_properties")]
        [JsonConverter(typeof(ExtraPropertiesConverter))]
        public ImmutableDictionary<string, object?> ExtraProperties { get; init; } =
            ImmutableDictionary<string, object?>.Empty;

        public TestClassWithNestedProperties SetExtraProperty<T>(string key, T value)
        {
            if (ExtraProperties == null)
            {
                return this with { ExtraProperties = ImmutableDictionary<string, object?>.Empty.Add(key, value) };
            }

            return this with
            {
                ExtraProperties = ExtraProperties.Add(key, value),
            };
        }

        public T? GetExtraProperty<T>(string key)
        {
            if (ExtraProperties == null)
            {
                return default;
            }

            if (ExtraProperties.TryGetValue(key, out var value) && value is T typedValue)
            {
                return typedValue;
            }

            return default;
        }
    }

    [Fact]
    public void BasicDictionarySerialization_WorksCorrectly()
    {
        // Arrange
        var options = new JsonSerializerOptions { WriteIndented = true };

        var dictionary = new Dictionary<string, string> { ["key1"] = "value1", ["key2"] = "value2" };

        // Act
        var json = JsonSerializer.Serialize(dictionary, options);
        _output.WriteLine($"Serialized JSON: {json}");
        var deserialized = JsonSerializer.Deserialize<Dictionary<string, string>>(json, options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized!.Count);
        Assert.Equal("value1", deserialized["key1"]);
        Assert.Equal("value2", deserialized["key2"]);
    }

    [Fact]
    public void ImmutableDictionaryWithConverter_SerializesAndDeserializes_StringValues()
    {
        // Arrange
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new ImmutableDictionaryJsonConverterFactory() },
        };

        var dictionary = ImmutableDictionary<string, string>.Empty.Add("key1", "value1").Add("key2", "value2");

        // Act
        var json = JsonSerializer.Serialize(dictionary, options);
        _output.WriteLine($"Serialized JSON: {json}");
        var deserialized = JsonSerializer.Deserialize<ImmutableDictionary<string, string>>(json, options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized!.Count);
        Assert.Equal("value1", deserialized["key1"]);
        Assert.Equal("value2", deserialized["key2"]);
    }

    [Fact]
    public void ImmutableDictionaryWithConverter_SerializesAndDeserializes_MixedValues()
    {
        // Arrange
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new ExtraPropertiesConverter() },
        };

        var dictionary = new Dictionary<string, object?>
        {
            ["string"] = "value",
            ["int"] = 42,
            ["bool"] = true,
            ["null"] = null,
        }.ToImmutableDictionary();

        // Act
        var json = JsonSerializer.Serialize(dictionary, options);
        _output.WriteLine($"Serialized JSON: {json}");
        var deserialized = JsonSerializer.Deserialize<ImmutableDictionary<string, object?>>(json, options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(4, deserialized!.Count);

        // Diagnostic information
        var stringValue = deserialized["string"];
        _output.WriteLine($"String value type: {stringValue?.GetType().FullName ?? "null"}");
        _output.WriteLine($"String value: {stringValue}");

        if (stringValue is JsonElement jsonElement)
        {
            _output.WriteLine($"JsonElement kind: {jsonElement.ValueKind}");
            _output.WriteLine($"JsonElement as string: {jsonElement.GetString()}");
            Assert.Equal("value", jsonElement.GetString());
        }
        else
        {
            Assert.Equal("value", stringValue);
        }

        // For numeric values, JsonElement needs conversion
        var intValue = deserialized["int"];
        _output.WriteLine($"Int value type: {intValue?.GetType().FullName ?? "null"}");

        if (intValue is JsonElement intElement)
        {
            Assert.Equal(42, intElement.GetInt32());
        }
        else
        {
            Assert.Equal(42, Convert.ToInt32(intValue));
        }

        // For boolean values
        var boolValue = deserialized["bool"];
        _output.WriteLine($"Bool value type: {boolValue?.GetType().FullName ?? "null"}");

        if (boolValue is JsonElement boolElement)
        {
            Assert.True(boolElement.GetBoolean());
        }
        else
        {
            Assert.True((bool)boolValue!);
        }

        // For null values
        Assert.Null(deserialized["null"]);
    }

    [Fact]
    public void ExtraPropertiesConverter_SerializesAndDeserializes_SimpleValues()
    {
        // Arrange
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new ExtraPropertiesConverter() },
        };

        var dictionary = new Dictionary<string, object?>
        {
            ["string"] = "value",
            ["int"] = 42,
            ["bool"] = true,
            ["null"] = null,
        }.ToImmutableDictionary();

        // Act
        var json = JsonSerializer.Serialize(dictionary, options);
        _output.WriteLine($"Serialized JSON: {json}");
        var deserialized = JsonSerializer.Deserialize<ImmutableDictionary<string, object?>>(json, options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(4, deserialized!.Count);

        // Diagnostic information
        var stringValue = deserialized["string"];
        _output.WriteLine($"String value type: {stringValue?.GetType().FullName ?? "null"}");
        _output.WriteLine($"String value: {stringValue}");

        if (stringValue is JsonElement jsonElement)
        {
            _output.WriteLine($"JsonElement kind: {jsonElement.ValueKind}");
            _output.WriteLine($"JsonElement as string: {jsonElement.GetString()}");
            Assert.Equal("value", jsonElement.GetString());
        }
        else
        {
            Assert.Equal("value", stringValue);
        }

        // For numeric values, JsonElement needs conversion
        var intValue = deserialized["int"];
        _output.WriteLine($"Int value type: {intValue?.GetType().FullName ?? "null"}");

        if (intValue is JsonElement intElement)
        {
            Assert.Equal(42, intElement.GetInt32());
        }
        else
        {
            Assert.Equal(42, Convert.ToInt32(intValue));
        }

        // For boolean values
        var boolValue = deserialized["bool"];
        _output.WriteLine($"Bool value type: {boolValue?.GetType().FullName ?? "null"}");

        if (boolValue is JsonElement boolElement)
        {
            Assert.True(boolElement.GetBoolean());
        }
        else
        {
            Assert.True((bool)boolValue!);
        }

        // For null values
        Assert.Null(deserialized["null"]);
    }

    [Fact]
    public void UsageClass_SerializesAndDeserializes_WithExtraProperties()
    {
        // Arrange
        var options = new JsonSerializerOptions { WriteIndented = true };

        var usage = new TestClassWithExtensionData { Name = "Test", Value = 10 };

        // Add extra properties
        var withExtras = usage.SetExtraProperty("estimated_cost", 0.05).SetExtraProperty("cached", true);

        // Act
        var json = JsonSerializer.Serialize(withExtras, options);
        _output.WriteLine($"Serialized JSON: {json}");

        // Examine the JSON structure
        using var doc = JsonDocument.Parse(json);
        _output.WriteLine("JSON structure:");
        PrintJsonElement(_output, doc.RootElement, 0);

        // Verify that the extra properties are in the JSON directly
        Assert.True(doc.RootElement.TryGetProperty("estimated_cost", out var estimatedCostElement));
        Assert.Equal(0.05, estimatedCostElement.GetDouble());

        Assert.True(doc.RootElement.TryGetProperty("cached", out var cachedElement));
        Assert.True(cachedElement.GetBoolean());

        var deserialized = JsonSerializer.Deserialize<TestClassWithExtensionData>(json, options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal("Test", deserialized!.Name);
        Assert.Equal(10, deserialized.Value);

        // Check extra properties - they should be accessible through the ExtraProperties property
        Assert.NotNull(deserialized.ExtraProperties);
        _output.WriteLine($"ExtraProperties count: {deserialized.ExtraProperties?.Count ?? 0}");

        if (deserialized.ExtraProperties != null)
        {
            foreach (var prop in deserialized.ExtraProperties)
            {
                _output.WriteLine(
                    $"Property: {prop.Key}, Type: {prop.Value?.GetType().FullName ?? "null"}, Value: {prop.Value}"
                );
            }
        }

        // Get the estimated_cost value
        var estimatedCost = deserialized.GetExtraProperty<double>("estimated_cost");
        _output.WriteLine($"Estimated cost: {estimatedCost}");
        Assert.Equal(0.05, estimatedCost);

        // Get the cached value
        var cached = deserialized.GetExtraProperty<bool>("cached");
        _output.WriteLine($"Cached: {cached}");
        Assert.True(cached);
    }

    [Fact]
    public void GenerateReplyOptions_SerializesAndDeserializes_WithExtraProperties()
    {
        // Arrange
        var options = new JsonSerializerOptions { WriteIndented = true };

        var replyOptions = new TestClassWithNestedProperties { Name = "Test", Value = 10 };

        // Add extra properties
        var withExtras = replyOptions.SetExtraProperty("function_call", "auto");

        // Act
        var json = JsonSerializer.Serialize(withExtras, options);
        _output.WriteLine($"Serialized JSON: {json}");

        // Examine the JSON structure
        using var doc = JsonDocument.Parse(json);
        _output.WriteLine("JSON structure:");
        PrintJsonElement(_output, doc.RootElement, 0);

        // Verify that the extra properties are in the JSON directly
        Assert.True(doc.RootElement.TryGetProperty("extra_properties", out var extraPropertiesElement));
        Assert.True(extraPropertiesElement.TryGetProperty("function_call", out var functionCallElement));
        Assert.Equal("auto", functionCallElement.GetString());

        var deserialized = JsonSerializer.Deserialize<TestClassWithNestedProperties>(json, options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal("Test", deserialized!.Name);
        Assert.Equal(10, deserialized.Value);

        // Check extra properties - they should be accessible through the ExtraProperties property
        Assert.NotNull(deserialized.ExtraProperties);
        _output.WriteLine($"ExtraProperties count: {deserialized.ExtraProperties.Count}");

        foreach (var prop in deserialized.ExtraProperties)
        {
            _output.WriteLine(
                $"Property: {prop.Key}, Type: {prop.Value?.GetType().FullName ?? "null"}, Value: {prop.Value}"
            );
        }

        // Check function_call property
        Assert.True(
            deserialized.ExtraProperties.ContainsKey("function_call"),
            "ExtraProperties should contain 'function_call'"
        );
        var functionCall = deserialized.ExtraProperties["function_call"];
        _output.WriteLine($"Function call type: {functionCall?.GetType().FullName ?? "null"}");

        if (functionCall is JsonElement functionCallElement2)
        {
            Assert.Equal("auto", functionCallElement2.GetString());
        }
        else
        {
            Assert.Equal("auto", functionCall);
        }
    }

    // Helper method to print JSON structure
    private static void PrintJsonElement(ITestOutputHelper output, JsonElement element, int indent)
    {
        var indentStr = new string(' ', indent * 2);

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                output.WriteLine($"{indentStr}{{");
                foreach (var property in element.EnumerateObject())
                {
                    output.WriteLine($"{indentStr}  \"{property.Name}\": ");
                    PrintJsonElement(output, property.Value, indent + 1);
                }
                output.WriteLine($"{indentStr}}}");
                break;

            case JsonValueKind.Array:
                output.WriteLine($"{indentStr}[");
                foreach (var item in element.EnumerateArray())
                {
                    PrintJsonElement(output, item, indent + 1);
                    output.WriteLine("");
                }
                output.WriteLine($"{indentStr}]");
                break;

            case JsonValueKind.String:
                output.WriteLine($"{indentStr}\"{element.GetString()}\"");
                break;

            case JsonValueKind.Number:
                output.WriteLine($"{indentStr}{element.GetRawText()}");
                break;

            case JsonValueKind.True:
            case JsonValueKind.False:
                output.WriteLine($"{indentStr}{element.GetBoolean()}");
                break;

            case JsonValueKind.Null:
                output.WriteLine($"{indentStr}null");
                break;
        }
    }
}
