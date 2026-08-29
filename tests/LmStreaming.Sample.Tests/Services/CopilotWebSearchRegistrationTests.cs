using System.Net;
using System.Text;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

public sealed class CopilotWebSearchRegistrationTests
{
    private const string Token = "test-copilot-token";

    [Fact]
    public void TryRegister_WhenModeEnablesWebSearch_ImportsOnlyBareHostedTool()
    {
        var registry = new FunctionRegistry();
        var upstream = new FakeCopilotMcpHandler();

        var result = CopilotWebSearchRegistration.TryRegister(
            registry,
            enabledTools: [CopilotWebSearchRegistration.ToolName],
            new StaticTokenProvider(Token),
            new CopilotSessionContext(),
            new CopilotOptions { BaseUrl = "https://copilot.test" },
            NullLoggerFactory.Instance,
            upstream
        );

        result.Registered.Should().BeTrue();
        result.Resource.Should().NotBeNull();
        // The upstream hosted tool is fetched under its wire name ("web_search"), but must be
        // exposed to the model under the renamed contract "WebSearch" — never the lowercase leak.
        RegisteredNames(registry).Should().Equal("WebSearch");
        upstream.Requests.Should().NotBeEmpty();
        upstream.Requests.Should().OnlyContain(request => request.WebSearchSelector == "web_search");
        upstream.Requests.Should().OnlyContain(request => request.Authorization == $"Bearer {Token}");
    }

    [Fact]
    public async Task TryRegister_SuccessResourceDisposesUnderlyingHttpHandler()
    {
        var upstream = new FakeCopilotMcpHandler();
        var result = Register(enabledTools: null, upstream);

        result.Resource.Should().NotBeNull();
        upstream.Disposed.Should().BeFalse();

        await result.Resource!.DisposeAsync();

        upstream.Disposed.Should().BeTrue();
    }

    [Fact]
    public void TryRegister_WhenEnabledToolsIsNull_RegistersHostedTool()
    {
        var result = Register(enabledTools: null, new FakeCopilotMcpHandler());

        result.Registered.Should().BeTrue();
        result.Resource.Should().NotBeNull();
    }

    [Theory]
    [InlineData("WebSearch")]
    [InlineData("WebFetch")]
    public void TryRegister_WhenModeDoesNotEnableExactHostedName_DoesNotConnect(string enabledTool)
    {
        var upstream = new FakeCopilotMcpHandler();

        var result = Register([enabledTool], upstream);

        result.Registered.Should().BeFalse();
        result.Resource.Should().BeNull();
        upstream.Requests.Should().BeEmpty();
    }

    [Fact]
    public void TryRegister_WhenEnabledToolsIsEmpty_DoesNotConnect()
    {
        var upstream = new FakeCopilotMcpHandler();

        var result = Register([], upstream);

        result.Registered.Should().BeFalse();
        result.Resource.Should().BeNull();
        upstream.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task TryRegister_RegisteredHandlerCallsHostedWebSearch()
    {
        var registry = new FunctionRegistry();
        var upstream = new FakeCopilotMcpHandler();
        var result = CopilotWebSearchRegistration.TryRegister(
            registry,
            enabledTools: null,
            new StaticTokenProvider(Token),
            new CopilotSessionContext(),
            new CopilotOptions { BaseUrl = "https://copilot.test" },
            NullLoggerFactory.Instance,
            upstream
        );
        var (_, handlers) = registry.Build();

        // The registered contract name is the renamed "WebSearch", not the upstream wire name.
        var handlerResult = await handlers["WebSearch"]
            ("{\"query\":\"current .NET release\"}", new ToolCallContext(), CancellationToken.None);

        handlerResult
            .Should()
            .BeOfType<ToolHandlerResult.Resolved>()
            .Which.Payload.Text.Should()
            .Contain("https://example.test/source");
        upstream.Requests.Should().Contain(request => request.Body.Contains("\"method\":\"tools/call\""));
        upstream.Requests.Should().Contain(request => request.Body.Contains("\"name\":\"web_search\""));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void TryRegister_WhenCatalogDoesNotContainExactlyOneTool_RejectsAndDisposesClient(int toolCount)
    {
        var registry = new FunctionRegistry();
        var upstream = new FakeCopilotMcpHandler(toolCount: toolCount);

        var result = CopilotWebSearchRegistration.TryRegister(
            registry,
            enabledTools: null,
            new StaticTokenProvider(Token),
            new CopilotSessionContext(),
            new CopilotOptions { BaseUrl = "https://copilot.test" },
            NullLoggerFactory.Instance,
            upstream
        );

        result.Registered.Should().BeFalse();
        result.Resource.Should().BeNull();
        RegisteredNames(registry).Should().BeEmpty();
        upstream.Disposed.Should().BeTrue();
    }

    [Fact]
    public void HostedSearchSuccess_ComposesWithJinaFetchWithoutDuplicateSearch()
    {
        var registry = new FunctionRegistry();
        var hosted = CopilotWebSearchRegistration.TryRegister(
            registry,
            enabledTools: ["web_search", "WebFetch", "WebSearch"],
            new StaticTokenProvider(Token),
            new CopilotSessionContext(),
            new CopilotOptions { BaseUrl = "https://copilot.test" },
            NullLoggerFactory.Instance,
            new FakeCopilotMcpHandler()
        );
        var webOptions = new AchieveAi.LmDotnetTools.Misc.Configuration.WebToolsOptions
        {
            JinaApiKey = "test-jina-key",
        };
        var jinaProvider = new AchieveAi.LmDotnetTools.Misc.Web.Jina.JinaWebProvider(webOptions);

        _ = WebToolRegistrationPolicy.Apply(
            registry,
            "claude-sonnet-5",
            ["web_search", "WebFetch", "WebSearch"],
            jinaProvider,
            webOptions,
            NullLoggerFactory.Instance,
            isCopilotBackedModel: true,
            suppressWebSearch: hosted.Registered
        );

        // Hosted search is exposed under the renamed "WebSearch" contract, never the lowercase leak.
        RegisteredNames(registry).Should().BeEquivalentTo("WebSearch", "WebFetch");
    }

    [Fact]
    public void WorkspaceAgentMode_RegistersRenamedWebSearchAndWebFetch_NeverLowercase()
    {
        // Mirrors the real Workspace Agent mode shape by READING it from Prompts.yaml (EnabledTools
        // carries the task family and no web names; EnabledBuiltInTools = ["web_search"]), so this
        // test tracks the mode instead of hard-coding a stale copy. Verifies the composed registry
        // exposes both "WebSearch" and "WebFetch" to the model, and never leaks the lowercase
        // upstream name.
        var workspaceMode = SystemChatModes.GetById(SystemChatModes.WorkspaceAgentModeId);
        workspaceMode!.EnabledBuiltInTools.Should().Contain("web_search");
        var registry = new FunctionRegistry();
        var enabledTools = WebToolRegistrationPolicy.ResolveEnabledTools(
            enabledTools: workspaceMode.EnabledTools,
            enabledBuiltInTools: workspaceMode.EnabledBuiltInTools
        );

        var hosted = CopilotWebSearchRegistration.TryRegister(
            registry,
            enabledTools,
            new StaticTokenProvider(Token),
            new CopilotSessionContext(),
            new CopilotOptions { BaseUrl = "https://copilot.test" },
            NullLoggerFactory.Instance,
            new FakeCopilotMcpHandler()
        );
        var webOptions = new AchieveAi.LmDotnetTools.Misc.Configuration.WebToolsOptions
        {
            JinaApiKey = "test-jina-key",
        };
        var jinaProvider = new AchieveAi.LmDotnetTools.Misc.Web.Jina.JinaWebProvider(webOptions);

        _ = WebToolRegistrationPolicy.Apply(
            registry,
            "claude-sonnet-5",
            enabledTools,
            jinaProvider,
            webOptions,
            NullLoggerFactory.Instance,
            isCopilotBackedModel: true,
            suppressWebSearch: hosted.Registered
        );

        var names = RegisteredNames(registry);
        names.Should().BeEquivalentTo("WebSearch", "WebFetch");
        names.Should().NotContain("web_search");
    }

    [Fact]
    public void HostedSearchFailure_ComposesWithJinaSearchAndFetchFallback()
    {
        var registry = new FunctionRegistry();
        var hosted = CopilotWebSearchRegistration.TryRegister(
            registry,
            enabledTools: ["web_search", "WebFetch", "WebSearch"],
            new StaticTokenProvider(Token),
            new CopilotSessionContext(),
            new CopilotOptions { BaseUrl = "https://copilot.test" },
            NullLoggerFactory.Instance,
            new FakeCopilotMcpHandler(failInitialization: true)
        );
        var webOptions = new AchieveAi.LmDotnetTools.Misc.Configuration.WebToolsOptions
        {
            JinaApiKey = "test-jina-key",
        };
        var jinaProvider = new AchieveAi.LmDotnetTools.Misc.Web.Jina.JinaWebProvider(webOptions);

        _ = WebToolRegistrationPolicy.Apply(
            registry,
            "claude-sonnet-5",
            ["web_search", "WebFetch", "WebSearch"],
            jinaProvider,
            webOptions,
            NullLoggerFactory.Instance,
            isCopilotBackedModel: true,
            suppressWebSearch: hosted.Registered
        );

        RegisteredNames(registry).Should().BeEquivalentTo("WebSearch", "WebFetch");
    }

    [Fact]
    public void TryRegister_WhenInitializationFails_DegradesWithoutRegisteringTool()
    {
        var registry = new FunctionRegistry();
        var upstream = new FakeCopilotMcpHandler(failInitialization: true);

        var result = CopilotWebSearchRegistration.TryRegister(
            registry,
            enabledTools: null,
            new StaticTokenProvider(Token),
            new CopilotSessionContext(),
            new CopilotOptions { BaseUrl = "https://copilot.test" },
            NullLoggerFactory.Instance,
            upstream
        );

        result.Registered.Should().BeFalse();
        result.Resource.Should().BeNull();
        RegisteredNames(registry).Should().BeEmpty();
        result.Status.Should().NotContain(Token);
    }

    private static CopilotWebSearchRegistrationResult Register(
        IReadOnlyList<string>? enabledTools,
        FakeCopilotMcpHandler upstream
    )
    {
        return CopilotWebSearchRegistration.TryRegister(
            new FunctionRegistry(),
            enabledTools,
            new StaticTokenProvider(Token),
            new CopilotSessionContext(),
            new CopilotOptions { BaseUrl = "https://copilot.test" },
            NullLoggerFactory.Instance,
            upstream
        );
    }

    private static IReadOnlyList<string> RegisteredNames(FunctionRegistry registry)
    {
        var (contracts, _) = registry.Build();
        return [.. contracts.Select(contract => contract.Name)];
    }

    private sealed class StaticTokenProvider(string token) : ICopilotTokenProvider
    {
        public Task<string> GetTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(token);
    }

    private sealed class FakeCopilotMcpHandler(bool failInitialization = false, int toolCount = 1) : HttpMessageHandler
    {
        private const string SessionId = "test-mcp-session";

        public List<CapturedRequest> Requests { get; } = [];
        public bool Disposed { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(
                new CapturedRequest(
                    request.Headers.Authorization?.ToString(),
                    request.Headers.TryGetValues("X-MCP-Tools", out var selectors) ? selectors.Single() : null,
                    body
                )
            );

            if (failInitialization)
            {
                return new HttpResponseMessage(HttpStatusCode.BadGateway)
                {
                    Content = new StringContent("upstream unavailable", Encoding.UTF8, "text/plain"),
                };
            }

            var method = ReadMethod(body);
            return method switch
            {
                "initialize" => Sse(
                    "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"capabilities\":{\"tools\":{}},\"protocolVersion\":\"2025-06-18\",\"serverInfo\":{\"name\":\"copilot-test\",\"version\":\"1\"}}}",
                    includeSession: true
                ),
                "notifications/initialized" => new HttpResponseMessage(HttpStatusCode.Accepted),
                "tools/list" => Sse(ToolsListResponse(toolCount)),
                "tools/call" => Sse(
                    "{\"jsonrpc\":\"2.0\",\"id\":3,\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"Current answer [1]\\n\\nSources: https://example.test/source\"}]}}"
                ),
                _ => new HttpResponseMessage(HttpStatusCode.Accepted),
            };
        }

        private static string ToolsListResponse(int count)
        {
            const string tool =
                "{\"name\":\"web_search\",\"description\":\"Search the web with citations\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}},\"required\":[\"query\"]}}";
            return $"{{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{{\"tools\":[{string.Join(",", Enumerable.Repeat(tool, count))}]}}}}";
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        private static string? ReadMethod(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("method", out var method) ? method.GetString() : null;
        }

        private static HttpResponseMessage Sse(string json, bool includeSession = false)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"event: message\ndata: {json}\n\n", Encoding.UTF8, "text/event-stream"),
            };
            if (includeSession)
            {
                response.Headers.TryAddWithoutValidation("Mcp-Session-Id", SessionId);
            }

            return response;
        }
    }

    private sealed record CapturedRequest(string? Authorization, string? WebSearchSelector, string Body);
}
