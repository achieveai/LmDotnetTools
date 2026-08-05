using Microsoft.Extensions.Options;

namespace LmStreaming.Sample.Configuration;

public sealed class AgentOutputTokenOptions
{
    public const string SectionName = "AgentOutputTokens";

    public int Primary { get; init; } = 24_576;

    public int Delegated { get; init; } = 16_384;

    public ValidateOptionsResult Validate()
    {
        if (Primary <= 0)
        {
            return ValidateOptionsResult.Fail("AgentOutputTokens:Primary must be greater than zero.");
        }

        if (Delegated <= 0)
        {
            return ValidateOptionsResult.Fail("AgentOutputTokens:Delegated must be greater than zero.");
        }

        return Primary < Delegated
            ? ValidateOptionsResult.Fail("AgentOutputTokens:Primary must be greater than or equal to Delegated.")
            : ValidateOptionsResult.Success;
    }
}
