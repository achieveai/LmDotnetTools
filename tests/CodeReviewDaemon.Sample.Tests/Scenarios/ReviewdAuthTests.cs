extern alias Reviewd;

using Microsoft.Extensions.Configuration;
using Reviewd::Reviewd.Sample;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

public sealed class ReviewdAuthTests
{
    [Fact]
    public void Default_callback_uses_the_Gateway_accepted_loopback_host()
    {
        using var config = new ReviewdConfig(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Auth:TokenStoreDir"] = Path.GetTempPath(),
                        ["Auth:Github:ClientId"] = "test-client",
                    }
                )
                .Build()
        );

        var auth = AuthCommands.LoadOptions(config, callback: null);

        auth.Webhook.CallbackBaseUrl.Should().Be("http://localhost:5203");
    }
}
