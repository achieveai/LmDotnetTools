using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.Sandbox.Integration.Tests;

/// <summary>
/// The app-owned credential broker and a TLS "github.com" serving one repository over dumb HTTP, which answers
/// only <c>Basic x-access-token:&lt;token&gt;</c> (what the gateway sends for a GitHub <c>basic</c> grant). Used by
/// <see cref="SandboxPrivateMarketplaceLiveContractTests"/>; the job wiring it needs is described there.
/// </summary>
internal sealed class FakePrivateProvider : IAsyncDisposable
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

    /// <summary>What the broker answers to an authenticated request.</summary>
    public enum BrokerMode
    {
        Allow,
        Deny,
        Unavailable,
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

    /// <summary>
    /// Starts the broker on <paramref name="brokerPort"/> and the Git host on <paramref name="gitPort"/>, serving
    /// <paramref name="gitDir"/> under <c>/<paramref name="repositoryPath"/>/</c>.
    /// </summary>
    public static async Task<FakePrivateProvider> StartAsync(
        int brokerPort,
        int gitPort,
        string bearer,
        string caCertPath,
        string caKeyPath,
        string repositoryPath,
        string gitDir
    )
    {
        var provider = new FakePrivateProvider(bearer, gitDir);
        var leaf = IssueGitHubCertificate(caCertPath, caKeyPath, DateTimeOffset.UtcNow);
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
        _ = app.MapGet($"/{repositoryPath}/{{**path}}", provider.GitAsync);
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

    /// <summary>
    /// Issues the <c>github.com</c> leaf under the job's CA, valid from a minute before <paramref name="now"/> to an
    /// hour after it, clamped into the CA's own validity: the job may create the CA only seconds before this runs,
    /// and <see cref="CertificateRequest.Create(X509Certificate2, DateTimeOffset, DateTimeOffset, byte[])"/>
    /// refuses a leaf that starts before (or ends after) its issuer.
    /// </summary>
    internal static X509Certificate2 IssueGitHubCertificate(string caCertPath, string caKeyPath, DateTimeOffset now)
    {
        using var ca = X509Certificate2.CreateFromPemFile(caCertPath, caKeyPath);
        var caNotBefore = new DateTimeOffset(ca.NotBefore.ToUniversalTime(), TimeSpan.Zero);
        var caNotAfter = new DateTimeOffset(ca.NotAfter.ToUniversalTime(), TimeSpan.Zero);
        var notBefore = Max(now.AddMinutes(-1), caNotBefore);
        var notAfter = Min(now.AddHours(1), caNotAfter);
        if (notAfter <= notBefore)
        {
            throw new InvalidOperationException(
                $"The test CA ({caCertPath}) is valid {caNotBefore:O} to {caNotAfter:O}, which leaves no validity "
                    + $"window for a leaf issued at {now:O}."
            );
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=github.com", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("github.com");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        using var signed = request.Create(ca, notBefore, notAfter, RandomNumberGenerator.GetBytes(16));
        using var ephemeral = signed.CopyWithPrivateKey(key);
        var pfx = ephemeral.Export(X509ContentType.Pfx);
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadPkcs12(pfx, null);
#else
        return new X509Certificate2(pfx);
#endif
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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

    /// <summary>One authenticated broker request, as the gateway sent it.</summary>
    public sealed record BrokerCall(string? Schema, string? AppId, string? ConnectionId, string? Url);
}
