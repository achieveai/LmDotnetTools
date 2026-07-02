using AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Agents;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.GithubCopilotProvider.Tests.Agents;

/// <summary>
///     Guards the time-to-first-response ceiling on the Copilot-backed Anthropic agent: a caller-supplied
///     timeout must reach the underlying <see cref="System.Net.Http.HttpClient"/>, and omitting it must
///     leave the 5-minute default intact. Because the streaming client reads with
///     <c>ResponseHeadersRead</c>, this timeout bounds a dead/stuck upstream connection (the blocking
///     sub-agent hang we fixed) without capping a healthy stream's duration.
/// </summary>
public sealed class CopilotAnthropicAgentFactoryTimeoutTests
{
    private sealed class StubTokenProvider : ICopilotTokenProvider
    {
        public Task<string> GetTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("gho_test_token");
    }

    [Fact]
    public void Create_ForwardsSuppliedTimeout_OntoTheHttpClient()
    {
        var agent = CopilotAnthropicAgentFactory.Create(
            "Sonnet",
            new StubTokenProvider(),
            timeout: TimeSpan.FromSeconds(42)
        );

        HttpClientOf(agent).Timeout.Should().Be(TimeSpan.FromSeconds(42));
    }

    [Fact]
    public void Create_WithoutTimeout_KeepsTheFiveMinuteDefault()
    {
        var agent = CopilotAnthropicAgentFactory.Create("Sonnet", new StubTokenProvider());

        HttpClientOf(agent).Timeout.Should().Be(TimeSpan.FromMinutes(5));
    }

    // The AnthropicAgent -> AnthropicClient -> HttpClient chain is private; reflect through it so the test
    // asserts on the REAL client the factory built rather than a re-derived value.
    private static HttpClient HttpClientOf(AnthropicAgent agent)
    {
        var client = GetPrivateField(agent, "_client")
            ?? throw new InvalidOperationException("AnthropicAgent._client not found.");
        var httpClient = GetPrivateField(client, "HttpClient") ?? GetPrivateField(client, "_httpClient")
            ?? throw new InvalidOperationException("AnthropicClient HttpClient not found.");
        return (HttpClient)httpClient;
    }

    private static object? GetPrivateField(object instance, string name)
    {
        var type = instance.GetType();
        while (type is not null)
        {
            var field = type.GetField(
                name,
                System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Public
            );
            if (field is not null)
            {
                return field.GetValue(instance);
            }

            var prop = type.GetProperty(
                name,
                System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Public
            );
            if (prop is not null)
            {
                return prop.GetValue(instance);
            }

            type = type.BaseType;
        }

        return null;
    }
}
