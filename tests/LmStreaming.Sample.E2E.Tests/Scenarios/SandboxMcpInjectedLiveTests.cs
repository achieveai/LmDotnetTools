using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AchieveAi.LmDotnetTools.Sandbox;
using FluentAssertions;
using LmStreaming.Sample.Controllers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>A real gateway/proxy/helper journey through the app's registry and authenticated MVC callback.</summary>
public sealed class SandboxMcpInjectedLiveTests
{
    [SkippableFact]
    public async Task InjectedCredential_ActualAppCallback_ReachesOnlyTheTlsStub()
    {
        var required = Environment.GetEnvironmentVariable("SANDBOX_CONTRACT_REQUIRED") == "true";
        var gatewayUrl = Environment.GetEnvironmentVariable("SANDBOX_BASE_URL");
        var appId = Environment.GetEnvironmentVariable("SANDBOX_APP_ID");
        var appKey = Environment.GetEnvironmentVariable("SANDBOX_APP_KEY");
        var caPath = Environment.GetEnvironmentVariable("SANDBOX_CANARY_CA_CERT");
        var caKeyPath = Environment.GetEnvironmentVariable("SANDBOX_CANARY_CA_KEY");
        var configured = new[] { gatewayUrl, appId, appKey, caPath, caKeyPath }.All(v => !string.IsNullOrWhiteSpace(v));
        Skip.If(!configured && !required, "Set the live gateway credentials and SANDBOX_CANARY_CA_CERT/CA_KEY.");
        Assert.True(configured, "The required injected-auth canary is missing live configuration.");

        var state = Path.Combine(Path.GetTempPath(), "mcp-canary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var logs = new CapturedLogs();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(logs));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        using var ca = X509Certificate2.CreateFromPemFile(caPath!, caKeyPath!);
        using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=mcp.sbx.test", leafKey, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("mcp.sbx.test");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        using var signed = request.Create(
            ca,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1),
            RandomNumberGenerator.GetBytes(16)
        );
        using var ephemeralLeaf = signed.CopyWithPrivateKey(leafKey);
        // Windows SChannel requires a persisted key handle; importing PFX also works on Linux CI.
        using var leaf = X509CertificateLoader.LoadPkcs12(ephemeralLeaf.Export(X509ContentType.Pfx), null);
        var stubBuilder = WebApplication.CreateBuilder();
        stubBuilder.Logging.ClearProviders();
        stubBuilder.Logging.AddProvider(logs);
        stubBuilder.WebHost.ConfigureKestrel(k =>
            k.ListenAnyIP(Port("SANDBOX_CANARY_STUB_PORT"), listen => listen.UseHttps(leaf))
        );
        await using var stub = stubBuilder.Build();
        var accepted = 0;
        var rejected = 0;
        stub.MapPost(
            "/mcp",
            async context =>
            {
                if (context.Request.Headers.Authorization.ToString() != "Bearer " + token)
                {
                    Interlocked.Increment(ref rejected);
                    context.Response.StatusCode = 401;
                    return;
                }
                Interlocked.Increment(ref accepted);
                using var payload = await JsonDocument.ParseAsync(context.Request.Body);
                var root = payload.RootElement;
                if (!root.TryGetProperty("id", out var id))
                {
                    context.Response.StatusCode = 202;
                    return;
                }
                object result = root.GetProperty("method").GetString() switch
                {
                    "initialize" => new
                    {
                        protocolVersion = "2025-06-18",
                        capabilities = new { tools = new { } },
                        serverInfo = new { name = "canary", version = "1" },
                    },
                    "tools/list" => new
                    {
                        tools = new[]
                        {
                            new
                            {
                                name = "add",
                                inputSchema = new
                                {
                                    type = "object",
                                    properties = new { a = new { type = "number" }, b = new { type = "number" } },
                                    required = new[] { "a", "b" },
                                },
                                annotations = new { readOnlyHint = true },
                            },
                        },
                    },
                    "tools/call" => Add(root),
                    _ => throw new InvalidOperationException("Unexpected canary MCP method."),
                };
                await context.Response.WriteAsJsonAsync(
                    new
                    {
                        jsonrpc = "2.0",
                        id = id.Clone(),
                        result,
                    }
                );
            }
        );
        await stub.StartAsync();
        var endpoint = "https://mcp.sbx.test:" + new Uri(stub.Urls.Single()).Port + "/mcp";
        var options = new SandboxGatewayOptions
        {
            BaseUrl = gatewayUrl!,
            AutoSpawn = false,
            AppId = appId!,
            AppKey = appKey,
            Mcp = new SandboxMcpOptions
            {
                Egress = "auto",
                Servers = new()
                {
                    ["canary"] = new()
                    {
                        Auth = "injected",
                        Headers = ["Authorization"],
                        Url = endpoint,
                    },
                },
                CredentialBindings =
                [
                    new()
                    {
                        AppId = appId!,
                        Server = "canary",
                        Url = endpoint,
                        ProviderId = "predefined-canary",
                        Marketplace = "lminjected",
                        Plugin = "injected-canary",
                    },
                ],
            },
        };
        var auth = new AuthOptions { Webhook = new WebhookOptions { UseSandboxWebhook = true } };
        var secrets = new SessionSecretStore(
            Path.Combine(state, "sessions"),
            loggerFactory.CreateLogger<SessionSecretStore>()
        );
        var tokens = new FileOAuthTokenStore(state, loggerFactory.CreateLogger<FileOAuthTokenStore>());
        var keys = new PredefinedKeyRegistry(state, tokens, http, loggerFactory);
        await keys.UpsertAsync(
            new PredefinedKeyEntry
            {
                Id = "canary",
                Host = "mcp.sbx.test",
                Port = new Uri(endpoint).Port,
                Kind = PredefinedKeyKind.CustomHeaders,
                Headers = [new("Authorization", "Bearer " + token)],
            }
        );
        var gateway = new SandboxGatewayLifetime(options, loggerFactory.CreateLogger<SandboxGatewayLifetime>(), http);
        await using var registry = new SandboxSessionRegistry(
            gateway,
            options,
            loggerFactory.CreateLogger<SandboxSessionRegistry>(),
            http,
            auth,
            secrets,
            keys
        );
        var mcp = new SandboxMcpAuthService(registry, [], keys);
        var callbackBuilder = WebApplication.CreateBuilder();
        callbackBuilder.Logging.ClearProviders();
        callbackBuilder.Logging.AddProvider(logs);
        callbackBuilder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(Port("SANDBOX_CANARY_CALLBACK_PORT")));
        callbackBuilder
            .Services.AddControllers()
            .AddApplicationPart(typeof(SandboxWebhookController).Assembly)
            .AddControllersAsServices();
        // Auth-only host: discovery/task collaborators are never dispatched in this scenario.
        callbackBuilder.Services.AddTransient(_ => new SandboxWebhookController(
            secrets,
            registry,
            null!,
            null!,
            loggerFactory.CreateLogger<SandboxWebhookController>(),
            mcp
        ));
        await using var callback = callbackBuilder.Build();
        var authCallbacks = 0;
        callback.Use(
            async (context, next) =>
            {
                if (context.Request.Path == "/api/sandbox/webhook")
                {
                    context.Request.EnableBuffering();
                    using var payload = await JsonDocument.ParseAsync(context.Request.Body);
                    context.Request.Body.Position = 0;
                    if (payload.RootElement.GetProperty("event").GetString() == "auth_request")
                        Interlocked.Increment(ref authCallbacks);
                    else
                    {
                        context.Response.StatusCode = await secrets.MatchesAsync(
                            payload.RootElement.GetProperty("session_id").GetString()!,
                            context.Request.Headers.Authorization.ToString()
                        )
                            ? 200
                            : 401;
                        return;
                    }
                }
                await next(context);
            }
        );
        callback.MapControllers();
        await callback.StartAsync();
        auth.Webhook.PublicBaseUrl = "http://127.0.0.1:" + new Uri(callback.Urls.Single()).Port;
        using var client = new SandboxClient(
            new SandboxClientOptions(
                new Uri(gatewayUrl!),
                appId!,
                appKey!,
                TimeSpan.FromSeconds(60),
                TimeSpan.FromSeconds(30)
            )
        );
        try
        {
            var workspace = "canary-" + Guid.NewGuid().ToString("N");
            var session = await registry.GetOrCreateSessionAsync(
                new WorkspaceRef(
                    workspace,
                    DirectoryRelPath: workspace,
                    Marketplaces: ["lminjected"],
                    Env: new Dictionary<string, string> { ["MCP_CANARY_ENDPOINT"] = endpoint }
                )
            );
            var result = await client.ExecuteAsync(
                session.SessionId,
                new SandboxCommand(["sbx-mcp", "call", "canary", "add", "{\"a\":2,\"b\":3}"])
            );
            var inspection = await client.ExecuteAsync(
                session.SessionId,
                new SandboxCommand(["sh", "-c", "env; cat \"$SBX_MCP_MANIFEST\""])
            );
            // Boolean assertions keep a leaked credential out of test failure output.
            Assert.False(
                (result.CombinedOutput + inspection.CombinedOutput).Contains(token, StringComparison.Ordinal),
                "Credential appeared in sandbox output/env/manifest."
            );
            result
                .ExitCode.Should()
                .Be(
                    0,
                    "the installed helper must complete the real proxy/callback roundtrip; sanitized helper error: {0}",
                    result.StandardError
                );
            inspection.ExitCode.Should().Be(0);
            using var reply = JsonDocument.Parse(result.StandardOutput);
            reply.RootElement.GetProperty("structured").GetProperty("sum").GetInt32().Should().Be(5);
            authCallbacks.Should().BeGreaterThan(0, "the actual MVC callback must resolve the injected credential");
            accepted.Should().BeGreaterThan(0);
            rejected.Should().Be(0, "every upstream request must have exactly the injected credential");
        }
        finally
        {
            await registry.DisposeAsync();
            await callback.StopAsync();
            await stub.StopAsync();
            Directory.Delete(state, recursive: true);
            Assert.False(
                logs.Messages.Any(m => m.Contains(token, StringComparison.Ordinal)),
                "Credential appeared in captured app/stub logs."
            );
        }
    }

    private static int Port(string name) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var port) ? port : 0;

    private static object Add(JsonElement root)
    {
        var parameters = root.GetProperty("params");
        if (parameters.GetProperty("name").GetString() != "add")
            throw new InvalidOperationException("Unexpected canary tool.");
        var args = parameters.GetProperty("arguments");
        var sum = args.GetProperty("a").GetInt32() + args.GetProperty("b").GetInt32();
        return new
        {
            content = new[] { new { type = "text", text = JsonSerializer.Serialize(new { sum }) } },
            structuredContent = new { sum },
            isError = false,
        };
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Capture(Messages);

        public void Dispose() { }

        private sealed class Capture(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            ) => messages.Enqueue(formatter(state, exception));
        }
    }
}
