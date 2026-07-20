using AchieveAi.LmDotnetTools.LmCore.Models;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Models;

public class ModelPricingTests
{
    [Fact]
    public void EstimateMicros_ComputesCostInMicroUnits()
    {
        // $2 / M input, $8 / M output. 1000 input + 500 output => $0.006 => 6000 micro-units.
        var pricing = new ModelPricing
        {
            ModelId = "model-A",
            PromptPerMillion = 2m,
            CompletionPerMillion = 8m,
        };

        pricing.EstimateMicros(inputTokens: 1000, outputTokens: 500).Should().Be(6000);
    }

    [Fact]
    public void EstimateMicros_IsZero_ForNoTokens()
    {
        var pricing = new ModelPricing
        {
            ModelId = "model-A",
            PromptPerMillion = 2m,
            CompletionPerMillion = 8m,
        };

        pricing.EstimateMicros(0, 0).Should().Be(0);
    }
}
