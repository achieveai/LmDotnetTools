using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.CopilotAnthropicProxy.Tests;

public class McpToolCompositionTests
{
    [Fact]
    public async Task ToolsList_GithubDefinitionWinsAndMissingFetchUsesJina()
    {
        var githubBody = """
            {"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"web_search","description":"github","inputSchema":{"type":"object","properties":{"q":{"type":"string"}}},"unknown":"kept"}]}}
            """;
        await using var factory = CreateFactory(githubBody);
        using var client = factory.CreateClient();

        using var response = await SendAsync(client, "/mcp", ListRequest(), sessionId: "session-1");
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var tools = body["result"]!["tools"]!.AsArray();

        tools.Should().HaveCount(2);
        tools.Single(t => t!["name"]!.GetValue<string>() == "web_search")!["unknown"]!.GetValue<string>().Should().Be("kept");
        tools.Single(t => t!["name"]!.GetValue<string>() == "web_fetch").Should().NotBeNull();
    }

    [Fact]
    public async Task ToolsList_NoJinaKeyIsByteTransparent()
    {
        const string githubBody = "{ \"jsonrpc\": \"2.0\", \"id\": 1, \"result\": { \"tools\": [] } }";
        await using var factory = new ProxyWebAppFactory((_, _) => Task.FromResult(TestUpstream.Json(githubBody)));
        using var client = factory.CreateClient();

        using var response = await SendAsync(client, "/mcp", ListRequest(), sessionId: "session-1");

        (await response.Content.ReadAsStringAsync()).Should().Be(githubBody);
    }

    [Fact]
    public async Task ToolsList_AllowlistDoesNotReintroduceFilteredTool()
    {
        await using var factory = CreateFactory(EmptyListResponse());
        using var client = factory.CreateClient();

        using var response = await SendAsync(
            client,
            "/mcp",
            ListRequest(),
            sessionId: "session-1",
            headers: new Dictionary<string, string> { ["X-MCP-Tools"] = "web_search" }
        );
        var tools = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["result"]!["tools"]!.AsArray();

        tools.Select(t => t!["name"]!.GetValue<string>()).Should().ContainSingle().Which.Should().Be("web_search");
    }

    [Fact]
    public async Task ToolsList_GithubFailuresPassThroughWithoutJinaFallback()
    {
        var jinaCalls = 0;
        const string rpcError = "{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":-32000,\"message\":\"failed\"}}";
        await using var rpcFactory = CreateFactory(
            rpcError,
            jina: (_, _) =>
            {
                jinaCalls++;
                return Task.FromResult(TestUpstream.Json("{\"data\":[]}"));
            }
        );
        using var rpcClient = rpcFactory.CreateClient();

        using var rpcResponse = await SendAsync(rpcClient, "/mcp", ListRequest(), "session-1");
        (await rpcResponse.Content.ReadAsStringAsync()).Should().Be(rpcError);

        const string httpError = "{\"error\":\"upstream\"}";
        await using var httpFactory = new ProxyWebAppFactory(
            (_, _) => Task.FromResult(TestUpstream.Json(httpError, HttpStatusCode.InternalServerError)),
            jinaApiKey: "jina-key",
            jinaUpstream: (_, _) =>
            {
                jinaCalls++;
                return Task.FromResult(TestUpstream.Json("{\"data\":[]}"));
            }
        );
        using var httpClient = httpFactory.CreateClient();
        using var httpResponse = await SendAsync(httpClient, "/mcp", ListRequest(), "session-2");

        httpResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await httpResponse.Content.ReadAsStringAsync()).Should().Be(httpError);
        jinaCalls.Should().Be(0);
    }

    [Fact]
    public async Task ToolsList_SseMessageComposesLocalFallbackAndPreservesFraming()
    {
        var jinaCalls = 0;
        const string sse = "event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"tools\":[]}}\n\n";
        await using var factory = new ProxyWebAppFactory(
            (_, _) => Task.FromResult(TestUpstream.Sse(sse)),
            jinaApiKey: "jina-key",
            jinaUpstream: (_, _) =>
            {
                jinaCalls++;
                return Task.FromResult(TestUpstream.Json("{\"data\":[]}"));
            }
        );
        using var client = factory.CreateClient();

        using var list = await SendAsync(client, "/mcp", ListRequest(), "session-1");
        var listed = await list.Content.ReadAsStringAsync();
        listed.Should().StartWith("event: message\ndata: ").And.EndWith("\n\n");
        var data = listed.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..];
        var tools = JsonNode.Parse(data)!["result"]!["tools"]!.AsArray();
        tools.Select(tool => tool!["name"]!.GetValue<string>()).Should().BeEquivalentTo("web_search", "web_fetch");

        using var call = await SendAsync(
            client,
            "/mcp",
            CallRequest("web_search", 2, "{\"query\":\"status\"}"),
            "session-1"
        );
        jinaCalls.Should().Be(1);
    }

    [Fact]
    public async Task ToolsList_BatchPassesThroughWithoutLocalOwnership()
    {
        var githubCalls = 0;
        await using var factory = new ProxyWebAppFactory(
            (_, _) =>
            {
                githubCalls++;
                return Task.FromResult(TestUpstream.Json("{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"content\":[]}}"));
            },
            jinaApiKey: "jina-key",
            jinaUpstream: (_, _) => throw new InvalidOperationException("Jina must not run")
        );
        using var client = factory.CreateClient();

        var batch = $"[{ListRequest()}]";
        using var batchResponse = await SendAsync(client, "/mcp", batch, "session-1");
        githubCalls.Should().Be(1);
    }

    [Fact]
    public async Task ToolsCall_GithubOwnedFailureNeverFallsBackToJina()
    {
        var jinaCalls = 0;
        var githubCalls = 0;
        var catalog = "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"tools\":[{\"name\":\"web_search\",\"inputSchema\":{\"type\":\"object\"}}]}}";
        var error = "{\"jsonrpc\":\"2.0\",\"id\":2,\"error\":{\"code\":-32000,\"message\":\"github failed\"}}";
        await using var factory = CreateFactory(
            catalog,
            github: (_, _) => Task.FromResult(TestUpstream.Json(++githubCalls == 1 ? catalog : error)),
            jina: (_, _) =>
            {
                jinaCalls++;
                return Task.FromResult(TestUpstream.Json("{\"data\":[]}"));
            }
        );
        using var client = factory.CreateClient();

        using var list = await SendAsync(client, "/mcp", ListRequest(), "session-1");
        using var call = await SendAsync(client, "/mcp", CallRequest("web_search", 2), "session-1");

        (await call.Content.ReadAsStringAsync()).Should().Be(error);
        githubCalls.Should().Be(2);
        jinaCalls.Should().Be(0);
    }

    [Fact]
    public async Task ToolsCall_MatchingFilterHeadersDispatchLocally()
    {
        var jinaCalls = 0;
        await using var factory = CreateFactory(
            EmptyListResponse(),
            jina: (_, _) =>
            {
                jinaCalls++;
                return Task.FromResult(TestUpstream.Json("{\"data\":[]}"));
            }
        );
        using var client = factory.CreateClient();
        var headers = new Dictionary<string, string> { ["X-MCP-Tools"] = "web_search" };

        using var list = await SendAsync(client, "/mcp", ListRequest(), "session-1", headers);
        using var call = await SendAsync(client, "/mcp", CallRequest("web_search", 2, "{\"query\":\"status\"}"), "session-1", headers);

        jinaCalls.Should().Be(1);
    }

    [Fact]
    public async Task ToolsList_DroppedBodyReturnsJsonRpcBadGateway()
    {
        await using var factory = new ProxyWebAppFactory(
            (_, _) => Task.FromResult(TestUpstream.JsonStream(new ThrowingStream(""))),
            jinaApiKey: "jina-key",
            jinaUpstream: (_, _) => Task.FromResult(TestUpstream.Json("{\"data\":[]}"))
        );
        using var client = factory.CreateClient();

        using var response = await SendAsync(client, "/mcp", ListRequest(), "session-1");

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await response.Content.ReadAsStringAsync()).Should().Contain("\"jsonrpc\":\"2.0\"");
    }

    [Fact]
    public async Task CancellationNotificationWithMalformedParamsPassesThroughWithoutThrowing()
    {
        var githubCalls = 0;
        await using var factory = new ProxyWebAppFactory(
            (_, _) =>
            {
                githubCalls++;
                return Task.FromResult(TestUpstream.Json("{}", HttpStatusCode.Accepted));
            },
            jinaApiKey: "jina-key",
            jinaUpstream: (_, _) => Task.FromResult(TestUpstream.Json("{\"data\":[]}"))
        );
        using var client = factory.CreateClient();
        const string malformed = "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":[]}";

        using var response = await SendAsync(client, "/mcp", malformed, "session-1");

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        githubCalls.Should().Be(1);
    }

    [Fact]
    public async Task ToolsList_IdleTimeoutReturnsBoundedGatewayTimeout()
    {
        await using var factory = new ProxyWebAppFactory(
            (_, _) => Task.FromResult(TestUpstream.JsonStream(new CancellationObservingStream(""))),
            idleTimeoutSeconds: 1,
            jinaApiKey: "jina-key",
            jinaUpstream: (_, _) => Task.FromResult(TestUpstream.Json("{\"data\":[]}"))
        );
        using var client = factory.CreateClient();

        using var response = await SendAsync(client, "/mcp", ListRequest(), "session-1");

        response.StatusCode.Should().Be(HttpStatusCode.GatewayTimeout);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Timed out waiting");
    }

    [Fact]
    public async Task ToolsList_PaginatedResponsePassesThroughAndDoesNotRouteLocally()
    {
        const string paginated = "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"tools\":[],\"nextCursor\":\"next\"}}";
        var githubCalls = 0;
        await using var factory = CreateFactory(
            paginated,
            github: (request, _) =>
            {
                githubCalls++;
                return Task.FromResult(
                    request.Content is null ? TestUpstream.Json("{}") : TestUpstream.Json(githubCalls == 1 ? paginated : "{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"github\"}]}}")
                );
            }
        );
        using var client = factory.CreateClient();

        using var list = await SendAsync(client, "/mcp", ListRequest(), "session-1");
        (await list.Content.ReadAsStringAsync()).Should().Be(paginated);
        using var call = await SendAsync(client, "/mcp", CallRequest("web_search", 2), "session-1");

        (await call.Content.ReadAsStringAsync()).Should().Contain("github");
        githubCalls.Should().Be(2);
    }

    [Fact]
    public async Task ToolsCall_LocallyInjectedToolUsesJinaAndPreservesNumericId()
    {
        var githubCalls = 0;
        var jinaCalls = 0;
        await using var factory = CreateFactory(
            EmptyListResponse(),
            github: (_, _) =>
            {
                githubCalls++;
                return Task.FromResult(TestUpstream.Json(EmptyListResponse()));
            },
            jina: (_, _) =>
            {
                jinaCalls++;
                return Task.FromResult(TestUpstream.Json("{\"data\":[]}"));
            }
        );
        using var client = factory.CreateClient();

        using var list = await SendAsync(client, "/mcp", ListRequest(), "session-1");
        using var call = await SendAsync(
            client,
            "/mcp",
            CallRequest("web_search", 42, "{\"query\":\"release notes\"}"),
            "session-1"
        );
        var body = JsonNode.Parse(await call.Content.ReadAsStringAsync())!;

        body["id"]!.GetValue<int>().Should().Be(42);
        body["result"]!["isError"]!.GetValue<bool>().Should().BeFalse();
        githubCalls.Should().Be(1);
        jinaCalls.Should().Be(1);
    }

    [Fact]
    public async Task ToolsCall_MalformedParamsForUnknownOwnershipPassesThrough()
    {
        const string malformed = "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":[]}";
        var githubCalls = 0;
        await using var factory = CreateFactory(
            EmptyListResponse(),
            github: (_, _) =>
            {
                githubCalls++;
                return Task.FromResult(
                    TestUpstream.Json(
                        githubCalls == 1
                            ? EmptyListResponse()
                            : "{\"jsonrpc\":\"2.0\",\"id\":7,\"error\":{\"code\":-32602,\"message\":\"github invalid params\"}}"
                    )
                );
            }
        );
        using var client = factory.CreateClient();
        using var list = await SendAsync(client, "/mcp", ListRequest(), "session-1");

        using var call = await SendAsync(client, "/mcp", malformed, "session-1");
        var body = JsonNode.Parse(await call.Content.ReadAsStringAsync())!;

        body["error"]!["message"]!.GetValue<string>().Should().Be("github invalid params");
        githubCalls.Should().Be(2);
    }

    [Fact]
    public async Task ToolsCall_MalformedArgumentsForLocalToolReturnsJsonRpcInvalidParams()
    {
        await using var factory = CreateFactory(EmptyListResponse());
        using var client = factory.CreateClient();
        using var list = await SendAsync(client, "/mcp", ListRequest(), "session-1");
        const string malformed = "{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"tools/call\",\"params\":{\"name\":\"web_search\",\"arguments\":[]}}";

        using var call = await SendAsync(client, "/mcp", malformed, "session-1");
        var body = JsonNode.Parse(await call.Content.ReadAsStringAsync())!;

        body["id"]!.GetValue<int>().Should().Be(8);
        body["error"]!["code"]!.GetValue<int>().Should().Be(-32602);
    }

    [Fact]
    public async Task ToolsList_SessionlessResponseHeaderDoesNotInjectLocalTools()
    {
        await using var factory = new ProxyWebAppFactory(
            (_, _) => Task.FromResult(
                TestUpstream.Json(
                    EmptyListResponse(),
                    headers: new Dictionary<string, string> { ["Mcp-Session-Id"] = "upstream-session" }
                )
            ),
            jinaApiKey: "jina-key",
            jinaUpstream: (_, _) => throw new InvalidOperationException("Jina must not run")
        );
        using var client = factory.CreateClient();

        using var list = await SendAsync(client, "/mcp", ListRequest());

        (await list.Content.ReadAsStringAsync()).Should().Be(EmptyListResponse());
    }

    [Fact]
    public async Task ToolsCall_ValidationFailurePreservesMcpIsError()
    {
        await using var factory = CreateFactory(EmptyListResponse());
        using var client = factory.CreateClient();

        using var list = await SendAsync(client, "/mcp", ListRequest(), "session-1");
        using var call = await SendAsync(client, "/mcp", CallRequest("web_fetch", "call-1", "{\"url\":\"http://127.0.0.1/secret\"}"), "session-1");
        var body = JsonNode.Parse(await call.Content.ReadAsStringAsync())!;

        body["id"]!.GetValue<string>().Should().Be("call-1");
        body["result"]!["isError"]!.GetValue<bool>().Should().BeTrue();
        body["result"]!["content"]![0]!["text"]!.GetValue<string>().Should().NotContain("jina-key");
    }

    [Fact]
    public async Task Delete_RemovesLocalSnapshotEvenWhenGithubReturns405()
    {
        var githubCalls = 0;
        var jinaCalls = 0;
        await using var factory = CreateFactory(
            EmptyListResponse(),
            github: (request, _) =>
            {
                githubCalls++;
                if (request.Method == HttpMethod.Delete)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
                }

                var body = githubCalls == 1
                    ? EmptyListResponse()
                    : "{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"github\"}]}}";
                return Task.FromResult(TestUpstream.Json(body));
            },
            jina: (_, _) =>
            {
                jinaCalls++;
                return Task.FromResult(TestUpstream.Json("{\"data\":[]}"));
            }
        );
        using var client = factory.CreateClient();

        using var list = await SendAsync(client, "/mcp", ListRequest(), "session-1");
        using (var delete = new HttpRequestMessage(HttpMethod.Delete, "/mcp"))
        {
            delete.Headers.TryAddWithoutValidation("Mcp-Session-Id", "session-1");
            using var deleted = await client.SendAsync(delete);
            deleted.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        }
        using var call = await SendAsync(client, "/mcp", CallRequest("web_search", 2), "session-1");

        (await call.Content.ReadAsStringAsync()).Should().Contain("github");
        jinaCalls.Should().Be(0);
    }

    [Fact]
    public async Task Session404_RemovesOnlyMatchingEndpointSnapshot()
    {
        var githubCalls = 0;
        await using var factory = CreateFactory(
            EmptyListResponse(),
            github: (_, _) =>
            {
                githubCalls++;
                var response = githubCalls switch
                {
                    1 or 2 => TestUpstream.Json(EmptyListResponse()),
                    3 => TestUpstream.Json("{}", HttpStatusCode.NotFound),
                    _ => TestUpstream.Json("{\"jsonrpc\":\"2.0\",\"id\":4,\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"github\"}]}}"),
                };
                return Task.FromResult(response);
            }
        );
        using var client = factory.CreateClient();

        using var regularList = await SendAsync(client, "/mcp", ListRequest(), "session-1");
        using var readonlyList = await SendAsync(client, "/mcp/readonly", ListRequest(), "session-1");
        using var expired = await SendAsync(client, "/mcp", "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"resources/list\"}", "session-1");
        expired.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var regularCall = await SendAsync(client, "/mcp", CallRequest("web_search", 4), "session-1");

        (await regularCall.Content.ReadAsStringAsync()).Should().Contain("github");
        using var readonlyCall = await SendAsync(
            client,
            "/mcp/readonly",
            CallRequest("web_search", 5, "{\"query\":\"release notes\"}"),
            "session-1"
        );
        (await readonlyCall.Content.ReadAsStringAsync()).Should().NotContain("github");
    }

    [Fact]
    public async Task ToolsCall_ClientCancellationReachesJinaRequest()
    {
        var upstreamStream = new CancellationObservingStream("");
        await using var factory = CreateFactory(
            EmptyListResponse(),
            jina: (_, _) => Task.FromResult(TestUpstream.JsonStream(upstreamStream))
        );
        using var client = factory.CreateClient();
        using var list = await SendAsync(client, "/mcp", ListRequest(), "session-1");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                CallRequest("web_search", 2, "{\"query\":\"status\"}"),
                Encoding.UTF8,
                "application/json"
            ),
        };
        request.Headers.TryAddWithoutValidation("Mcp-Session-Id", "session-1");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = async () => await client.SendAsync(request, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await upstreamStream.Cancelled.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ToolsCall_MismatchedMcpContextForwardsToGithub()
    {
        var githubCalls = 0;
        await using var factory = CreateFactory(
            EmptyListResponse(),
            github: (_, _) =>
            {
                githubCalls++;
                var body = githubCalls == 1 ? EmptyListResponse() : "{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"github\"}]}}";
                return Task.FromResult(TestUpstream.Json(body));
            }
        );
        using var client = factory.CreateClient();

        using var list = await SendAsync(client, "/mcp", ListRequest(), "session-1", new Dictionary<string, string> { ["X-MCP-Host"] = "one" });
        using var call = await SendAsync(client, "/mcp", CallRequest("web_search", 2), "session-1", new Dictionary<string, string> { ["X-MCP-Host"] = "two" });

        (await call.Content.ReadAsStringAsync()).Should().Contain("github");
        githubCalls.Should().Be(2);
    }

    private static ProxyWebAppFactory CreateFactory(
        string githubBody,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? github = null,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? jina = null
    ) =>
        new(
            github ?? ((_, _) => Task.FromResult(TestUpstream.Json(githubBody))),
            jinaApiKey: "jina-key",
            jinaUpstream: jina ?? ((_, _) => Task.FromResult(TestUpstream.Json("{\"data\":[]}")))
        );

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        string path,
        string body,
        string? sessionId = null,
        IReadOnlyDictionary<string, string>? headers = null
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (sessionId is not null)
        {
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        }
        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }
        return await client.SendAsync(request);
    }

    private static string ListRequest() => "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}}";

    private static string CallRequest(string name, JsonNode id, string arguments = "{}") =>
        $"{{\"jsonrpc\":\"2.0\",\"id\":{id.ToJsonString()},\"method\":\"tools/call\",\"params\":{{\"name\":\"{name}\",\"arguments\":{arguments}}}}}";

    private static string EmptyListResponse() => "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"tools\":[]}}";
}
