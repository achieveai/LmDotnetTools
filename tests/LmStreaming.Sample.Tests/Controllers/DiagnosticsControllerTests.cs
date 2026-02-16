namespace LmStreaming.Sample.Tests.Controllers;

public class DiagnosticsControllerTests
{
    [Fact]
    public void GetProviderInfo_ReturnsCodexFields_WhenCodexModeEnabled()
    {
        var previousMode = Environment.GetEnvironmentVariable("LM_PROVIDER_MODE");
        var previousModel = Environment.GetEnvironmentVariable("CODEX_MODEL");
        var previousApiKey = Environment.GetEnvironmentVariable("CODEX_API_KEY");

        try
        {
            Environment.SetEnvironmentVariable("LM_PROVIDER_MODE", "codex");
            Environment.SetEnvironmentVariable("CODEX_MODEL", "gpt-5.3-codex");
            Environment.SetEnvironmentVariable("CODEX_API_KEY", "test-key");

            var controller = new DiagnosticsController(NullLogger<DiagnosticsController>.Instance);
            var result = controller.GetProviderInfo();

            var ok = Assert.IsType<OkObjectResult>(result);
            var payloadJson = JsonSerializer.Serialize(ok.Value);

            payloadJson.Should().Contain("\"providerMode\":\"codex\"");
            payloadJson.Should().Contain("\"model\":\"gpt-5.3-codex\"");
            payloadJson.Should().Contain("\"apiKeyConfigured\":\"True\"");
            payloadJson.Should().Contain("\"authMode\":\"api_key\"");
            payloadJson.Should().Contain("\"mcpEndpointUrl\"");
            payloadJson.Should().Contain("\"bridgeDependencyState\"");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LM_PROVIDER_MODE", previousMode);
            Environment.SetEnvironmentVariable("CODEX_MODEL", previousModel);
            Environment.SetEnvironmentVariable("CODEX_API_KEY", previousApiKey);
        }
    }
}
