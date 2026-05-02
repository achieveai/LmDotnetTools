using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.TestDoubles;

namespace LmStreaming.Sample.Tests.Controllers;

[Collection("EnvironmentVariables")]
public class ProvidersControllerTests
{
    [Fact]
    public void List_ReturnsCatalog_AndDefault()
    {
        var snapshot = SnapshotEnv();
        try
        {
            Environment.SetEnvironmentVariable("LM_PROVIDER_MODE", "openai");
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "sk-test");
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            Environment.SetEnvironmentVariable("CLAUDE_CLI_PATH", null);
            Environment.SetEnvironmentVariable("COPILOT_CLI_PATH", null);

            var registry = new ProviderRegistry(new FakeFileSystemProbe());
            var controller = new ProvidersController(registry);

            var result = controller.List();
            var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
            var response = ok.Value.Should().BeOfType<ProvidersResponse>().Subject;

            response.Default.Should().Be("openai");
            response.Providers.Select(p => p.Id).Should().Contain(
                ["openai", "anthropic", "claude", "codex", "copilot", "test", "test-anthropic"]);

            var openai = response.Providers.Single(p => p.Id == "openai");
            openai.Available.Should().BeTrue();
            openai.DisplayName.Should().Be("OpenAI");

            var anthropic = response.Providers.Single(p => p.Id == "anthropic");
            anthropic.Available.Should().BeFalse();
        }
        finally
        {
            RestoreEnv(snapshot);
        }
    }

    [Fact]
    public void List_DefaultsToTestMode_WhenEnvVarUnset()
    {
        var snapshot = SnapshotEnv();
        try
        {
            Environment.SetEnvironmentVariable("LM_PROVIDER_MODE", null);

            var registry = new ProviderRegistry(new FakeFileSystemProbe());
            var controller = new ProvidersController(registry);

            var result = controller.List();
            var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
            var response = ok.Value.Should().BeOfType<ProvidersResponse>().Subject;

            response.Default.Should().Be("test");
        }
        finally
        {
            RestoreEnv(snapshot);
        }
    }

    private static Dictionary<string, string?> SnapshotEnv() => new()
    {
        ["LM_PROVIDER_MODE"] = Environment.GetEnvironmentVariable("LM_PROVIDER_MODE"),
        ["OPENAI_API_KEY"] = Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
        ["ANTHROPIC_API_KEY"] = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"),
        ["CLAUDE_CLI_PATH"] = Environment.GetEnvironmentVariable("CLAUDE_CLI_PATH"),
        ["COPILOT_CLI_PATH"] = Environment.GetEnvironmentVariable("COPILOT_CLI_PATH"),
    };

    private static void RestoreEnv(Dictionary<string, string?> snapshot)
    {
        foreach (var (k, v) in snapshot)
        {
            Environment.SetEnvironmentVariable(k, v);
        }
    }
}
