using LmStreaming.Sample.Configuration;

namespace LmStreaming.Sample.Tests.Configuration;

public sealed class AgentOutputTokenOptionsTests
{
    [Fact]
    public void Defaults_ArePrimary24K_AndDelegated16K()
    {
        var options = new AgentOutputTokenOptions();

        options.Primary.Should().Be(24_576);
        options.Delegated.Should().Be(16_384);
        options.Validate().Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 16_384)]
    [InlineData(24_576, 0)]
    [InlineData(-1, 16_384)]
    public void Validate_RejectsNonPositiveValues(int primary, int delegated)
    {
        new AgentOutputTokenOptions { Primary = primary, Delegated = delegated }
            .Validate().Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_RejectsPrimaryBelowDelegated()
    {
        new AgentOutputTokenOptions { Primary = 16_383, Delegated = 16_384 }
            .Validate().Failed.Should().BeTrue();
    }
}
