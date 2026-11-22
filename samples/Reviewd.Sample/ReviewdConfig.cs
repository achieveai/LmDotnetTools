using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.Sandbox;
using CodeReviewDaemon.Sample.Agents;
using Microsoft.Extensions.Configuration;

namespace Reviewd.Sample;

internal sealed class ReviewdConfig(IConfiguration configuration) : IDisposable
{
    private HttpClient? _s2sTransport;
    private HttpClient? _sandboxTransport;
    private ConversationListingCache? _listing;

    public IConfiguration Configuration { get; } = configuration;

    /// <summary>
    ///     The handler both the host and gateway clients send through, instead of the network. Tests set it to drive
    ///     a command's real wrapper, client construction included, over scripted endpoints.
    /// </summary>
    internal HttpMessageHandler? Transport { get; init; }
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

    /// <summary>
    ///     How many slots each repository has (<c>Reviewd:LanesPerRepo</c>, 1–6, default 6). An automatic run tries
    ///     only these, so it never builds an extra slot the runner does not keep warm.
    /// </summary>
    public int LanesPerRepo =>
        Configuration["Reviewd:LanesPerRepo"] is not { } value ? 6
        : int.TryParse(value, out var lanes) && lanes is >= 1 and <= 6 ? lanes
        : throw new ArgumentException("Reviewd:LanesPerRepo must be between 1 and 6.");

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

        // The conversation listing (title-prefix lookup) is paged and, with a large conversation store, a
        // page took 17-22s under a full batch; 30s timed out and held nine PRs before they were admitted.
        _s2sTransport ??= new HttpClient(
            Transport ?? new HttpClientHandler { AllowAutoRedirect = false },
            disposeHandler: Transport is null
        )
        {
            BaseAddress = address,
            Timeout = TimeSpan.FromSeconds(120),
        };
        // Every review in this process reads one copy of the listing. A thread missing from it is fetched for again
        // after 30s, so a conversation another process just created is not hidden for the whole copy's life.
        _listing ??= new ConversationListingCache(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(30));
        return new LmStreamingS2SClient(_s2sTransport, S2SSecret, AppId, AppKey, _listing);
    }

    public SandboxClient CreateSandboxClient(TimeSpan? executionTimeout = null)
    {
        var options = new SandboxClientOptions(
            new Uri(GatewayUrl, UriKind.Absolute),
            AppId,
            AppKey ?? "",
            executionTimeout ?? TimeSpan.FromMinutes(2),
            TimeSpan.FromSeconds(30)
        );
        if (Transport is null)
        {
            return new SandboxClient(options);
        }
        _sandboxTransport ??= new HttpClient(Transport, disposeHandler: false);
        return new SandboxClient(options, _sandboxTransport);
    }

    public void Dispose()
    {
        _s2sTransport?.Dispose();
        _sandboxTransport?.Dispose();
    }
}
