using System.Text.Json;
using AchieveAi.LmDotnetTools.LmConfig.Capabilities;

namespace LmConfig.Tests.Capabilities;

public class JsonSerializationTests
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    [Fact]
    public void ThinkingCapability_JsonSerializationRoundTrip_Success()
    {
        // Arrange
        System.Diagnostics.Debug.WriteLine(
            "Testing ThinkingCapability JSON serialization round-trip"
        );

        var original = new ThinkingCapability
        {
            Type = ThinkingType.Anthropic,
            SupportsBudgetTokens = true,
            SupportsThinkingType = true,
            MaxThinkingTokens = 8192,
            IsBuiltIn = false,
            IsExposed = true,
            ParameterName = "thinking",
        };

        // Act
        var json = JsonSerializer.Serialize(original, _jsonOptions);
        System.Diagnostics.Debug.WriteLine($"Serialized JSON: {json}");

        var deserialized = JsonSerializer.Deserialize<ThinkingCapability>(json, _jsonOptions);
        System.Diagnostics.Debug.WriteLine($"Deserialized type: {deserialized?.Type}");

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(original.Type, deserialized.Type);
        Assert.Equal(original.SupportsBudgetTokens, deserialized.SupportsBudgetTokens);
        Assert.Equal(original.SupportsThinkingType, deserialized.SupportsThinkingType);
        Assert.Equal(original.MaxThinkingTokens, deserialized.MaxThinkingTokens);
        Assert.Equal(original.IsBuiltIn, deserialized.IsBuiltIn);
        Assert.Equal(original.IsExposed, deserialized.IsExposed);
        Assert.Equal(original.ParameterName, deserialized.ParameterName);
    }

    [Fact]
    public void ModelCapabilities_CompleteJsonSerializationRoundTrip_Success()
    {
        // Arrange
        System.Diagnostics.Debug.WriteLine(
            "Testing complete ModelCapabilities JSON serialization round-trip"
        );

        var original = new ModelCapabilities
        {
            Thinking = new ThinkingCapability
            {
                Type = ThinkingType.Anthropic,
                SupportsBudgetTokens = true,
                MaxThinkingTokens = 8192,
            },
            TokenLimits = new TokenLimits { MaxContextTokens = 200000, MaxOutputTokens = 8192 },
            SupportsStreaming = true,
            Version = "2024-01",
            IsPreview = false,
            IsDeprecated = false,
        };

        // Act
        var json = JsonSerializer.Serialize(original, _jsonOptions);
        System.Diagnostics.Debug.WriteLine(
            $"Serialized complete ModelCapabilities JSON length: {json.Length} characters"
        );

        var deserialized = JsonSerializer.Deserialize<ModelCapabilities>(json, _jsonOptions);
        System.Diagnostics.Debug.WriteLine(
            $"Deserialized thinking type: {deserialized?.Thinking?.Type}"
        );

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(original.Thinking?.Type, deserialized.Thinking?.Type);
        Assert.Equal(
            original.TokenLimits.MaxContextTokens,
            deserialized.TokenLimits.MaxContextTokens
        );
        Assert.Equal(original.SupportsStreaming, deserialized.SupportsStreaming);
        Assert.Equal(original.Version, deserialized.Version);
        Assert.Equal(original.IsPreview, deserialized.IsPreview);
        Assert.Equal(original.IsDeprecated, deserialized.IsDeprecated);
    }
}
