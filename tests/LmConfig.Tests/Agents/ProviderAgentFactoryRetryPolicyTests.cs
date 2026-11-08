using System.Net;
using AchieveAi.LmDotnetTools.LmConfig.Agents;
using AchieveAi.LmDotnetTools.LmConfig.Http;
using AchieveAi.LmDotnetTools.LmConfig.Models;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Http;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProviderConfig = AchieveAi.LmDotnetTools.LmConfig.Models.ProviderConfig;

namespace LmConfig.Tests.Agents;

/// <summary>
///     A host's provider retry policy, registered as a <see cref="RetryOptions" /> in DI, reaches the client
///     each LmConfig-built agent talks through (#930 P1 item 1). With <c>MaxRetries: 0</c> a rate-limited
///     call goes out once and fails with its own status; the client's default would send it three times.
/// </summary>
public class ProviderAgentFactoryRetryPolicyTests
{
    /// <summary>Answers every request 429 and counts them. Stands in for the whole handler pipeline.</summary>
    private sealed class RateLimitedHandler : HttpMessageHandler
    {
        private int _sends;

        public int Sends => Volatile.Read(ref _sends);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            _ = Interlocked.Increment(ref _sends);
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("""{"error":{"message":"rate limited"}}"""),
                }
            );
        }
    }

    private sealed class FixedHandlerBuilder(HttpMessageHandler handler) : IHttpHandlerBuilder
    {
        public HttpMessageHandler Build(HttpMessageHandler innerMost, ILogger? logger = null) => handler;
    }

    [Theory]
    [InlineData("OpenAI")]
    [InlineData("Anthropic")]
    public async Task ARegisteredZeroRetryPolicy_SendsARateLimitedCallOnce(string provider)
    {
        var apiKeyVariable = $"LMCONFIG_RETRY_POLICY_TEST_KEY_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(apiKeyVariable, "test-key");
        try
        {
            var handler = new RateLimitedHandler();
            var services = new ServiceCollection();
            _ = services.AddSingleton(RetryOptions.Default with { MaxRetries = 0 });
            using var serviceProvider = services.BuildServiceProvider();
            var factory = new ProviderAgentFactory(serviceProvider, new FixedHandlerBuilder(handler));

            var agent = factory.CreateAgent(Resolution(provider, apiKeyVariable));
            var call = () =>
                agent.GenerateReplyAsync(
                    [new TextMessage { Text = "hi", Role = Role.User }],
                    new GenerateReplyOptions { ModelId = "model-x" }
                );

            var thrown = await Assert.ThrowsAsync<HttpRequestException>(call);

            Assert.Equal(HttpStatusCode.TooManyRequests, thrown.StatusCode);
            Assert.Equal(1, handler.Sends);
        }
        finally
        {
            Environment.SetEnvironmentVariable(apiKeyVariable, null);
        }
    }

    private static ProviderResolution Resolution(string provider, string apiKeyVariable)
    {
        var providerConfig = new ProviderConfig
        {
            Name = provider,
            ModelName = "model-x",
            Pricing = new PricingConfig { PromptPerMillion = 0, CompletionPerMillion = 0 },
        };

        return new ProviderResolution
        {
            Model = new ModelConfig { Id = "model-x", Providers = [providerConfig] },
            Provider = providerConfig,
            Connection = new ProviderConnectionInfo
            {
                EndpointUrl = "http://provider.test/v1",
                ApiKeyEnvironmentVariable = apiKeyVariable,
                Compatibility = provider,
            },
        };
    }
}
