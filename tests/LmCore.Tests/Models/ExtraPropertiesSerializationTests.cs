namespace AchieveAi.LmDotnetTools.LmCore.Tests.Models;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Models;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Tests for validating that extra properties are serialized inline in production models.
/// </summary>
public class ExtraPropertiesSerializationTests
{
  private readonly ITestOutputHelper _output;

  public ExtraPropertiesSerializationTests(ITestOutputHelper output)
  {
    _output = output;
  }

  [Fact]
  public void Usage_ExtraProperties_SerializedInline()
  {
    // Arrange
    var options = new JsonSerializerOptions
    {
      WriteIndented = true,
    };
    
    var usage = new Usage
    {
      PromptTokens = 10,
      CompletionTokens = 20,
      TotalTokens = 30
    };
    
    // Add extra properties
    var withExtras = usage.SetExtraProperty("estimated_cost", 0.05)
                          .SetExtraProperty("cached", true);
    
    // Act
    var json = JsonSerializer.Serialize(withExtras, options);
    _output.WriteLine($"Serialized JSON: {json}");
    
    // Examine the JSON structure
    using var doc = JsonDocument.Parse(json);
    
    // Assert - verify that the standard properties are present
    Assert.True(doc.RootElement.TryGetProperty("prompt_tokens", out var promptTokensElement));
    Assert.Equal(10, promptTokensElement.GetInt32());
    
    Assert.True(doc.RootElement.TryGetProperty("completion_tokens", out var completionTokensElement));
    Assert.Equal(20, completionTokensElement.GetInt32());
    
    Assert.True(doc.RootElement.TryGetProperty("total_tokens", out var totalTokensElement));
    Assert.Equal(30, totalTokensElement.GetInt32());
    
    // Verify that the extra properties are serialized directly in the parent object
    Assert.True(doc.RootElement.TryGetProperty("estimated_cost", out var estimatedCostElement));
    Assert.Equal(0.05, estimatedCostElement.GetDouble());
    
    Assert.True(doc.RootElement.TryGetProperty("cached", out var cachedElement));
    Assert.True(cachedElement.GetBoolean());
    
    // Deserialize and verify that the extra properties are accessible
    var deserialized = JsonSerializer.Deserialize<Usage>(json, options);
    Assert.NotNull(deserialized);
    Assert.Equal(10, deserialized!.PromptTokens);
    Assert.Equal(20, deserialized.CompletionTokens);
    Assert.Equal(30, deserialized.TotalTokens);

    Assert.Equal(2, deserialized.ExtraProperties.Count);
    
    // Verify that the extra properties can be accessed through GetExtraProperty
    Assert.Equal(0.05, deserialized.ExtraProperties["estimated_cost"]);
    
    var cached = deserialized.ExtraProperties["cached"];
    Assert.True(deserialized.ExtraProperties["cached"] is bool cachedBool && cachedBool);
  }

  [Fact]
  public void GenerateReplyOptions_ExtraProperties_SerializedInline()
  {
    // Arrange
    var options = new JsonSerializerOptions
    {
      WriteIndented = true
    };
    
    var replyOptions = new GenerateReplyOptions
    {
      ModelId = "gpt-4",
      Temperature = 0.7f,
      MaxToken = 1000,
      ExtraProperties = ImmutableDictionary<string, object?>.Empty
        .Add("function_call", "auto")
        .Add("top_k", 50)
    };
    
    // Act
    var json = JsonSerializer.Serialize(replyOptions, options);
    _output.WriteLine($"Serialized JSON: {json}");
    
    // Examine the JSON structure
    using var doc = JsonDocument.Parse(json);
    
    // Assert - verify that the standard properties are present
    Assert.True(doc.RootElement.TryGetProperty("model", out var modelIdElement));
    Assert.Equal("gpt-4", modelIdElement.GetString());
    
    Assert.True(doc.RootElement.TryGetProperty("temperature", out var temperatureElement));
    Assert.Equal(0.7f, temperatureElement.GetSingle());
    
    Assert.True(doc.RootElement.TryGetProperty("max_tokens", out var maxTokenElement));
    Assert.Equal(1000, maxTokenElement.GetInt32());
    
    // Verify that the extra properties are serialized directly in the parent object
    Assert.True(doc.RootElement.TryGetProperty("function_call", out var functionCallElement));
    Assert.Equal("auto", functionCallElement.GetString());
    
    Assert.True(doc.RootElement.TryGetProperty("top_k", out var topKElement));
    Assert.Equal(50, topKElement.GetInt32());
    
    // Deserialize and verify that the extra properties are accessible
    var deserialized = JsonSerializer.Deserialize<GenerateReplyOptions>(json, options);
    Assert.NotNull(deserialized);
    Assert.Equal("gpt-4", deserialized!.ModelId);
    Assert.Equal(0.7f, deserialized.Temperature);
    Assert.Equal(1000, deserialized.MaxToken);
    
    // Verify that the extra properties can be accessed through the ExtraProperties property
    Assert.True(deserialized.ExtraProperties.ContainsKey("function_call"));
    var functionCall = deserialized.ExtraProperties["function_call"];
    if (functionCall is JsonElement functionCallJsonElement)
    {
      Assert.Equal("auto", functionCallJsonElement.GetString());
    }
    else
    {
      Assert.Equal("auto", functionCall);
    }
    
    Assert.True(deserialized.ExtraProperties.ContainsKey("top_k"));
    var topK = deserialized.ExtraProperties["top_k"];
    if (topK is JsonElement topKJsonElement)
    {
      Assert.Equal(50, topKJsonElement.GetInt32());
    }
    else
    {
      Assert.Equal(50, Convert.ToInt32(topK));
    }
  }
}
