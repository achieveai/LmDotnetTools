using System.Reflection;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Agents;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.CodexSdkProvider.Tests.Agents;

public sealed class CodexAppServerRequestConfigTests
{
    private const string ProviderId = "lm-dotnet-tools-openai-responses";

    [Theory]
    [InlineData("BuildThreadStartParams")]
    [InlineData("BuildThreadResumeParams")]
    public void BuildThreadParams_BaseUrlSet_RoutesThroughExplicitResponsesModelProvider(string methodName)
    {
        var options = new CodexBridgeInitOptions
        {
            Model = "gpt-5.3-codex",
            ApprovalPolicy = "never",
            SandboxMode = "read-only",
            WorkingDirectory = @"B:\sources\LmDotnetTools",
            BaseUrl = "http://127.0.0.1:5099/v1",
            ApiKey = "mock-token",
            ThreadId = "thread-1",
        };

        var parameters = InvokeThreadParams(methodName, options);

        parameters["modelProvider"].Should().Be(ProviderId);
        var config = parameters["config"].Should().BeAssignableTo<Dictionary<string, object?>>().Subject;
        config["model_provider"].Should().Be(ProviderId);

        var modelProviders = config["model_providers"]
            .Should()
            .BeAssignableTo<Dictionary<string, object?>>()
            .Subject;
        var provider = modelProviders[ProviderId]
            .Should()
            .BeAssignableTo<Dictionary<string, object?>>()
            .Subject;

        provider["name"].Should().Be("LmDotnetTools OpenAI Responses");
        provider["base_url"].Should().Be("http://127.0.0.1:5099/v1");
        provider["wire_api"].Should().Be("responses");
        provider["env_key"].Should().Be("OPENAI_API_KEY");
        provider["requires_openai_auth"].Should().Be(false);
        provider["supports_websockets"].Should().Be(true);
        provider.Should().NotContainKey("experimental_bearer_token");
    }

    [Fact]
    public void BuildThreadParams_BaseUrlWithoutApiKey_DoesNotRequireEnvKey()
    {
        var options = new CodexBridgeInitOptions
        {
            Model = "gpt-5.3-codex",
            BaseUrl = "http://127.0.0.1:5099/v1",
        };

        var parameters = InvokeThreadParams("BuildThreadStartParams", options);
        var config = parameters["config"].Should().BeAssignableTo<Dictionary<string, object?>>().Subject;
        var modelProviders = config["model_providers"]
            .Should()
            .BeAssignableTo<Dictionary<string, object?>>()
            .Subject;
        var provider = modelProviders[ProviderId]
            .Should()
            .BeAssignableTo<Dictionary<string, object?>>()
            .Subject;

        provider.Should().NotContainKey("env_key");
    }

    private static Dictionary<string, object?> InvokeThreadParams(
        string methodName,
        CodexBridgeInitOptions options)
    {
        var client = new CodexSdkClient(
            new CodexSdkOptions(),
            NullLogger<CodexSdkClient>.Instance);
        var method = typeof(CodexSdkClient).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(CodexSdkClient), methodName);

        return method.Invoke(client, [options])
                .Should()
                .BeAssignableTo<Dictionary<string, object?>>()
                .Subject;
    }
}
