using System.Collections.ObjectModel;

namespace LmStreaming.Sample.SandboxApps;

public sealed record SandboxAppDefinition(
    string Id,
    string Name,
    string Executable,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    int MaxRequestBytes,
    long MaxOutputBytes,
    TimeSpan ExecutionTimeout
);

/// <summary>Immutable, administrator supplied commands and the dedicated app site.</summary>
public sealed class SandboxAppCatalog
{
    private readonly IReadOnlyDictionary<string, SandboxAppDefinition> _apps;

    private SandboxAppCatalog(
        bool enabled,
        string siteDomain,
        string appDomain,
        int httpsPort,
        bool sameOriginDevelopment,
        Uri? browserOrigin,
        bool productionActivationBlocked,
        Dictionary<string, SandboxAppDefinition> apps
    )
    {
        Enabled = enabled;
        SiteDomain = siteDomain;
        AppDomain = appDomain;
        HttpsPort = httpsPort;
        SameOriginDevelopment = sameOriginDevelopment;
        BrowserOrigin = browserOrigin;
        ProductionActivationBlocked = productionActivationBlocked;
        _apps = new ReadOnlyDictionary<string, SandboxAppDefinition>(apps);
    }

    public bool Enabled { get; }
    public string SiteDomain { get; }
    public string AppDomain { get; }
    public int HttpsPort { get; }
    public bool SameOriginDevelopment { get; }
    public Uri? BrowserOrigin { get; }
    public bool ProductionActivationBlocked { get; }
    public IReadOnlyCollection<SandboxAppDefinition> Apps => [.. _apps.Values];

    public bool TryGet(string id, out SandboxAppDefinition? app) => _apps.TryGetValue(id, out app);

    public bool IsAvailableFor(string requestHost, bool isHttps, bool identityEnforced, int? requestPort = null)
    {
        ArgumentNullException.ThrowIfNull(requestHost);
        if (SameOriginDevelopment)
            return ResolveSameOriginBrowserOrigin(requestHost, requestPort, isHttps) is not null;
        return Enabled
            && isHttps
            && identityEnforced
            && IsDnsSubdomain(requestHost, SiteDomain)
            && !IsAppHost(requestHost);
    }

    public Uri? ResolveSameOriginBrowserOrigin(string requestHost, int? requestPort, bool isHttps)
    {
        ArgumentNullException.ThrowIfNull(requestHost);
        if (!SameOriginDevelopment || BrowserOrigin is null || (requestPort ?? 443) != BrowserOrigin.Port)
            return null;
        if (string.Equals(requestHost, BrowserOrigin.Host, StringComparison.OrdinalIgnoreCase))
            return BrowserOrigin;
        // Local Kestrel HTTPS is a second development entry point beside the configured front door.
        return isHttps && string.Equals(requestHost, "localhost", StringComparison.OrdinalIgnoreCase)
            ? new UriBuilder(Uri.UriSchemeHttps, "localhost", BrowserOrigin.Port).Uri
            : null;
    }

    public bool IsAppHost(string requestHost)
    {
        ArgumentNullException.ThrowIfNull(requestHost);
        return IsDnsSubdomain(requestHost, AppDomain);
    }

    public static SandboxAppCatalog Load(IConfiguration configuration, bool isDevelopment = true)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("SandboxApps");
        var enabled = section.GetValue<bool>("Enabled");
        if (!isDevelopment)
        {
            // A stray development app setting must not take the production chat host down.
            return new SandboxAppCatalog(
                false,
                NormalizeDomain(section["SiteDomain"]),
                NormalizeDomain(section["AppDomain"]),
                443,
                false,
                null,
                enabled,
                new Dictionary<string, SandboxAppDefinition>(StringComparer.Ordinal)
            );
        }
        var sameOriginRequested = section.GetValue<bool>("SameOriginDevelopment");
        var sameOriginDevelopment = enabled && isDevelopment && sameOriginRequested;
        Uri? browserOrigin = null;
        if (sameOriginDevelopment)
        {
            if (
                !Uri.TryCreate(section["BrowserOrigin"], UriKind.Absolute, out browserOrigin)
                || browserOrigin.Scheme != Uri.UriSchemeHttps
                || browserOrigin.HostNameType != UriHostNameType.Dns
                || browserOrigin.AbsolutePath != "/"
                || browserOrigin.Query.Length != 0
                || browserOrigin.Fragment.Length != 0
                || browserOrigin.UserInfo.Length != 0
            )
                throw new InvalidOperationException("SandboxApps:BrowserOrigin must be an HTTPS origin.");
        }
        var site = NormalizeDomain(section["SiteDomain"]);
        var appDomain = NormalizeDomain(section["AppDomain"]);
        var httpsPort = section.GetValue<int?>("HttpsPort") ?? 443;
        if (httpsPort is < 1 or > 65535)
            throw new InvalidOperationException("SandboxApps:HttpsPort must be a valid TCP port.");
        var apps = new Dictionary<string, SandboxAppDefinition>(StringComparer.Ordinal);
        foreach (var child in section.GetSection("Apps").GetChildren())
        {
            var id = child.Key;
            var executable = child["Executable"];
            if (!ValidId(id) || !ValidExecutable(executable))
            {
                if (enabled && isDevelopment)
                    throw new InvalidOperationException($"SandboxApps:Apps:{id} has an invalid id or executable.");
                continue;
            }

            var arguments = child
                .GetSection("Arguments")
                .GetChildren()
                .OrderBy(x => int.TryParse(x.Key, out var n) ? n : int.MaxValue)
                .Select(x => x.Value ?? string.Empty)
                .ToArray();
            if (arguments.Any(x => x.Contains('\0', StringComparison.Ordinal)))
                throw new InvalidOperationException($"SandboxApps:Apps:{id} has a NUL argument.");
            var maxRequestBytes = child.GetValue<int?>("MaxRequestBytes") ?? 65536;
            var maxOutputBytes = child.GetValue<long?>("MaxOutputBytes") ?? 8388608L;
            var timeoutSeconds = child.GetValue<int?>("TimeoutSeconds") ?? 30;
            if (
                (maxRequestBytes is < 1 or > 64 * 1024)
                || (maxOutputBytes is < 1 or > 8L * 1024 * 1024)
                || (timeoutSeconds is < 1 or > 30)
            )
                throw new InvalidOperationException($"SandboxApps:Apps:{id} has invalid resource limits.");
            apps.Add(
                id,
                new SandboxAppDefinition(
                    id,
                    string.IsNullOrWhiteSpace(child["Name"]) ? id : child["Name"]!,
                    executable!,
                    Array.AsReadOnly(arguments),
                    child["WorkingDirectory"],
                    maxRequestBytes,
                    maxOutputBytes,
                    TimeSpan.FromSeconds(timeoutSeconds)
                )
            );
        }

        if (
            enabled
            && isDevelopment
            && !sameOriginDevelopment
            && (!ValidDomain(site) || !ValidDomain(appDomain) || !IsDnsSubdomain(appDomain, site))
        )
            throw new InvalidOperationException("SandboxApps requires a distinct DNS app domain below SiteDomain.");

        return new SandboxAppCatalog(
            enabled && isDevelopment,
            site,
            appDomain,
            httpsPort,
            sameOriginDevelopment,
            browserOrigin,
            enabled && !isDevelopment,
            apps
        );
    }

    private static string NormalizeDomain(string? value) =>
        (value ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();

    private static bool ValidDomain(string host) =>
        host.Contains('.', StringComparison.Ordinal)
        && Uri.CheckHostName(host) == UriHostNameType.Dns
        && !host.Contains("..", StringComparison.Ordinal);

    private static bool IsDnsSubdomain(string host, string parent) =>
        ValidDomain(host) && ValidDomain(parent) && host.EndsWith("." + parent, StringComparison.OrdinalIgnoreCase);

    private static bool ValidId(string id) =>
        id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static bool ValidExecutable(string? path) =>
        path is not null
        && path.StartsWith("/plugins/sandbox-apps/", StringComparison.Ordinal)
        && !path.Contains("//", StringComparison.Ordinal)
        && !path.Split('/').Contains("..")
        && !path.Contains('\0', StringComparison.Ordinal);
}
