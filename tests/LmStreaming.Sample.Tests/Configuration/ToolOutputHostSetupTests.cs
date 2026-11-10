using LmStreaming.Sample.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

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

    [Fact]
    public void ALimitBelowTheMinimum_StopsTheHostFromStarting()
    {
        using var host = new BadLimitHost();

        // Program.cs logs the reason and exits without a host, which is all a caller can see of it.
        var act = () => host.Services;

        act.Should().Throw<InvalidOperationException>().WithMessage("*entry point exited*");
    }

    private sealed class BadLimitHost : WebApplicationFactory<Program>
    {
        public BadLimitHost() => Environment.SetEnvironmentVariable("LM_PROVIDER_MODE", "test");

        // Program.cs reads this before it builds, so it has to arrive as a host argument.
        protected override IHost CreateHost(IHostBuilder builder)
        {
            _ = builder.ConfigureHostConfiguration(config =>
                config.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        [ToolOutputHostSetup.MaxInlineBytesKey] = "1",
                        ["SandboxGateway:BaseUrl"] = "http://127.0.0.1:1",
                        ["SandboxGateway:AutoSpawn"] = "false",
                    }
                )
            );
            return base.CreateHost(builder);
        }
    }
}
