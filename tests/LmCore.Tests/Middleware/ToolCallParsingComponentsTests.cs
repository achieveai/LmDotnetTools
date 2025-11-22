using System.Text;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Middleware;

/// <summary>
/// Unit tests for the tool call parsing components that will be used in the refactored middleware
/// </summary>
public class ToolCallParsingComponentsTests
{
    [Fact]
    public void ToolCallTextParser_WithNoToolCalls_ReturnsTextChunk()
    {
        // Arrange
        var parser = new ToolCallTextParser();
        var text = "Hello world, this is just regular text.";

        // Act
        var result = ToolCallTextParser.Parse(text);

        // Assert
        _ = Assert.Single(result);
        var chunk = Assert.IsType<TextChunk>(result[0]);
        Assert.Equal(text, chunk.Text);
    }

    [Fact]
    public void ToolCallTextParser_WithSingleToolCall_ReturnsToolCallChunk()
    {
        // Arrange
        var parser = new ToolCallTextParser();
        var text = "<tool_call name=\"GetWeather\">\n```json\n{\"location\": \"San Francisco\"}\n```\n</tool_call>";

        // Act
        var result = ToolCallTextParser.Parse(text);

        // Assert
        _ = Assert.Single(result);
        var chunk = Assert.IsType<ToolCallChunk>(result[0]);
        Assert.Equal("GetWeather", chunk.ToolName);
        Assert.Contains("San Francisco", chunk.Content);
        Assert.Equal(text, chunk.RawMatch);
    }

    [Fact]
    public void ToolCallTextParser_WithTextAndToolCall_ReturnsAlternatingChunks()
    {
        // Arrange
        var parser = new ToolCallTextParser();
        var text =
            "Here's the weather: <tool_call name=\"GetWeather\">\n```json\n{\"location\": \"San Francisco\"}\n```\n</tool_call> Hope that helps!";

        // Act
        var result = ToolCallTextParser.Parse(text);

        // Assert
        Assert.Equal(3, result.Count);

        var textChunk1 = Assert.IsType<TextChunk>(result[0]);
        Assert.Equal("Here's the weather: ", textChunk1.Text);

        var toolChunk = Assert.IsType<ToolCallChunk>(result[1]);
        Assert.Equal("GetWeather", toolChunk.ToolName);

        var textChunk2 = Assert.IsType<TextChunk>(result[2]);
        Assert.Equal(" Hope that helps!", textChunk2.Text);
    }

    [Fact]
    public void PartialToolCallDetector_WithCompleteText_ReturnsNoMatch()
    {
        // Arrange
        var detector = new PartialToolCallDetector();
        var text = "Hello world, complete sentence.";

        // Act
        var result = PartialToolCallDetector.DetectPartialStart(text);

        // Assert
        Assert.False(result.IsMatch);
    }

    [Theory]
    [InlineData("Hello <", 6, "<")]
    [InlineData("Hello <t", 6, "<t")]
    [InlineData("Hello <tool_call", 6, "<tool_call")]
    [InlineData("Hello <tool_ca", 6, "<tool_ca")]
    [InlineData("Hello <tool_call name=\"FooBar\"", 6, "<tool_call name=\"FooBar\"")]
    [InlineData("Hello <tool_call  name   =\"FooBar\"", 6, "<tool_call  name   =\"FooBar\"")]
    [InlineData("Hello <tool_call  name   =\"FooBar\">", 6, "<tool_call  name   =\"FooBar\">")]
    [InlineData(
        "Hello <tool_call  name   =\"FooBar\">\n```json{}</tool_call",
        6,
        "<tool_call  name   =\"FooBar\">\n```json{}</tool_call"
    )]
    public void PartialToolCallDetector_WithPartialPattern_ReturnsMatch(
        string text,
        int expectedIndex,
        string expectedPattern
    )
    {
        // Arrange
        var detector = new PartialToolCallDetector();

        // Act
        var result = PartialToolCallDetector.DetectPartialStart(text);

        // Assert
        Assert.True(result.IsMatch);
        Assert.Equal(expectedIndex, result.StartIndex);
        Assert.Equal(expectedPattern, result.PartialPattern);
    }

    [Fact]
    public void SafeTextExtractor_WithNoPartialPattern_ReturnsAllTextAsSafe()
    {
        // Arrange
        var detector = new PartialToolCallDetector();
        var extractor = new SafeTextExtractor(detector);
        var text = "Hello world, complete text.";

        // Act
        var result = SafeTextExtractor.ExtractSafeText(text);

        // Assert
        Assert.Equal(text, result.SafeText);
        Assert.Equal(string.Empty, result.RemainingBuffer);
    }

    [Fact]
    public void SafeTextExtractor_WithPartialPattern_SplitsCorrectly()
    {
        // Arrange
        var detector = new PartialToolCallDetector();
        var extractor = new SafeTextExtractor(detector);
        var text = "Hello world, then partial <tool_ca";

        // Act
        var result = SafeTextExtractor.ExtractSafeText(text);

        // Assert
        Assert.Equal("Hello world, then partial ", result.SafeText);
        Assert.Equal("<tool_ca", result.RemainingBuffer);
    }

    [Fact]
    public void SafeTextExtractor_WithJustPartialPattern_ReturnsEmptySafeText()
    {
        // Arrange
        var detector = new PartialToolCallDetector();
        var extractor = new SafeTextExtractor(detector);
        var text = "<tool_call";

        // Act
        var result = SafeTextExtractor.ExtractSafeText(text);

        // Assert
        Assert.Equal(string.Empty, result.SafeText);
        Assert.Equal("<tool_call", result.RemainingBuffer);
    }

    [Theory]
    [InlineData("Hello world", "Hello world", "")] // No tool call - all text is safe
    [InlineData("Complete sentence.", "Complete sentence.", "")] // No tool call - all text is safe
    [InlineData("Text with <tag>", "Text with <tag>", "")] // Regular XML tag - all safe
    [InlineData("Text with <div>content</div>", "Text with <div>content</div>", "")] // Regular HTML - all safe
    [InlineData("Text ending with <", "Text ending with ", "<")] // Partial opening bracket
    [InlineData("Text ending with <t", "Text ending with ", "<t")] // Partial tool start
    [InlineData("Text ending with <tool_call", "Text ending with ", "<tool_call")] // Partial tool call
    [InlineData("Text ending with <tool_call name=\"Test\"", "Text ending with ", "<tool_call name=\"Test\"")] // Partial with attributes
    [InlineData("Text ending with <tool_call name=\"Test\">", "Text ending with ", "<tool_call name=\"Test\">")] // Complete opening but incomplete
    [InlineData("Text with <tool_call name=\"Test\">content", "Text with ", "<tool_call name=\"Test\">content")] // Has content but no closing
    [InlineData(
        "Text with <tool_call name=\"Test\">content</tool_call",
        "Text with ",
        "<tool_call name=\"Test\">content</tool_call"
    )] // Missing final >
    [InlineData("Text with <tool_call name=\"Test\">content</t", "Text with ", "<tool_call name=\"Test\">content</t")] // Partial closing tag
    [InlineData("Text with <tool_call name=\"Test\">content</", "Text with ", "<tool_call name=\"Test\">content</")] // Just closing bracket
    [InlineData("<tool_call", "", "<tool_call")] // Just partial tool call
    [InlineData("<tool_call name=\"Test\">", "", "<tool_call name=\"Test\">")] // Just complete opening
    [InlineData("Multiple <tool_call calls", "Multiple ", "<tool_call calls")] // Partial in middle
    [InlineData("Before <other>tag</other> then <tool_call", "Before <other>tag</other> then ", "<tool_call")] // Mixed content
    public void SafeTextExtractor_VariousInputs_ReturnsExpectedSafeTextAndBuffer(
        string input,
        string expectedSafe,
        string expectedBuffer
    )
    {
        // Arrange
        var detector = new PartialToolCallDetector();
        var extractor = new SafeTextExtractor(detector);

        // Act
        var result = SafeTextExtractor.ExtractSafeText(input);

        // Assert
        Assert.Equal(expectedSafe, result.SafeText);
        Assert.Equal(expectedBuffer, result.RemainingBuffer);
    }

    [Theory]
    [InlineData("Simple text", 1, typeof(TextChunk))] // Just text
    [InlineData("<tool_call name=\"Test\">```json\n{\"arg\": \"value\"}\n```</tool_call>", 1, typeof(ToolCallChunk))] // Just tool call
    [InlineData(
        "Before <tool_call name=\"Test\">```json\n{\"arg\": \"value\"}\n```</tool_call> after",
        3,
        typeof(TextChunk)
    )] // Text-Tool-Text
    [InlineData(
        "Start <tool_call name=\"First\">```json\n{}\n```</tool_call> middle <tool_call name=\"Second\">```json\n{}\n```</tool_call> end",
        5,
        typeof(TextChunk)
    )] // Text-Tool-Text-Tool-Text
    [InlineData(
        "<tool_call name=\"First\">```json\n{}\n```</tool_call><tool_call name=\"Second\">```json\n{}\n```</tool_call>",
        2,
        typeof(ToolCallChunk)
    )] // Tool-Tool
    [InlineData("Text with <other>tag</other> normal content", 1, typeof(TextChunk))] // Text with non-tool tags
    [InlineData(
        "Mixed <div>html</div> and <tool_call name=\"Test\">```json\n{}\n```</tool_call> content",
        3,
        typeof(TextChunk)
    )] // Mixed HTML and tool calls
    public void ToolCallTextParser_VariousInputs_ReturnsExpectedChunkCount(
        string input,
        int expectedCount,
        Type expectedFirstType
    )
    {
        // Arrange
        var parser = new ToolCallTextParser();

        // Act
        var result = ToolCallTextParser.Parse(input);

        // Assert
        Assert.Equal(expectedCount, result.Count);
        Assert.IsType(expectedFirstType, result[0]);
    }

    [Theory]
    [InlineData(
        "Text with <tool_call name=\"GetWeather\">```json\n{\"location\": \"NYC\"}\n```</tool_call> done",
        "GetWeather",
        "location"
    )] // Basic tool call
    [InlineData(
        "Call <tool_call name=\"CalculateSum\">```json\n{\"a\": 5, \"b\": 10}\n```</tool_call>",
        "CalculateSum",
        "a"
    )] // Math tool
    [InlineData(
        "Use <tool_call name=\"SearchDatabase\">```json\n{\"query\": \"users\", \"limit\": 100}\n```</tool_call>",
        "SearchDatabase",
        "query"
    )] // Database tool
    [InlineData(
        "Execute <tool_call name=\"SendEmail\">```json\n{\"to\": \"test@example.com\", \"subject\": \"Test\"}\n```</tool_call>",
        "SendEmail",
        "to"
    )] // Email tool
    public void ToolCallTextParser_WithValidToolCalls_ExtractsCorrectToolNameAndContent(
        string input,
        string expectedToolName,
        string expectedContentContains
    )
    {
        // Arrange
        var parser = new ToolCallTextParser();

        // Act
        var result = ToolCallTextParser.Parse(input);

        // Assert
        var toolCall = result.OfType<ToolCallChunk>().First();
        Assert.Equal(expectedToolName, toolCall.ToolName);
        Assert.Contains(expectedContentContains, toolCall.Content);
    }

    [Theory]
    [InlineData("Normal text", false, -1)] // No partial pattern
    [InlineData("Text with <div>", false, -1)] // Regular HTML tag
    [InlineData("Content with </div>", false, -1)] // Regular closing tag
    [InlineData("Text with <tool", true, 10)] // Partial tool
    [InlineData("Text with </tool", true, 10)] // Partial closing tool
    [InlineData("Content <tool_call name=\"Test\" >", true, 8)] // Complete opening (still partial)
    [InlineData("Content <tool_call name=\"Test\">data</tool_call", true, 8)] // Missing final >
    [InlineData("Multiple lines\nwith <tool_call name=\"Test\">\ncontent\n</tool_call", true, 20)] // Multiline partial (corrected index)
    [InlineData("Unicode content 🔧 <tool_call", true, 19)] // Unicode with partial (corrected for emoji)
    [InlineData("Nested <div><tool_call name=\"Test\">", true, 12)] // Nested in HTML
    [InlineData("JSON-like {\"key\": \"<tool_call\"}", false, -1)] // Partial in JSON-like string (should NOT match - it's inside quotes)
    public void PartialToolCallDetector_VariousInputs_ReturnsExpectedMatch(
        string input,
        bool shouldMatch,
        int expectedStartIndex
    )
    {
        // Arrange
        var detector = new PartialToolCallDetector();

        // Act
        var result = PartialToolCallDetector.DetectPartialStart(input);

        // Assert
        Assert.Equal(shouldMatch, result.IsMatch);
        if (shouldMatch)
        {
            Assert.Equal(expectedStartIndex, result.StartIndex);
            Assert.Equal(input.Substring(expectedStartIndex), result.PartialPattern);
        }
        else
        {
            Assert.Equal(-1, result.StartIndex);
        }
    }

    [Theory]
    [InlineData("")] // Empty string
    [InlineData("   ")] // Whitespace only
    [InlineData("\n\t\r")] // Various whitespace
    public void AllComponents_WithEmptyOrWhitespaceInput_HandleGracefully(string input)
    {
        // Arrange
        var parser = new ToolCallTextParser();
        var detector = new PartialToolCallDetector();
        var extractor = new SafeTextExtractor(detector);

        // Act & Assert - Should not throw exceptions
        var parseResult = ToolCallTextParser.Parse(input);
        var detectResult = PartialToolCallDetector.DetectPartialStart(input);
        var extractResult = SafeTextExtractor.ExtractSafeText(input);

        // Basic validation
        Assert.NotNull(parseResult);
        Assert.NotNull(detectResult);
        Assert.NotNull(extractResult);

        if (string.IsNullOrEmpty(input))
        {
            Assert.False(detectResult.IsMatch);
            Assert.Equal(string.Empty, extractResult.SafeText);
            Assert.Equal(string.Empty, extractResult.RemainingBuffer);
        }
    }

    [Fact]
    public void AllComponents_WithNullInput_HandleGracefully()
    {
        // Arrange
        var parser = new ToolCallTextParser();
        var detector = new PartialToolCallDetector();
        var extractor = new SafeTextExtractor(detector);
        string? nullInput = null;

        // Act & Assert - Should not throw exceptions
        var parseResult = ToolCallTextParser.Parse(nullInput!);
        var detectResult = PartialToolCallDetector.DetectPartialStart(nullInput!);
        var extractResult = SafeTextExtractor.ExtractSafeText(nullInput!);

        // Basic validation for null input
        Assert.NotNull(parseResult);
        Assert.False(detectResult.IsMatch);
        Assert.Equal(string.Empty, extractResult.SafeText);
        Assert.Equal(string.Empty, extractResult.RemainingBuffer);
    }

    [Fact]
    public void StreamingScenario_ToolCallSplitAcrossChunks_WorksCorrectly()
    {
        // This test simulates the exact scenario from the failing streaming test
        var detector = new PartialToolCallDetector();
        var extractor = new SafeTextExtractor(detector);
        var parser = new ToolCallTextParser();

        // Simulate the streaming chunks from the debug output
        var chunks = new[]
        {
            "Here's ",
            "the",
            " we",
            "ather:",
            " ",
            "<tool_call name=\"GetWeather\">\n```json\n{\n  \"location\": \"San Francisco, CA\",\n  \"unit\": \"fahrenheit\"\n}\n```\n",
            "</tool_call>",
        };

        var buffer = new StringBuilder();
        var emittedTextChunks = new List<string>();
        var detectedToolCalls = new List<ToolCallChunk>();

        // Process each chunk as it would arrive in streaming
        foreach (var chunk in chunks)
        {
            _ = buffer.Append(chunk);
            var currentText = buffer.ToString();

            var safeResult = SafeTextExtractor.ExtractSafeText(currentText);

            if (!string.IsNullOrEmpty(safeResult.SafeText))
            {
                // Parse the safe text for tool calls
                var parsedChunks = ToolCallTextParser.Parse(safeResult.SafeText);

                foreach (var parsedChunk in parsedChunks)
                {
                    if (parsedChunk is TextChunk textChunk)
                    {
                        emittedTextChunks.Add(textChunk.Text);
                    }
                    else if (parsedChunk is ToolCallChunk toolCallChunk)
                    {
                        detectedToolCalls.Add(toolCallChunk);
                    }
                }

                // Update buffer
                _ = buffer.Clear();
                _ = buffer.Append(safeResult.RemainingBuffer);
            }
        }

        // Final flush
        if (buffer.Length > 0)
        {
            var parsedChunks = ToolCallTextParser.Parse(buffer.ToString());
            foreach (var parsedChunk in parsedChunks)
            {
                if (parsedChunk is TextChunk textChunk)
                {
                    emittedTextChunks.Add(textChunk.Text);
                }
                else if (parsedChunk is ToolCallChunk toolCallChunk)
                {
                    detectedToolCalls.Add(toolCallChunk);
                }
            }
        }

        // Verify results
        _ = Assert.Single(detectedToolCalls); // Should detect exactly one tool call
        var toolCall = detectedToolCalls[0];
        Assert.Equal("GetWeather", toolCall.ToolName);
        Assert.Contains("San Francisco", toolCall.Content);
        Assert.Contains("fahrenheit", toolCall.Content);

        // Verify the non-tool call text was emitted correctly
        var allEmittedText = string.Join("", emittedTextChunks);
        Assert.Equal("Here's the weather: ", allEmittedText);
    }
}
