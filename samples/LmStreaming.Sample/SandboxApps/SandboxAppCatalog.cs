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
        Dictionary<string, SandboxAppDefinition> apps
    )
    {
        Enabled = enabled;
        SiteDomain = siteDomain;
        AppDomain = appDomain;
        HttpsPort = httpsPort;
        _apps = new ReadOnlyDictionary<string, SandboxAppDefinition>(apps);
    }

    public bool Enabled { get; }
    public string SiteDomain { get; }
    public string AppDomain { get; }
    public int HttpsPort { get; }
    public IReadOnlyCollection<SandboxAppDefinition> Apps => [.. _apps.Values];

    public bool TryGet(string id, out SandboxAppDefinition? app) => _apps.TryGetValue(id, out app);

    public bool IsAvailableFor(string requestHost, bool isHttps, bool identityEnforced)
    {
        ArgumentNullException.ThrowIfNull(requestHost);
        return Enabled
            && isHttps
            && identityEnforced
            && IsDnsSubdomain(requestHost, SiteDomain)
            && !IsAppHost(requestHost);
    }

    public bool IsAppHost(string requestHost)
    {
        ArgumentNullException.ThrowIfNull(requestHost);
        return IsDnsSubdomain(requestHost, AppDomain);
    }

    public static SandboxAppCatalog Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("SandboxApps");
        var enabled = section.GetValue<bool>("Enabled");
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
                if (enabled)
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

        if (enabled && (!ValidDomain(site) || !ValidDomain(appDomain) || !IsDnsSubdomain(appDomain, site)))
            throw new InvalidOperationException("SandboxApps requires a distinct DNS app domain below SiteDomain.");

        return new SandboxAppCatalog(enabled, site, appDomain, httpsPort, apps);
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
