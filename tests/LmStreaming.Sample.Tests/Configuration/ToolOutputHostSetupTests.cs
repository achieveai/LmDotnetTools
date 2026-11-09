using LmStreaming.Sample.Configuration;
using Microsoft.Extensions.Configuration;

namespace LmStreaming.Sample.Tests.Configuration;

/// <summary>A bad tool output limit has to stop the host at startup, not fail each new sandbox chat.</summary>
public class ToolOutputHostSetupTests
{
    private static IConfiguration WithLimit(string? value) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [ToolOutputHostSetup.MaxInlineBytesKey] = value })
            .Build();

    [Fact]
    public void AnAbsentLimit_IsTheLibraryDefault() =>
        ToolOutputHostSetup
            .ReadMaxInlineBytes(new ConfigurationBuilder().Build())
            .Should()
            .Be(ToolOutputSpillOptions.DefaultMaxInlineBytes);

    [Fact]
    public void AConfiguredLimit_IsUsed() =>
        ToolOutputHostSetup.ReadMaxInlineBytes(WithLimit("2048")).Should().Be(2048);

    [Fact]
    public void ALimitBelowTheMinimum_IsRefusedAndNamesTheSetting()
    {
        var act = () => ToolOutputHostSetup.ReadMaxInlineBytes(WithLimit("1023"));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{ToolOutputHostSetup.MaxInlineBytesKey}*");
    }
}
