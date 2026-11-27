using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Integration.Tests;

/// <summary>
/// Live contract for private marketplace registration (#1004; gateway PR #375, first released in v0.1.23): an opaque
/// connection id reaches the app's credential broker, the gateway reads the repository only with the broker's
/// short-lived token, and register, list, sync and remove round-trip. A broker denial and an unreachable broker come
/// back as their typed errors, a bad app key stays distinguishable from a denial, and nothing falls back to an
/// anonymous read. No real provider or secret is involved.
/// </summary>
/// <remarks>
/// The test hosts both providers. The broker listens on <c>SANDBOX_PRIVATE_BROKER_PORT</c>; the contract job relays
/// the gateway's <c>http://127.0.0.1:8099/</c> (its <c>APP_SECRETS_FILE</c> <c>marketplace_broker.url</c>) to it.
/// The "GitHub" repository is served over dumb HTTP with TLS on <c>SANDBOX_PRIVATE_GIT_PORT</c>, under a certificate
/// for <c>github.com</c> signed by the job's ephemeral CA (<c>SANDBOX_TEST_CA_CERT</c>/<c>_KEY</c>); the job points
/// the gateway's <c>github.com</c> at a relay to that port and trusts the CA through <c>GIT_SSL_CAINFO</c>.
/// </remarks>
[Collection(LiveGatewayCollection.Name)]
public sealed class SandboxPrivateMarketplaceLiveContractTests
{
    private const string Owner = "lmdt-contract";
    private const string Repository = "private-plugins";
    private const string PrivateUrl = $"https://github.com/{Owner}/{Repository}";

    private readonly LiveGatewayFixture _fixture;

    public SandboxPrivateMarketplaceLiveContractTests(LiveGatewayFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task PrivateRegistration_RoundTrips_ThroughTheBroker_AndNeverReadsAnonymously()
    {
        Skip.If(_fixture.SkipReason is not null, _fixture.SkipReason);
        var brokerPort = Port("SANDBOX_PRIVATE_BROKER_PORT");
        var gitPort = Port("SANDBOX_PRIVATE_GIT_PORT");
        var bearer = Environment.GetEnvironmentVariable("SANDBOX_PRIVATE_BROKER_BEARER");
        var caCert = Environment.GetEnvironmentVariable("SANDBOX_TEST_CA_CERT");
        var caKey = Environment.GetEnvironmentVariable("SANDBOX_TEST_CA_KEY");
        _fixture.RequirePrecondition(
            brokerPort > 0
                && gitPort > 0
                && !string.IsNullOrWhiteSpace(bearer)
                && File.Exists(caCert)
                && File.Exists(caKey),
            "Set SANDBOX_PRIVATE_BROKER_PORT, SANDBOX_PRIVATE_GIT_PORT, SANDBOX_PRIVATE_BROKER_BEARER and "
                + "SANDBOX_TEST_CA_CERT/SANDBOX_TEST_CA_KEY, with the gateway wired to them as in sandbox-contract.yml."
        );

        var client = _fixture.Client;
        var run = Guid.NewGuid().ToString("N")[..12];
        var alias = "lmpriv-" + run;
        var repo = GitMarketplaceRepo.Init(Path.Combine(Path.GetTempPath(), "lmdt-private-" + run));
        var firstCommit = repo.CommitVersion("first-" + run);
        repo.UpdateServerInfo();
        await using var provider = await FakePrivateProvider.StartAsync(
            brokerPort,
            gitPort,
            bearer!,
            caCert!,
            caKey!,
            Path.Combine(repo.Path, ".git")
        );

        var registered = await client.RegisterAppMarketplaceAsync(
            alias,
            new SandboxAppMarketplaceRegistration { Url = PrivateUrl, ConnectionId = "lmdt-conn-" + run }
        );
        try
        {
            registered.Url.Should().Be(PrivateUrl);
            registered.Ref.Should().Be("latest");
            registered.ResolvedSha.Should().Be(firstCommit);
            provider.BrokerCalls.Should().NotBeEmpty();
            provider
                .BrokerCalls.Should()
                .OnlyContain(c =>
                    c.Schema == "marketplace.git.read/1"
                    && c.AppId == _fixture.AppId
                    && c.ConnectionId == "lmdt-conn-" + run
                    && c.Url == PrivateUrl
                );
            provider.AuthorizedGitReads.Should().BePositive("the clone reads with the broker's token");

            var listed = await client.ListAppMarketplacesAsync();
            listed.Should().ContainSingle(m => m.Alias == alias).Which.ResolvedSha.Should().Be(firstCommit);

            var secondCommit = repo.CommitVersion("second-" + run);
            repo.UpdateServerInfo();
            var brokerCallsBeforeSync = provider.BrokerCalls.Count;
            var synced = await client.SyncAppMarketplaceAsync(alias);
            synced.ResolvedSha.Should().Be(secondCommit);
            provider.BrokerCalls.Count.Should().BeGreaterThan(brokerCallsBeforeSync, "every fetch re-asks the broker");

            // A revoked grant: the sync and a new registration are denied, and no Git read happens at all.
            provider.Mode = BrokerMode.Deny;
            var gitReadsBeforeDenial = provider.AuthorizedGitReads;
            var revoked = await Assert.ThrowsAsync<SandboxException>(() => client.SyncAppMarketplaceAsync(alias));
            ExpectDenied(revoked, bearer!, provider.Token);
            var deniedAlias = alias + "-denied";
            var denied = await Assert.ThrowsAsync<SandboxException>(() =>
                client.RegisterAppMarketplaceAsync(
                    deniedAlias,
                    new SandboxAppMarketplaceRegistration { Url = PrivateUrl, ConnectionId = "lmdt-denied-" + run }
                )
            );
            ExpectDenied(denied, bearer!, provider.Token);
            provider.BrokerCalls.Should().Contain(c => c.ConnectionId == "lmdt-denied-" + run);
            provider.AuthorizedGitReads.Should().Be(gitReadsBeforeDenial, "a denied grant never reaches Git");

            provider.Mode = BrokerMode.Unavailable;
            var unavailable = await Assert.ThrowsAsync<SandboxException>(() =>
                client.RegisterAppMarketplaceAsync(
                    deniedAlias,
                    new SandboxAppMarketplaceRegistration { Url = PrivateUrl, ConnectionId = "lmdt-down-" + run }
                )
            );
            unavailable.Kind.Should().Be(SandboxErrorKind.Unavailable);
            unavailable.ErrorCode.Should().Be("marketplace_broker_unavailable");
            unavailable.StatusCode.Should().Be(503);
            ExpectRedacted(unavailable, bearer!, provider.Token);
            provider.AuthorizedGitReads.Should().Be(gitReadsBeforeDenial);
            provider.UnauthorizedGitReads.Should().Be(0, "no request reached the repository without the token");
            provider.RejectedBrokerCalls.Should().Be(0, "the gateway always authenticates to the broker");
            (await client.ListAppMarketplacesAsync()).Should().NotContain(m => m.Alias == deniedAlias);

            // A rejected app key is an authorization failure WITHOUT the denial code, so callers can tell them apart.
            using var wrongKey = new SandboxClient(
                new SandboxClientOptions(
                    _fixture.Options.ServerAddress,
                    _fixture.AppId,
                    Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                    _fixture.Options.ExecutionTimeout,
                    _fixture.Options.TransportTimeout,
                    _fixture.Options.AllowInsecureDevelopmentTransport
                )
            );
            var badKey = await Assert.ThrowsAsync<SandboxException>(() =>
                wrongKey.RegisterAppMarketplaceAsync(
                    deniedAlias,
                    new SandboxAppMarketplaceRegistration { Url = PrivateUrl, ConnectionId = "lmdt-conn-" + run }
                )
            );
            badKey.Kind.Should().Be(SandboxErrorKind.Authorization);
            badKey.ErrorCode.Should().BeNull();
            badKey.StatusCode.Should().Be(401);
        }
        finally
        {
            provider.Mode = BrokerMode.Allow;
            var removed = await client.RemoveAppMarketplaceAsync(alias);
            removed.Should().BeTrue();
        }

        (await client.ListAppMarketplacesAsync()).Should().NotContain(m => m.Alias == alias);
        (await client.RemoveAppMarketplaceAsync(alias)).Should().BeFalse("it is no longer registered");
        DeleteTree(repo.Path);
    }

    /// <summary>Deletes a git work tree; git writes its objects read-only, which Windows refuses to delete.</summary>
    private static void DeleteTree(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    private static void ExpectDenied(SandboxException error, string bearer, string token)
    {
        error.Kind.Should().Be(SandboxErrorKind.Authorization);
        error.ErrorCode.Should().Be("marketplace_access_denied");
        error.StatusCode.Should().Be(403);
        ExpectRedacted(error, bearer, token);
    }

    private static void ExpectRedacted(SandboxException error, string bearer, string token) =>
        error.ToString().Should().NotContain(token).And.NotContain(bearer).And.NotContain(PrivateUrl);

    private static int Port(string name) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var port) ? port : 0;

    private enum BrokerMode
    {
        Allow,
        Deny,
        Unavailable,
    }

    private sealed record BrokerCall(string? Schema, string? AppId, string? ConnectionId, string? Url);

    /// <summary>
    /// The app-owned credential broker and a TLS "github.com" serving one repository over dumb HTTP, which answers
    /// only <c>Basic x-access-token:&lt;token&gt;</c> (what the gateway sends for a GitHub <c>basic</c> grant).
    /// </summary>
    private sealed class FakePrivateProvider : IAsyncDisposable
    {
        private readonly ConcurrentQueue<BrokerCall> _brokerCalls = new();
        private readonly string _bearer;
        private readonly string _gitDir;
        private readonly string _expectedGitAuthorization;
        private WebApplication? _app;
        private X509Certificate2? _leaf;
        private int _authorizedGitReads;
        private int _unauthorizedGitReads;
        private int _rejectedBrokerCalls;
        private volatile BrokerMode _mode = BrokerMode.Allow;

        private FakePrivateProvider(string bearer, string gitDir)
        {
            _bearer = bearer;
            _gitDir = Path.GetFullPath(gitDir);
            Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            _expectedGitAuthorization =
                "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + Token));
        }

        public string Token { get; }

        public BrokerMode Mode
        {
            get => _mode;
            set => _mode = value;
        }

        public IReadOnlyCollection<BrokerCall> BrokerCalls => _brokerCalls;

        public int AuthorizedGitReads => Volatile.Read(ref _authorizedGitReads);

        public int UnauthorizedGitReads => Volatile.Read(ref _unauthorizedGitReads);

        public int RejectedBrokerCalls => Volatile.Read(ref _rejectedBrokerCalls);

        public static async Task<FakePrivateProvider> StartAsync(
            int brokerPort,
            int gitPort,
            string bearer,
            string caCertPath,
            string caKeyPath,
            string gitDir
        )
        {
            var provider = new FakePrivateProvider(bearer, gitDir);
            var leaf = IssueGitHubCertificate(caCertPath, caKeyPath);
            provider._leaf = leaf;
            var builder = WebApplication.CreateBuilder();
            _ = builder.Logging.ClearProviders();
            _ = builder.WebHost.ConfigureKestrel(kestrel =>
            {
                kestrel.ListenAnyIP(brokerPort);
                kestrel.ListenAnyIP(gitPort, listen => listen.UseHttps(leaf));
            });
            var app = builder.Build();
            _ = app.MapPost("/", provider.BrokerAsync);
            _ = app.MapGet($"/{Owner}/{Repository}/{{**path}}", provider.GitAsync);
            await app.StartAsync();
            provider._app = app;
            return provider;
        }

        public async ValueTask DisposeAsync()
        {
            if (_app is not null)
            {
                await _app.DisposeAsync();
            }

            _leaf?.Dispose();
        }

        private async Task BrokerAsync(HttpContext context)
        {
            if (context.Request.Headers.Authorization.ToString() != "Bearer " + _bearer)
            {
                _ = Interlocked.Increment(ref _rejectedBrokerCalls);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            using var body = await JsonDocument.ParseAsync(context.Request.Body);
            var request = body.RootElement;
            var call = new BrokerCall(
                Text(request, "schema"),
                Text(request, "appId"),
                Text(request, "connectionId"),
                Text(request, "url")
            );
            _brokerCalls.Enqueue(call);
            var mode = Mode;
            if (mode != BrokerMode.Allow)
            {
                context.Response.StatusCode =
                    mode == BrokerMode.Deny ? StatusCodes.Status403Forbidden : StatusCodes.Status503ServiceUnavailable;
                return;
            }

            await context.Response.WriteAsJsonAsync(
                new
                {
                    repositoryId = "lmdt-contract-repo-1",
                    url = call.Url,
                    scheme = "basic",
                    token = Token,
                    expiresAt = DateTimeOffset.UtcNow.AddMinutes(10).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                }
            );
        }

        private async Task GitAsync(HttpContext context, string path)
        {
            if (context.Request.Headers.Authorization.ToString() != _expectedGitAuthorization)
            {
                _ = Interlocked.Increment(ref _unauthorizedGitReads);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            _ = Interlocked.Increment(ref _authorizedGitReads);
            var file = Path.GetFullPath(Path.Combine(_gitDir, path));
            if (!file.StartsWith(_gitDir + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(file))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            // A text/plain info/refs (even for ?service=git-upload-pack) makes Git use the dumb protocol.
            context.Response.ContentType = path == "info/refs" ? "text/plain" : "application/octet-stream";
            await context.Response.SendFileAsync(file);
        }

        private static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static X509Certificate2 IssueGitHubCertificate(string caCertPath, string caKeyPath)
        {
            using var ca = X509Certificate2.CreateFromPemFile(caCertPath, caKeyPath);
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=github.com", key, HashAlgorithmName.SHA256);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("github.com");
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            using var signed = request.Create(
                ca,
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddHours(1),
                RandomNumberGenerator.GetBytes(16)
            );
            using var ephemeral = signed.CopyWithPrivateKey(key);
            var pfx = ephemeral.Export(X509ContentType.Pfx);
#if NET9_0_OR_GREATER
            return X509CertificateLoader.LoadPkcs12(pfx, null);
#else
            return new X509Certificate2(pfx);
#endif
        }
    }
}
