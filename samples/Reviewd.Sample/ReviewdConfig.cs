using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.Sandbox;
using CodeReviewDaemon.Sample.Agents;
using Microsoft.Extensions.Configuration;

namespace Reviewd.Sample;

internal sealed class ReviewdConfig(IConfiguration configuration) : IDisposable
{
    private HttpClient? _s2sTransport;

    public IConfiguration Configuration { get; } = configuration;
    public string GatewayUrl =>
        Environment.GetEnvironmentVariable("CRD_SANDBOX_GATEWAY")
        ?? Configuration["SandboxGateway:BaseUrl"]
        ?? "http://127.0.0.1:3000";
    public string AppId =>
        Environment.GetEnvironmentVariable("CRD_SANDBOX_APP_ID")
        ?? Configuration["SandboxGateway:AppId"]
        ?? "codereview-daemon";
    public string? AppKey =>
        Environment.GetEnvironmentVariable("CRD_SANDBOX_APP_KEY") ?? Configuration["SandboxGateway:AppKey"];
    public string HostUrl =>
        Environment.GetEnvironmentVariable("LMSTREAMING_BASE_URL")
        ?? Configuration["CodeReviewDaemon:LmStreamingBaseUrl"]
        ?? "http://localhost:5000";
    public string? S2SSecret => Configuration["CodeReviewDaemon:LmStreamingS2SSecret"];
    public string ModeId => Configuration["CodeReviewDaemon:LmStreamingModeId"] ?? "workspace-agent";

    public static ReviewdConfig Load(string? path)
    {
        var builder = new ConfigurationBuilder();
        if (path is not null)
        {
            _ = builder.AddJsonFile(Path.GetFullPath(path), optional: false);
        }
        else
        {
            _ = builder.AddJsonFile(Path.Combine(Environment.CurrentDirectory, "appsettings.json"), optional: true);
        }

        _ = builder.AddEnvironmentVariables();
        return new ReviewdConfig(builder.Build());
    }

    public LmStreamingS2SClient CreateS2SClient()
    {
        if (!string.IsNullOrWhiteSpace(AppKey))
        {
            SandboxCredential.ValidateKeyOrThrow(AppId, AppKey);
        }
        var address = new Uri(HostUrl.TrimEnd('/') + "/", UriKind.Absolute);
        if (address.Scheme != Uri.UriSchemeHttps && !(address.Scheme == Uri.UriSchemeHttp && address.IsLoopback))
        {
            throw new ArgumentException("The S2S host must use HTTPS unless it is loopback.");
        }
        if (
            !string.IsNullOrEmpty(address.UserInfo)
            || !string.IsNullOrEmpty(address.Query)
            || !string.IsNullOrEmpty(address.Fragment)
        )
        {
            throw new ArgumentException("The S2S host URL must not contain credentials, a query, or a fragment.");
        }

        _s2sTransport ??= new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = address,
            Timeout = TimeSpan.FromSeconds(30),
        };
        return new LmStreamingS2SClient(_s2sTransport, S2SSecret, AppId, AppKey);
    }

    public SandboxClient CreateSandboxClient(TimeSpan? executionTimeout = null) =>
        new(
            new SandboxClientOptions(
                new Uri(GatewayUrl, UriKind.Absolute),
                AppId,
                AppKey ?? "",
                executionTimeout ?? TimeSpan.FromMinutes(2),
                TimeSpan.FromSeconds(30)
            )
        );

    public void Dispose() => _s2sTransport?.Dispose();
}
