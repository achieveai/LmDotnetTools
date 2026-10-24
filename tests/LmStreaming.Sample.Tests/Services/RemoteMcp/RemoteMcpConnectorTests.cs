using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.Sandbox;
using LmStreaming.Sample.Services.RemoteMcp;
using Microsoft.Extensions.Logging;

namespace LmStreaming.Sample.Tests.Services.RemoteMcp;

/// <summary>
/// SC7: connects to plugins' http MCP servers through a fake transport. The egress key's headers are set on every
/// request from the key as it is now (F-007), and no failure path logs the url, the host, a header value or a key
/// value (F-009). Which tools a build keeps, and what connects nothing (G4); the production transport (G5).
/// </summary>
public sealed class RemoteMcpConnectorTests : IDisposable
{
    private const string Url = "https://acme.test/mcp/secret-path";
    private const string LookupTool =
        """[{"name":"lookup","description":"Look up a record.","inputSchema":{"type":"object","properties":{}}}]""";

    private readonly DirectoryInfo _keysDirectory = Directory.CreateTempSubdirectory("remote-mcp-keys-");

    public void Dispose() => _keysDirectory.Delete(recursive: true);

    // F-007. Red: the token minted at connect is frozen into the transport's headers, so a call made after the key
    // provider has a new token still sends the old one (a 401 once a real token expires).
    [Fact]
    public async Task KeyedConnection_SendsTheKeysCurrentToken_OnARequestAfterTheConnect()
    {
        var tokens = new TokenEndpoint("tok-A");
        var keys = Keys(tokens);
        await keys.UpsertAsync(
            new PredefinedKeyEntry
            {
                Id = "acme",
                Host = "acme.test",
                Kind = PredefinedKeyKind.ClientCredentials,
                TokenEndpoint = "https://login.test/token",
                ClientId = "client",
                ClientSecret = "client-secret",
            }
        );
        var server = new FakeRemoteMcpHandler(LookupTool);
        var connection = await ConnectAsync(server, keys, new RecordingLogger());
        try
        {
            var tool = connection.Tools.Should().ContainSingle().Subject;
            server
                .Requests.First(r => r.Method == "initialize")
                .Headers["Authorization"]
                .Should()
                .Be("Bearer tok-A", "guard: the connect sent the key's token");

            tokens.Current = "tok-B";
            _ = await tool.Handler("{}", new ToolCallContext(), CancellationToken.None);

            server.Requests.Single(r => r.Method == "tools/call").Headers["Authorization"].Should().Be("Bearer tok-B");
        }
        finally
        {
            await DisposeAsync(connection);
        }
    }

    // F-007. Red: the old revision's headers are frozen into the transport, so an edited (rotated or revoked) key's
    // credential is still sent for the life of the build.
    [Fact]
    public async Task KeyedConnection_NeverSendsTheMatchedRevision_OnceTheKeyIsEdited()
    {
        var keys = Keys(new TokenEndpoint("unused"));
        await keys.UpsertAsync(CustomHeadersKey("Bearer key-v1"));
        var server = new FakeRemoteMcpHandler(LookupTool);
        var connection = await ConnectAsync(server, keys, new RecordingLogger());
        try
        {
            var tool = connection.Tools.Should().ContainSingle().Subject;
            server
                .Requests.First(r => r.Method == "initialize")
                .Headers["Authorization"]
                .Should()
                .Be("Bearer key-v1", "guard: the connect sent the matched revision's header");
            var sentBeforeTheEdit = server.Requests.Count;

            await keys.UpsertAsync(CustomHeadersKey("Bearer key-v2"));
            var result = await tool.Handler("{}", new ToolCallContext(), CancellationToken.None);

            // The model reads the refusal: a fixed phrase, never the url or the host.
            var text = result.Should().BeOfType<ToolHandlerResult.Resolved>().Subject.Payload.Text;
            text.Should().Contain("was changed or has no valid token").And.NotContain("acme.test");
            var sentAfterTheEdit = server.Requests.Skip(sentBeforeTheEdit).ToList();
            sentAfterTheEdit.Should().NotContain(r => r.Headers.Values.Any(v => v.Contains("key-v1")));
            sentAfterTheEdit
                .Should()
                .NotContain(
                    r => r.Method == "tools/call",
                    "the plan matched the old revision; the next build matches anew"
                );
        }
        finally
        {
            await DisposeAsync(connection);
        }
    }

    // F-009. Passes today. Red if the connect catch logged ex (the exception's text holds the host and url) or
    // ex.Message, or if ReasonOf returned ex.Message: the change would be adding ex as the first LogWarning argument
    // in ConnectOneAsync's catch, or "{Reason}" = ex.Message.
    [Theory]
    [InlineData("throws", "HttpRequestException")]
    [InlineData("401", "HTTP 401")]
    [InlineData("500", "HTTP 500")]
    public async Task FailedConnect_LogsAFixedReason_AndNoUrlHostHeaderOrKeyValue(string failure, string reason)
    {
        var keys = Keys(new TokenEndpoint("unused"));
        await keys.UpsertAsync(CustomHeadersKey("Bearer key-secret-f009"));
        var server = new FakeRemoteMcpHandler(LookupTool)
        {
            Fail = request =>
                failure == "throws"
                    ? throw new HttpRequestException(
                        $"No connection could be made to acme.test:443 ({request.RequestUri}) for {request.Headers}"
                    )
                    : new HttpResponseMessage(
                        failure == "401" ? HttpStatusCode.Unauthorized : HttpStatusCode.InternalServerError
                    )
                    {
                        Content = new StringContent($"denied for {request.RequestUri}", Encoding.UTF8, "text/plain"),
                    },
        };
        var log = new RecordingLogger();

        var connection = await ConnectAsync(server, keys, log, ("X-Trace", "trace-secret-f009"));
        await DisposeAsync(connection);

        connection.Tools.Should().BeEmpty();
        server.Requests.Should().NotBeEmpty("guard: the failure came from the transport, not from the plan");
        log.Lines.Should().Contain(l => l.Contains($"Remote MCP server mk/acme/api not connected: {reason} |"));
        foreach (var secret in new[] { "acme.test", "secret-path", "trace-secret-f009", "key-secret-f009" })
        {
            log.Lines.Should().NotContain(l => l.Contains(secret, StringComparison.OrdinalIgnoreCase), secret);
        }
    }

    // G4. A name of exactly 64 characters is kept; a longer one is skipped (a provider rejects every request that lists
    // it). The same plugin and server in a second marketplace yield the same names: the first in gateway order keeps
    // them. Red: compare with >= instead of >, drop the length check, or let the later duplicate in.
    [Fact]
    public async Task Connect_KeepsA64CharacterName_SkipsALongerOne_AndASecondServersDuplicate()
    {
        var fits = new string('f', RemoteMcpConnector.MaxToolNameLength - "mcp_plugin_acme_api_".Length);
        var tooLong = fits + "x";
        var server = new FakeRemoteMcpHandler(
            JsonSerializer.Serialize(
                new[] { fits, tooLong }.Select(name => new { name, inputSchema = new { type = "object" } })
            )
        );
        var log = new RecordingLogger();

        var connection = await ConnectAsync(
            server,
            Keys(new TokenEndpoint("unused")),
            log,
            _ => Task.FromResult<IReadOnlyList<SandboxMcpServer>>([Row("mk"), Row("mk2")])
        );
        try
        {
            var kept = RemoteMcpConnector.ToolName("acme", "api", fits);
            kept.Should().HaveLength(RemoteMcpConnector.MaxToolNameLength, "guard: the kept name is at the limit");
            connection.Resources.Should().HaveCount(2, "guard: both servers connected");
            connection.Tools.Select(t => t.Contract.Name).Should().Equal(kept);
            log.Lines.Where(l => l.Contains("skipped: its name is longer than 64")).Should().HaveCount(2);
            log.Lines.Should()
                .ContainSingle(l => l.Contains("skipped: another server already has the name"))
                .Which.Should()
                .Contain($"Remote MCP tool {kept} of mk2/acme/api skipped");
        }
        finally
        {
            await DisposeAsync(connection);
        }
    }

    // G4. Nothing connects, and nothing reaches the server: the gateway's listing failed (the next build retries), or
    // the egress key the plan matched has no valid token (a request would go out without its credential). Red: let
    // the listing's exception escape, or drop the connect-time key check (the per-request key handler still stops
    // initialize, but the WARN becomes "not connected: HttpRequestException").
    [Theory]
    [InlineData("listing fails", "Warning Remote MCP servers not listed sess-1: Unavailable 503, retryable")]
    [InlineData(
        "key has no token",
        "Warning Remote MCP server mk/acme/api not connected: its egress key has no valid token"
    )]
    public async Task Connect_WithNoListingOrNoValidKeyToken_ConnectsNothing_AndWarns(string failure, string warning)
    {
        var keys = Keys(new TokenEndpoint("unused"));
        await keys.UpsertAsync(
            new PredefinedKeyEntry
            {
                Id = "acme",
                Host = "acme.test",
                Kind = PredefinedKeyKind.CustomHeaders,
                Headers = [],
            }
        );
        var server = new FakeRemoteMcpHandler(LookupTool);
        var log = new RecordingLogger();

        var connection =
            failure == "listing fails"
                ? await ConnectAsync(
                    server,
                    keys,
                    log,
                    _ =>
                        Task.FromException<IReadOnlyList<SandboxMcpServer>>(
                            new SandboxException(
                                SandboxErrorKind.Unavailable,
                                "GET https://gw.test/mcp-servers: 503",
                                503
                            )
                        )
                )
                : await ConnectAsync(server, keys, log);

        connection.Tools.Should().BeEmpty();
        connection.Resources.Should().BeEmpty();
        server.Requests.Should().BeEmpty();
        log.Lines.Should().ContainSingle(l => l.StartsWith(warning, StringComparison.Ordinal));
        log.Lines.Should().NotContain(l => l.Contains("gw.test", StringComparison.Ordinal));
    }

    // G5. Every test injects a transport, so only this pins the production one. Red: AllowAutoRedirect = true, which
    // lets a plugin server's 3xx carry the egress key's headers to another host.
    [Fact]
    public void DefaultTransportHandler_FollowsNoRedirect()
    {
        using var handler = RemoteMcpConnector.DefaultTransportHandler();

        handler.AllowAutoRedirect.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://acme.test:8443")]
    [InlineData("https://evilacme.test")]
    public async Task Connect_WithoutExactOperatorOriginAdmission_NeverReachesTransport(string? origin)
    {
        var server = new FakeRemoteMcpHandler(LookupTool);
        var connection = await RemoteMcpConnector.ConnectAsync(
            _ => Task.FromResult<IReadOnlyList<SandboxMcpServer>>([Row("mk")]),
            Keys(new TokenEndpoint("unused")),
            false,
            server,
            new RecordingLogger(),
            "sess-1",
            CancellationToken.None,
            origin is null ? [] : [origin]
        );

        connection.Tools.Should().BeEmpty();
        connection.Resources.Should().BeEmpty();
        server.Requests.Should().BeEmpty("a plugin cannot choose an application network destination");
    }

    private PredefinedKeyRegistry Keys(TokenEndpoint tokens) =>
        new(_keysDirectory.FullName, new NoTokenStore(), new HttpClient(tokens), NullLoggerFactory.Instance);

    private static PredefinedKeyEntry CustomHeadersKey(string authorization) =>
        new()
        {
            Id = "acme",
            Host = "acme.test",
            Kind = PredefinedKeyKind.CustomHeaders,
            Headers = [new PredefinedHeader("Authorization", authorization)],
        };

    private static Task<RemoteMcpConnection> ConnectAsync(
        FakeRemoteMcpHandler server,
        PredefinedKeyRegistry keys,
        ILogger logger,
        params (string Name, string Value)[] headers
    ) =>
        ConnectAsync(server, keys, logger, _ => Task.FromResult<IReadOnlyList<SandboxMcpServer>>([Row("mk", headers)]));

    private static Task<RemoteMcpConnection> ConnectAsync(
        FakeRemoteMcpHandler server,
        PredefinedKeyRegistry keys,
        ILogger logger,
        Func<CancellationToken, Task<IReadOnlyList<SandboxMcpServer>>> fetch
    ) =>
        RemoteMcpConnector.ConnectAsync(
            fetch,
            keys,
            allowLoopbackHttp: false,
            server,
            logger,
            "sess-1",
            CancellationToken.None,
            ["https://acme.test"]
        );

    /// <summary>Plugin acme's http server "api" at <see cref="Url"/>, as marketplace <paramref name="marketplace"/> lists it.</summary>
    private static SandboxMcpServer Row(string marketplace, params (string Name, string Value)[] headers) =>
        new()
        {
            Name = "api",
            Plugin = "acme",
            Marketplace = marketplace,
            Type = "http",
            Supported = true,
            Url = Url,
            Headers = [.. headers.Select(h => new SandboxMcpHeader { Name = h.Name, Value = h.Value })],
        };

    private static async Task DisposeAsync(RemoteMcpConnection connection)
    {
        foreach (var resource in connection.Resources)
        {
            await resource.DisposeAsync();
        }
    }

    /// <summary>
    /// An OAuth token endpoint that mints <see cref="Current"/>. Its lifetime is inside the provider's refresh window,
    /// so every token request mints again: a token "expires" between two requests.
    /// </summary>
    private sealed class TokenEndpoint(string current) : HttpMessageHandler
    {
        public string Current { get; set; } = current;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$"""{"access_token":"{{Current}}","token_type":"Bearer","expires_in":60}""",
                        Encoding.UTF8,
                        "application/json"
                    ),
                }
            );
    }

    /// <summary>Everything a log sink could write: level, message, each structured value and the whole exception.</summary>
    private sealed class RecordingLogger : ILogger
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyList<string> Lines => [.. _lines];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(" ", pairs.Select(p => $"{p.Key}={Render(p.Value)}"))
                : string.Empty;
            _lines.Enqueue($"{logLevel} {formatter(state, exception)} | {values} | {exception}");
        }

        private static string Render(object? value) =>
            value is IEnumerable many and not string
                ? string.Join(",", many.Cast<object?>())
                : value?.ToString() ?? "null";
    }

    private sealed class NoTokenStore : IOAuthTokenStore
    {
        public Task<OAuthTokenRecord?> GetAsync(string provider, CancellationToken ct = default) =>
            Task.FromResult<OAuthTokenRecord?>(null);

        public Task SaveAsync(OAuthTokenRecord record, CancellationToken ct = default) => Task.CompletedTask;

        public Task RemoveAsync(string provider, CancellationToken ct = default) => Task.CompletedTask;
    }
}
