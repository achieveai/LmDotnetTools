using FluentAssertions;

namespace AchieveAi.LmDotnetTools.MockProviderHost.E2E.Tests.Infrastructure;

/// <summary>
/// Pure file-emission tests for <see cref="IsolatedCodexHome"/>. These do not spawn the Codex
/// CLI, so they run in default CI alongside the rest of the suite. They guard against regressions
/// in the routing-config payload that turned out to be load-bearing on Codex 0.125+ (the CLI
/// ignores <c>OPENAI_BASE_URL</c> for the default provider, so the <c>config.toml</c> we write is
/// what redirects the CLI onto plain HTTP <c>/v1/chat/completions</c>).
/// </summary>
[Collection(CodexHomeCollection.Name)]
public sealed class IsolatedCodexHomeTests
{
    [Fact]
    public void Without_baseUrl_writes_authJson_only_no_configToml()
    {
        using var home = new IsolatedCodexHome("test-token");

        File.Exists(Path.Combine(home.HomePath, "auth.json")).Should().BeTrue();
        File.Exists(Path.Combine(home.HomePath, "config.toml"))
            .Should()
            .BeFalse("no mock URL was supplied, so we have no provider redirect to install");

        var auth = File.ReadAllText(Path.Combine(home.HomePath, "auth.json"));
        auth.Should().Contain("\"auth_mode\": \"apikey\"");
        auth.Should().Contain("\"OPENAI_API_KEY\": \"test-token\"");
    }

    [Fact]
    public void With_baseUrl_writes_configToml_selecting_mock_provider_on_chat_wireApi()
    {
        const string url = "http://127.0.0.1:54321/v1";
        using var home = new IsolatedCodexHome("test-token", url);

        var configPath = Path.Combine(home.HomePath, "config.toml");
        File.Exists(configPath).Should().BeTrue();

        var config = File.ReadAllText(configPath);
        config
            .Should()
            .Contain(
                "model_provider = \"mock\"",
                "the CLI must default to our redirected provider rather than the hardcoded openai default"
            );
        config.Should().Contain("[model_providers.mock]");
        config
            .Should()
            .Contain($"base_url = \"{url}\"", "the CLI binds to this URL for /chat/completions on a real TCP port");
        config
            .Should()
            .Contain(
                "wire_api = \"chat\"",
                "the mock host serves OpenAI Chat Completions, not the WebSocket /responses shape"
            );
        config
            .Should()
            .Contain(
                "env_key = \"OPENAI_API_KEY\"",
                "the SDK passes the API key via OPENAI_API_KEY; Codex must read from the same var"
            );
    }

    [Fact]
    public void Sets_and_restores_CODEX_HOME_env_var()
    {
        const string sentinel = "lmdotnet-codex-test-sentinel";
        var previous = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", sentinel);

            string capturedDuring;
            using (var home = new IsolatedCodexHome("token"))
            {
                capturedDuring = Environment.GetEnvironmentVariable("CODEX_HOME") ?? string.Empty;
                capturedDuring
                    .Should()
                    .Be(home.HomePath, "CODEX_HOME must point at the isolated home so the spawned CLI inherits it");
            }

            Environment
                .GetEnvironmentVariable("CODEX_HOME")
                .Should()
                .Be(
                    sentinel,
                    "Dispose must restore the prior CODEX_HOME so concurrent / sequential tests do not leak state"
                );
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previous);
        }
    }

    [Fact]
    public void Dispose_clears_CODEX_HOME_when_no_previous_value_existed()
    {
        var previous = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", null);

            using (var _ = new IsolatedCodexHome("token")) { }

            Environment
                .GetEnvironmentVariable("CODEX_HOME")
                .Should()
                .BeNull("an empty 'previous' value must round-trip back to unset, not to an empty string");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previous);
        }
    }

    [Fact]
    public void TOML_string_writer_escapes_backslash_and_quote()
    {
        // Defensive: we don't expect either character in a typical fixture URL, but a future
        // Windows-style pipe URL could contain backslashes. Failing to escape would silently
        // produce an invalid TOML file (Codex would reject it at startup with an opaque error).
        const string awkwardUrl = "http://example.invalid/\"quoted\"\\path";
        using var home = new IsolatedCodexHome("token", awkwardUrl);

        var config = File.ReadAllText(Path.Combine(home.HomePath, "config.toml"));
        config.Should().Contain("base_url = \"http://example.invalid/\\\"quoted\\\"\\\\path\"");
    }
}
