using System.Text.Json;

namespace AchieveAi.LmDotnetTools.LmTestUtils.TestMode;

/// <summary>
/// Analyzes conversation history to determine which instruction to execute.
/// </summary>
public interface IConversationAnalyzer
{
    /// <summary>
    /// Analyzes the full conversation history to find the instruction chain and count assistant responses.
    /// Returns the instruction to execute based on the response count.
    /// </summary>
    /// <param name="root">The root JSON element containing the conversation messages.</param>
    /// <returns>A tuple containing the instruction plan to execute (or null) and the count of assistant responses.</returns>
    (InstructionPlan? plan, int assistantResponseCount) AnalyzeConversation(JsonElement root);

    /// <summary>
    /// Extracts the latest user message from the conversation.
    /// </summary>
    /// <param name="root">The root JSON element containing the conversation messages.</param>
    /// <returns>The latest user message content, or null if not found.</returns>
    string? ExtractLatestUserMessage(JsonElement root);
}
