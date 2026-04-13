using System.Text.Json;
using AchieveAi.LmDotnetTools.LmConfig.Capabilities;

namespace LmConfig.Tests.Capabilities;

public class ModelCapabilitiesTests
{
    [Fact]
    public void ModelCapabilities_WithThinkingCapability_HasThinkingCapability()
    {
        // Arrange
        System.Diagnostics.Debug.WriteLine("Testing ModelCapabilities with thinking capability");

        var thinkingCapability = new ThinkingCapability { Type = ThinkingType.Anthropic, SupportsBudgetTokens = true };

        var tokenLimits = new TokenLimits { MaxContextTokens = 200000, MaxOutputTokens = 8192 };

        var capabilities = new ModelCapabilities { Thinking = thinkingCapability, TokenLimits = tokenLimits };

        // Act
        var hasThinking = capabilities.HasCapability("thinking");
        var allCapabilities = capabilities.GetAllCapabilities();

        System.Diagnostics.Debug.WriteLine($"Has thinking capability: {hasThinking}");
        System.Diagnostics.Debug.WriteLine($"All capabilities: {string.Join(", ", allCapabilities)}");

        // Assert
        Assert.True(hasThinking);
        Assert.Contains("thinking", allCapabilities);
    }

    [Fact]
    public void ModelCapabilities_WithMultimodalCapability_HasMultimodalCapability()
    {
        // Arrange
        System.Diagnostics.Debug.WriteLine("Testing ModelCapabilities with multimodal capability");

        var multimodalCapability = new MultimodalCapability
        {
            SupportsImages = true,
            SupportedImageFormats = ["jpeg", "png", "webp"],
            MaxImageSize = 5242880,
        };

        var tokenLimits = new TokenLimits { MaxContextTokens = 128000, MaxOutputTokens = 4096 };

        var capabilities = new ModelCapabilities { Multimodal = multimodalCapability, TokenLimits = tokenLimits };

        // Act
        var hasMultimodal = capabilities.HasCapability("multimodal");
        var allCapabilities = capabilities.GetAllCapabilities();

        System.Diagnostics.Debug.WriteLine($"Has multimodal capability: {hasMultimodal}");
        System.Diagnostics.Debug.WriteLine($"All capabilities: {string.Join(", ", allCapabilities)}");

        // Assert
        Assert.True(hasMultimodal);
        Assert.Contains("multimodal", allCapabilities);
    }

    [Fact]
    public void ModelCapabilities_WithFunctionCallingCapability_HasFunctionCallingCapability()
    {
        // Arrange
        System.Diagnostics.Debug.WriteLine("Testing ModelCapabilities with function calling capability");

        var functionCallingCapability = new FunctionCallingCapability
        {
            SupportsTools = true,
            SupportsParallelCalls = false,
            MaxToolsPerRequest = 64,
        };

        var tokenLimits = new TokenLimits { MaxContextTokens = 128000, MaxOutputTokens = 4096 };

        var capabilities = new ModelCapabilities
        {
            FunctionCalling = functionCallingCapability,
            TokenLimits = tokenLimits,
        };

        // Act
        var hasFunctionCalling = capabilities.HasCapability("function_calling");
        var hasTools = capabilities.HasCapability("tools");
        var allCapabilities = capabilities.GetAllCapabilities();

        System.Diagnostics.Debug.WriteLine($"Has function calling capability: {hasFunctionCalling}");
        System.Diagnostics.Debug.WriteLine($"Has tools capability: {hasTools}");
        System.Diagnostics.Debug.WriteLine($"All capabilities: {string.Join(", ", allCapabilities)}");

        // Assert
        Assert.True(hasFunctionCalling);
        Assert.True(hasTools);
        Assert.Contains("function_calling", allCapabilities);
    }

    [Fact]
    public void ModelCapabilities_WithJsonSchemaCapability_HasJsonSchemaCapability()
    {
        // Arrange
        System.Diagnostics.Debug.WriteLine("Testing ModelCapabilities with json_schema capability");

        var responseFormatCapability = new ResponseFormatCapability { SupportsJsonSchema = true };

        var tokenLimits = new TokenLimits { MaxContextTokens = 128000, MaxOutputTokens = 4096 };

        var capabilities = new ModelCapabilities
        {
            ResponseFormats = responseFormatCapability,
            TokenLimits = tokenLimits,
        };

        // Act
        var hasJsonSchema = capabilities.HasCapability("json_schema");
        var allCapabilities = capabilities.GetAllCapabilities();

        System.Diagnostics.Debug.WriteLine($"Has json_schema capability: {hasJsonSchema}");
        System.Diagnostics.Debug.WriteLine($"All capabilities: {string.Join(", ", allCapabilities)}");

        // Assert
        Assert.True(hasJsonSchema);
        Assert.Contains("json_schema", allCapabilities);
    }

    [Fact]
    public void ModelCapabilities_WithAllCapabilities_ReturnsCompleteCapabilityList()
    {
        // Arrange
        System.Diagnostics.Debug.WriteLine("Testing ModelCapabilities with all capability types");

        var capabilities = new ModelCapabilities
        {
            Thinking = new ThinkingCapability { Type = ThinkingType.Anthropic },
            Multimodal = new MultimodalCapability { SupportsImages = true },
            FunctionCalling = new FunctionCallingCapability { SupportsTools = true },
            ResponseFormats = new ResponseFormatCapability
            {
                SupportsJsonMode = true,
                SupportsJsonSchema = true,
                SupportsStructuredOutput = true,
            },
            TokenLimits = new TokenLimits { MaxContextTokens = 200000, MaxOutputTokens = 8192 },
            SupportsStreaming = true,
            SupportedFeatures = ["custom-feature"],
            CustomCapabilities = new Dictionary<string, object> { ["custom"] = true },
        };

        // Act
        var allCapabilities = capabilities.GetAllCapabilities();

        System.Diagnostics.Debug.WriteLine($"Complete capability list: {string.Join(", ", allCapabilities)}");

        // Assert
        Assert.Contains("thinking", allCapabilities);
        Assert.Contains("multimodal", allCapabilities);
        Assert.Contains("function_calling", allCapabilities);
        Assert.Contains("streaming", allCapabilities);
        Assert.Contains("json_mode", allCapabilities);
        Assert.Contains("json_schema", allCapabilities);
        Assert.Contains("structured_output", allCapabilities);
        Assert.Contains("custom-feature", allCapabilities);
        Assert.Contains("custom", allCapabilities);
    }

    [Theory]
    [InlineData("thinking", ThinkingType.Anthropic, true)]
    [InlineData("thinking", ThinkingType.None, false)]
    [InlineData("multimodal", true, true)]
    [InlineData("multimodal", false, false)]
    [InlineData("function_calling", true, true)]
    [InlineData("function_calling", false, false)]
    [InlineData("streaming", true, true)]
    [InlineData("streaming", false, false)]
    public void ModelCapabilities_HasCapability_ReturnsCorrectValue(string capability, object setupValue, bool expected)
    {
        // Arrange
        System.Diagnostics.Debug.WriteLine($"Testing HasCapability for {capability} with setup value {setupValue}");

        var tokenLimits = new TokenLimits { MaxContextTokens = 128000, MaxOutputTokens = 4096 };

        var capabilities = new ModelCapabilities
        {
            TokenLimits = tokenLimits,
            Thinking = capability == "thinking" ? new ThinkingCapability { Type = (ThinkingType)setupValue } : null,
            Multimodal =
                capability == "multimodal" ? new MultimodalCapability { SupportsImages = (bool)setupValue } : null,
            FunctionCalling =
                capability == "function_calling"
                    ? new FunctionCallingCapability { SupportsTools = (bool)setupValue }
                    : null,
            SupportsStreaming = capability == "streaming" ? (bool)setupValue : true,
        };

        // Act
        var result = capabilities.HasCapability(capability);

        System.Diagnostics.Debug.WriteLine($"HasCapability result: {result}, expected: {expected}");

        // Assert
        Assert.Equal(expected, result);
    }
}
