using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Models;
using Xunit.Abstractions;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Models;
/// <summary>
/// Tests for validating that the shadow property pattern works correctly for serialization.
/// </summary>
public class ShadowPropertySerializationTests
{
    private readonly ITestOutputHelper _output;

    public ShadowPropertySerializationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Usage_WithExtraProperties_SerializesInline()
    {
        // Arrange
        var options = new JsonSerializerOptions { WriteIndented = true };

        var usage = new Usage
        {
            PromptTokens = 10,
            CompletionTokens = 20,
            TotalTokens = 30,
        };

        // Add extra properties
        var withExtras = usage.SetExtraProperty("estimated_cost", 0.05).SetExtraProperty("cached", true);

        // Act
        var json = JsonSerializer.Serialize(withExtras, options);
        _output.WriteLine($"Serialized JSON: {json}");

        // Assert - verify that extra properties are in the JSON directly
        using var doc = JsonDocument.Parse(json);

        // Check standard properties
        Assert.True(doc.RootElement.TryGetProperty("prompt_tokens", out var promptTokens));
        Assert.Equal(10, promptTokens.GetInt32());

        Assert.True(doc.RootElement.TryGetProperty("completion_tokens", out var completionTokens));
        Assert.Equal(20, completionTokens.GetInt32());

        Assert.True(doc.RootElement.TryGetProperty("total_tokens", out var totalTokens));
        Assert.Equal(30, totalTokens.GetInt32());

        // Check extra properties are serialized inline
        Assert.True(doc.RootElement.TryGetProperty("estimated_cost", out var estimatedCost));
        Assert.Equal(0.05, estimatedCost.GetDouble());

        Assert.True(doc.RootElement.TryGetProperty("cached", out var cached));
        Assert.True(cached.GetBoolean());
    }

    [Fact]
    public void Usage_WithExtraProperties_DeserializesCorrectly()
    {
        // Arrange
        var json =
            @"{
      ""prompt_tokens"": 10,
      ""completion_tokens"": 20,
      ""total_tokens"": 30,
      ""estimated_cost"": 0.05,
      ""cached"": true
    }";

        // Act
        var usage = JsonSerializer.Deserialize<Usage>(json);

        // Assert
        Assert.NotNull(usage);
        Assert.Equal(10, usage!.PromptTokens);
        Assert.Equal(20, usage.CompletionTokens);
        Assert.Equal(30, usage.TotalTokens);

        // Check extra properties are accessible through the ExtraProperties property
        Assert.NotNull(usage.ExtraProperties);
        Assert.Equal(2, usage.ExtraProperties.Count);

        // Check individual extra properties
        var estimatedCost = usage.GetExtraProperty<double>("estimated_cost");
        Assert.Equal(0.05, estimatedCost);

        var cached = usage.GetExtraProperty<bool>("cached");
        Assert.True(cached);
    }

    [Fact]
    public void GenerateReplyOptions_WithExtraProperties_SerializesInline()
    {
        // Arrange
        var options = new JsonSerializerOptions { WriteIndented = true };

        var replyOptions = new GenerateReplyOptions
        {
            ModelId = "gpt-4",
            Temperature = 0.7f,
            MaxToken = 1000,
            ExtraProperties = ImmutableDictionary<string, object?>.Empty.Add("function_call", "auto").Add("top_k", 50),
        };

        // Act
        var json = JsonSerializer.Serialize(replyOptions, options);
        _output.WriteLine($"Serialized JSON: {json}");

        // Assert - verify that extra properties are in the JSON directly
        using var doc = JsonDocument.Parse(json);

        // Check standard properties
        Assert.True(doc.RootElement.TryGetProperty("model", out var modelId));
        Assert.Equal("gpt-4", modelId.GetString());

        Assert.True(doc.RootElement.TryGetProperty("temperature", out var temperature));
        Assert.Equal(0.7f, temperature.GetSingle());

        Assert.True(doc.RootElement.TryGetProperty("max_tokens", out var maxToken));
        Assert.Equal(1000, maxToken.GetInt32());

        // Check extra properties are serialized inline
        Assert.True(doc.RootElement.TryGetProperty("function_call", out var functionCall));
        Assert.Equal("auto", functionCall.GetString());

        Assert.True(doc.RootElement.TryGetProperty("top_k", out var topK));
        Assert.Equal(50, topK.GetInt32());
    }

    [Fact]
    public void GenerateReplyOptions_WithExtraProperties_DeserializesCorrectly()
    {
        // Arrange
        var json =
            @"{
      ""model"": ""gpt-4"",
      ""temperature"": 0.7,
      ""max_tokens"": 1000,
      ""function_call"": ""auto"",
      ""top_k"": 50
    }";

        // Act
        var replyOptions = JsonSerializer.Deserialize<GenerateReplyOptions>(json);

        // Assert
        Assert.NotNull(replyOptions);
        Assert.Equal("gpt-4", replyOptions!.ModelId);
        Assert.Equal(0.7f, replyOptions.Temperature);
        Assert.Equal(1000, replyOptions.MaxToken);

        // Check extra properties are accessible through the ExtraProperties property
        Assert.NotNull(replyOptions.ExtraProperties);
        Assert.Equal(2, replyOptions.ExtraProperties.Count);

        // Check individual extra properties
        Assert.True(replyOptions.ExtraProperties.ContainsKey("function_call"));
        var functionCall = replyOptions.ExtraProperties["function_call"];
        if (functionCall is JsonElement functionCallElement)
        {
            Assert.Equal("auto", functionCallElement.GetString());
        }
        else
        {
            Assert.Equal("auto", functionCall);
        }

        Assert.True(replyOptions.ExtraProperties.ContainsKey("top_k"));
        var topK = replyOptions.ExtraProperties["top_k"];
        if (topK is JsonElement topKElement)
        {
            Assert.Equal(50, topKElement.GetInt32());
        }
        else
        {
            Assert.Equal(50, Convert.ToInt32(topK));
        }
    }

    public record Person(string Name, int Age, string? Address = null);

    [Fact]
    public void JsonSchemaObject_CanBeDeserialized_FromDotNetJsonSchema_SimpleType()
    {
        // Generate schema using .NET 9 API
        var dotnetSchema = JsonSerializerOptions.Default.GetJsonSchemaAsNode(typeof(Person));
        var schemaJson = dotnetSchema.ToJsonString();
        var deserOptions = JsonSerializerOptionsFactory.CreateCaseInsensitive();
        deserOptions.Converters.Add(new JsonStringEnumConverter());

        // Deserialize to JsonSchemaObject
        var schemaObj = JsonSerializer.Deserialize<JsonSchemaObject>(schemaJson, deserOptions);

        Assert.NotNull(schemaObj);
        Assert.Equal("object", JsonSchemaObject.GetJsonPrimaryType(schemaObj));
        Assert.NotNull(schemaObj.Properties);
        Assert.Contains("Name", schemaObj.Properties.Keys);
        Assert.Contains("Age", schemaObj.Properties.Keys);
        Assert.Contains("Address", schemaObj.Properties.Keys);
        Assert.Contains("Name", schemaObj.Required ?? []);
        Assert.Contains("Age", schemaObj.Required ?? []);
    }

    public class UserProfile
    {
        public string DisplayName { get; set; } = string.Empty;
        public int Level { get; set; }
    }

    public class UserAccount
    {
        public Guid Id { get; set; }
        public string Email { get; set; } = string.Empty;
        public UserProfile? Profile { get; set; }
    }

    [Fact]
    public void JsonSchemaObject_CanBeDeserialized_FromDotNetJsonSchema_NestedType()
    {
        // Generate schema using .NET 9 API
        var dotnetSchema = JsonSerializerOptions.Default.GetJsonSchemaAsNode(typeof(UserAccount));
        var schemaJson = dotnetSchema.ToJsonString();
        var deserOptions = JsonSerializerOptionsFactory.CreateCaseInsensitive();
        deserOptions.Converters.Add(new JsonStringEnumConverter());

        // Deserialize to JsonSchemaObject
        var schemaObj = JsonSerializer.Deserialize<JsonSchemaObject>(schemaJson, deserOptions);

        Assert.NotNull(schemaObj);
        Assert.Equal("object", JsonSchemaObject.GetJsonPrimaryType(schemaObj));
        Assert.NotNull(schemaObj.Properties);
        Assert.Contains("Id", schemaObj.Properties.Keys);
        Assert.Contains("Email", schemaObj.Properties.Keys);
        Assert.Contains("Profile", schemaObj.Properties.Keys);
        // Profile should be an object type property
        var profileProp = schemaObj.Properties["Profile"];
        Assert.NotNull(profileProp);
        // If Profile is nullable, type may be ["object", "null"] or similar
        // We check that type contains "object" or is "object"
        Assert.Equal("object", profileProp.Type.GetTypeString());
    }
}
