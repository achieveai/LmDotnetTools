using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LmTestUtils.Tests;

public class InstructionChainParserTests
{
    private readonly InstructionChainParser _parser;

    public InstructionChainParserTests()
    {
        var logger = NullLoggerFactory.Instance.CreateLogger<InstructionChainParser>();
        _parser = new InstructionChainParser(logger);
    }

    [Fact]
    public void ExtractInstructionChain_WithReasoningOnlyInstruction_ShouldParseSuccessfully()
    {
        // Arrange - This is the exact format that was failing
        var content = """
            <|instruction_start|>
            {"instruction_chain": [
                {"reasoning": {"length": 150}},
                {"id_message": "Something interesting", "messages":[{"tool_call":[{"name":"get_weather","args":{"location":"Seattle"}}]}]},
                {"messages":[{"tool_call":[{"name":"get_weather","args":{"location":"San Francisco"}}]}]},
                {"id":"step2","id_message":"Now responding with summary","messages":[{"text_message":{"length":20}}]}
            ]}
            <|instruction_end|>
            """;

        // Act
        var result = _parser.ExtractInstructionChain(content);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(4, result.Length);

        // First instruction: reasoning only (no messages)
        Assert.Equal(150, result[0].ReasoningLength);
        Assert.Empty(result[0].Messages);

        // Second instruction: tool call with id_message
        Assert.Equal("Something interesting", result[1].IdMessage);
        Assert.Single(result[1].Messages);
        Assert.NotNull(result[1].Messages[0].ToolCalls);
        Assert.Equal("get_weather", result[1].Messages[0].ToolCalls![0].Name);

        // Third instruction: tool call without id_message
        Assert.Equal(string.Empty, result[2].IdMessage);
        Assert.Single(result[2].Messages);
        Assert.NotNull(result[2].Messages[0].ToolCalls);

        // Fourth instruction: text message with id and id_message
        Assert.Equal("Now responding with summary", result[3].IdMessage);
        Assert.Single(result[3].Messages);
        Assert.Equal(20, result[3].Messages[0].TextLength);
    }

    [Fact]
    public void ExtractInstructionChain_WithReasoningAndTextMessage_ShouldParseSuccessfully()
    {
        // Arrange
        var content = """
            <|instruction_start|>
            {"instruction_chain": [
                {"reasoning": {"length": 100}, "messages":[{"text_message":{"length":50}}]}
            ]}
            <|instruction_end|>
            """;

        // Act
        var result = _parser.ExtractInstructionChain(content);

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(100, result[0].ReasoningLength);
        Assert.Single(result[0].Messages);
        Assert.Equal(50, result[0].Messages[0].TextLength);
    }

    [Fact]
    public void ExtractInstructionChain_WithOnlyReasoning_ShouldParseSuccessfully()
    {
        // Arrange - Instruction with only reasoning, no messages
        var content = """
            <|instruction_start|>
            {"instruction_chain": [
                {"reasoning": {"length": 200}}
            ]}
            <|instruction_end|>
            """;

        // Act
        var result = _parser.ExtractInstructionChain(content);

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(200, result[0].ReasoningLength);
        Assert.Empty(result[0].Messages);
    }

    [Fact]
    public void ExtractInstructionChain_IdMessageShouldNotFallbackToId()
    {
        // Arrange - Only id is provided, id_message should be empty
        var content = """
            <|instruction_start|>
            {"instruction_chain": [
                {"id": "step1", "messages":[{"text_message":{"length":10}}]}
            ]}
            <|instruction_end|>
            """;

        // Act
        var result = _parser.ExtractInstructionChain(content);

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(string.Empty, result[0].IdMessage); // Should NOT fallback to "step1"
    }

    [Fact]
    public void ExtractInstructionChain_WithEmptyMessages_ShouldStillParseIfReasoningPresent()
    {
        // Arrange
        var content = """
            <|instruction_start|>
            {"instruction_chain": [
                {"reasoning": {"length": 50}, "messages":[]}
            ]}
            <|instruction_end|>
            """;

        // Act
        var result = _parser.ExtractInstructionChain(content);

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(50, result[0].ReasoningLength);
        Assert.Empty(result[0].Messages);
    }

    [Fact]
    public void ExtractInstructionChain_WithNoMessagesAndNoReasoning_ShouldSkipInstruction()
    {
        // Arrange - Empty instruction should be skipped
        var content = """
            <|instruction_start|>
            {"instruction_chain": [
                {},
                {"messages":[{"text_message":{"length":10}}]}
            ]}
            <|instruction_end|>
            """;

        // Act
        var result = _parser.ExtractInstructionChain(content);

        // Assert
        Assert.NotNull(result);
        Assert.Single(result); // Only the second instruction should be parsed
        Assert.Equal(10, result[0].Messages[0].TextLength);
    }

    [Fact]
    public void ExtractInstructionChain_WithMultipleToolCalls_ShouldParseAll()
    {
        // Arrange
        var content = """
            <|instruction_start|>
            {"instruction_chain": [
                {"messages":[{"tool_call":[
                    {"name":"tool1","args":{"a":1}},
                    {"name":"tool2","args":{"b":2}}
                ]}]}
            ]}
            <|instruction_end|>
            """;

        // Act
        var result = _parser.ExtractInstructionChain(content);

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Single(result[0].Messages);
        Assert.NotNull(result[0].Messages[0].ToolCalls);
        Assert.Equal(2, result[0].Messages[0].ToolCalls!.Count);
        Assert.Equal("tool1", result[0].Messages[0].ToolCalls![0].Name);
        Assert.Equal("tool2", result[0].Messages[0].ToolCalls![1].Name);
    }

    [Fact]
    public void ExtractInstructionChain_WithSystemPromptEcho_ShouldParseAsExplicitText()
    {
        // Arrange
        var content = """
            <|instruction_start|>
            {"instruction_chain": [
                {"id_message": "Echo system prompt", "messages":[{"system_prompt_echo":{}}]}
            ]}
            <|instruction_end|>
            """;

        // Act
        var result = _parser.ExtractInstructionChain(content);

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("Echo system prompt", result[0].IdMessage);
        Assert.Single(result[0].Messages);
        Assert.Equal("__SYSTEM_PROMPT__", result[0].Messages[0].ExplicitText);
        Assert.Null(result[0].Messages[0].TextLength);
        Assert.Null(result[0].Messages[0].ToolCalls);
    }

    [Fact]
    public void ExtractInstructionChain_WithToolsList_ShouldParseAsExplicitText()
    {
        // Arrange
        var content = """
            <|instruction_start|>
            {"instruction_chain": [
                {"id_message": "List tools", "messages":[{"tools_list":{}}]}
            ]}
            <|instruction_end|>
            """;

        // Act
        var result = _parser.ExtractInstructionChain(content);

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("List tools", result[0].IdMessage);
        Assert.Single(result[0].Messages);
        Assert.Equal("__TOOLS_LIST__", result[0].Messages[0].ExplicitText);
        Assert.Null(result[0].Messages[0].TextLength);
        Assert.Null(result[0].Messages[0].ToolCalls);
    }

    [Fact]
    public void ExtractInstructionChain_WithMixedDynamicAndStaticMessages_ShouldParseAll()
    {
        // Arrange
        var content = """
            <|instruction_start|>
            {"instruction_chain": [
                {"id_message": "Mixed messages", "messages":[
                    {"text_message":{"length":10}},
                    {"system_prompt_echo":{}},
                    {"tools_list":{}},
                    {"tool_call":[{"name":"my_tool","args":{}}]}
                ]}
            ]}
            <|instruction_end|>
            """;

        // Act
        var result = _parser.ExtractInstructionChain(content);

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal(4, result[0].Messages.Count);

        // First message: text_message
        Assert.Equal(10, result[0].Messages[0].TextLength);
        Assert.Null(result[0].Messages[0].ExplicitText);

        // Second message: system_prompt_echo
        Assert.Equal("__SYSTEM_PROMPT__", result[0].Messages[1].ExplicitText);
        Assert.Null(result[0].Messages[1].TextLength);

        // Third message: tools_list
        Assert.Equal("__TOOLS_LIST__", result[0].Messages[2].ExplicitText);
        Assert.Null(result[0].Messages[2].TextLength);

        // Fourth message: tool_call
        Assert.NotNull(result[0].Messages[3].ToolCalls);
        Assert.Equal("my_tool", result[0].Messages[3].ToolCalls![0].Name);
    }

    [Fact]
    public void ExtractInstructionChain_WithDynamicMessagesInChain_ShouldParseMultipleInstructions()
    {
        // Arrange
        var content = """
            <|instruction_start|>
            {"instruction_chain": [
                {"id_message": "Show prompt", "messages":[{"system_prompt_echo":{}}]},
                {"id_message": "Show tools", "messages":[{"tools_list":{}}]}
            ]}
            <|instruction_end|>
            """;

        // Act
        var result = _parser.ExtractInstructionChain(content);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Length);

        Assert.Equal("Show prompt", result[0].IdMessage);
        Assert.Single(result[0].Messages);
        Assert.Equal("__SYSTEM_PROMPT__", result[0].Messages[0].ExplicitText);

        Assert.Equal("Show tools", result[1].IdMessage);
        Assert.Single(result[1].Messages);
        Assert.Equal("__TOOLS_LIST__", result[1].Messages[0].ExplicitText);
    }
}
