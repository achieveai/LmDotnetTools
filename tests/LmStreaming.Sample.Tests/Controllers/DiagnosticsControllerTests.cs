namespace LmStreaming.Sample.Tests.Controllers;

public class DiagnosticsControllerTests
{
    [Fact]
    public async Task GetProviderInfo_ReturnsCodexFields_WhenCodexModeEnabled()
    {
        var previousMode = Environment.GetEnvironmentVariable("LM_PROVIDER_MODE");
        var previousModel = Environment.GetEnvironmentVariable("CODEX_MODEL");
        var previousApiKey = Environment.GetEnvironmentVariable("CODEX_API_KEY");
        var previousCliPath = Environment.GetEnvironmentVariable("CODEX_CLI_PATH");
        var previousRpcTraceEnabled = Environment.GetEnvironmentVariable("CODEX_RPC_TRACE_ENABLED");
        var previousMcpPortEffective = Environment.GetEnvironmentVariable("CODEX_MCP_PORT_EFFECTIVE");

        try
        {
            Environment.SetEnvironmentVariable("LM_PROVIDER_MODE", "codex");
            Environment.SetEnvironmentVariable("CODEX_MODEL", "gpt-5.3-codex");
            Environment.SetEnvironmentVariable("CODEX_API_KEY", "test-key");
            Environment.SetEnvironmentVariable("CODEX_CLI_PATH", "/path/does/not/exist/codex");
            Environment.SetEnvironmentVariable("CODEX_RPC_TRACE_ENABLED", "true");
            Environment.SetEnvironmentVariable("CODEX_MCP_PORT_EFFECTIVE", "49200");

            var controller = new DiagnosticsController(NullLogger<DiagnosticsController>.Instance);
            var result = await controller.GetProviderInfo();

            var ok = Assert.IsType<OkObjectResult>(result);
            var payloadJson = JsonSerializer.Serialize(ok.Value);

            payloadJson.Should().Contain("\"providerMode\":\"codex\"");
            payloadJson.Should().Contain("\"model\":\"gpt-5.3-codex\"");
            payloadJson.Should().Contain("\"apiKeyConfigured\":\"True\"");
            payloadJson.Should().Contain("\"authMode\":\"api_key\"");
            payloadJson.Should().Contain("\"mcpEndpointUrl\"");
            payloadJson.Should().Contain("\"mcpPortEffective\":\"49200\"");
            payloadJson.Should().Contain("\"rpcTraceEnabled\":\"True\"");
            payloadJson.Should().Contain("\"codexCliDetected\":\"False\"");
            payloadJson.Should().Contain("\"appServerHandshakeOk\":\"False\"");
            payloadJson.Should().Contain("\"appServerLastError\"");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LM_PROVIDER_MODE", previousMode);
            Environment.SetEnvironmentVariable("CODEX_MODEL", previousModel);
            Environment.SetEnvironmentVariable("CODEX_API_KEY", previousApiKey);
            Environment.SetEnvironmentVariable("CODEX_CLI_PATH", previousCliPath);
            Environment.SetEnvironmentVariable("CODEX_RPC_TRACE_ENABLED", previousRpcTraceEnabled);
            Environment.SetEnvironmentVariable("CODEX_MCP_PORT_EFFECTIVE", previousMcpPortEffective);
        }
    }
}
