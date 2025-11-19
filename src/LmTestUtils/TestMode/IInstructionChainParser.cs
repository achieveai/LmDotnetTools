using System.Text.Json;

namespace AchieveAi.LmDotnetTools.LmTestUtils.TestMode;

/// <summary>
/// Parses instruction chains from user message content for test mode execution.
/// </summary>
public interface IInstructionChainParser
{
    /// <summary>
    /// Extracts and parses instruction chains from user message content.
    /// Supports both array format (instruction_chain) and single instruction format.
    /// </summary>
    /// <param name="content">The user message content containing instruction tags.</param>
    /// <returns>An array of instruction plans, or null if no valid instructions found.</returns>
    InstructionPlan[]? ExtractInstructionChain(string content);

    /// <summary>
    /// Parses a single instruction object from JSON into an InstructionPlan.
    /// </summary>
    /// <param name="instructionElement">The JSON element containing the instruction.</param>
    /// <returns>A parsed InstructionPlan or null if invalid.</returns>
    InstructionPlan? ParseSingleInstruction(JsonElement instructionElement);
}
