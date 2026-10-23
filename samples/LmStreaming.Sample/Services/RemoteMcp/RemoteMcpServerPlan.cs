using System.Net;
using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using AchieveAi.LmDotnetTools.Sandbox;

namespace LmStreaming.Sample.Services.RemoteMcp;

/// <summary>
/// What the harness does with one plugin MCP server row: connect to <see cref="Endpoint"/> with
/// <see cref="Headers"/> and, when set, the headers of egress key <see cref="Key"/>; or skip it for
/// <see cref="SkipReason"/>.
/// </summary>
/// <remarks>
/// SECRET: <see cref="Endpoint"/> and <see cref="Key"/> are credential material. A class, not a record, so no
/// generated <c>ToString</c> ever prints them.
/// </remarks>
internal sealed class RemoteMcpDecision
{
    /// <summary>The endpoint to connect to; null when skipped.</summary>
    public Uri? Endpoint { get; private init; }

    /// <summary>The plugin's literal, non-credential headers, sent as authored.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; private init; } = [];

    /// <summary>The egress key whose headers are attached (they win over a same-named literal header).</summary>
    public PredefinedKeyEntry? Key { get; private init; }

    /// <summary>The variable names of the <c>${VAR}</c> headers left out (names only, never a value).</summary>
    public IReadOnlyList<string> DroppedVariables { get; private init; } = [];

    /// <summary>
    /// The names of the framing or hop-by-hop headers left out (<c>Host</c>, <c>Content-Length</c>, ...); a name that
    /// is not an RFC 7230 token is kept as <see cref="RemoteMcpServerPlan.InvalidHeaderName"/>. Never a value.
    /// </summary>
    public IReadOnlyList<string> DroppedHeaders { get; private init; } = [];

    /// <summary>A fixed phrase saying why the server is not connected; null when it is. Holds no row value.</summary>
    public string? SkipReason { get; private init; }

    /// <summary>True when the reason is routine (a type this app never connects), not a problem to look at.</summary>
    public bool RoutineSkip { get; private init; }

    public static RemoteMcpDecision Skip(string reason, bool routine = false) =>
        new() { SkipReason = reason, RoutineSkip = routine };

    public static RemoteMcpDecision Connect(
        Uri endpoint,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        PredefinedKeyEntry? key,
        IReadOnlyList<string> droppedVariables,
        IReadOnlyList<string> droppedHeaders
    ) =>
        new()
        {
            Endpoint = endpoint,
            Headers = headers,
            Key = key,
            DroppedVariables = droppedVariables,
            DroppedHeaders = droppedHeaders,
        };
}

/// <summary>
/// The rules that turn one plugin MCP server row into a <see cref="RemoteMcpDecision"/> (SC7, Way 1). Pure: no I/O.
/// </summary>
/// <remarks>
/// <para>
/// <b>Credentials come only from egress keys.</b> A key matches a server when its port is the url's port and its
/// host pattern admits the url's host: the same gate the auth webhook applies before it injects that key
/// (<c>AuthWebhookController.IsDestinationAllowed</c>). <c>${VAR}</c> is never expanded, from any environment: a plugin
/// could otherwise name a variable that holds the app's own secret and have it sent to its url.
/// </para>
/// <para>
/// A credential-looking header (see <see cref="IsCredentialHeader"/>), <c>extra.oauth</c> or
/// <c>extra.headersHelper</c> needs a key: with one, the header is dropped and the key's headers are sent; without
/// one, the server is not connected. A header a key may not set either (<c>Host</c>, <c>Content-Length</c>,
/// <c>Connection</c>, <c>Transfer-Encoding</c>, <c>Content-Type</c>, or a name that is not a token) is dropped. Any
/// other header with a <c>${</c> is dropped. Literal headers are sent as authored. A matched key is attached even when
/// the plugin asks for nothing, as the sandbox's egress does.
/// </para>
/// <para>
/// <b>Url:</b> https always; http only to a literal loopback host (<c>localhost</c>, 127.0.0.0/8, ::1, no DNS) when
/// <c>RemoteMcp:AllowLoopbackHttp</c> is on. A url with user info or a <c>${</c> is not connected. Only <c>http</c>
/// (streamable HTTP) servers are connected.
/// </para>
/// </remarks>
internal static partial class RemoteMcpServerPlan
{
    /// <summary>The reason a server that needs a credential, with no egress key for its host, is not connected.</summary>
    internal const string NeedsEgressKey = "needs an egress key for its host";

    /// <summary>Stands in for a dropped header name that is not an RFC 7230 token: such a name is never logged.</summary>
    internal const string InvalidHeaderName = "(an invalid header name)";

    // Header-name fragments that mark a credential: Authorization, X-Api-Key, X-Auth-Token, Cookie, ...
    private static readonly string[] CredentialNameParts =
    [
        "auth",
        "token",
        "key",
        "secret",
        "password",
        "credential",
        "cookie",
        "session",
    ];

    // Entry keys that mean the server authenticates by a mechanism this app does not run.
    private static readonly string[] CredentialExtraKeys = ["oauth", "headersHelper"];

    /// <summary>The decision for <paramref name="row"/> given the egress <paramref name="keys"/> that exist now.</summary>
    internal static RemoteMcpDecision Decide(
        SandboxMcpServer row,
        IReadOnlyList<PredefinedKeyEntry> keys,
        bool allowLoopbackHttp
    )
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(keys);

        if (!row.Supported)
        {
            return RemoteMcpDecision.Skip(
                string.Equals(row.Type, "stdio", StringComparison.Ordinal)
                    ? "stdio servers are not supported"
                    : "the gateway marks it unsupported",
                routine: true
            );
        }

        if (!string.Equals(row.Type, "http", StringComparison.Ordinal))
        {
            return RemoteMcpDecision.Skip("only http servers are connected", routine: true);
        }

        if (string.IsNullOrWhiteSpace(row.Url))
        {
            return RemoteMcpDecision.Skip("it declares no url");
        }

        if (row.Url.Contains("${", StringComparison.Ordinal))
        {
            return RemoteMcpDecision.Skip("its url names a variable");
        }

        if (!Uri.TryCreate(row.Url, UriKind.Absolute, out var endpoint))
        {
            return RemoteMcpDecision.Skip("its url is not an absolute url");
        }

        if (endpoint.UserInfo.Length > 0)
        {
            return RemoteMcpDecision.Skip("its url carries user info");
        }

        if (endpoint.Scheme == Uri.UriSchemeHttp)
        {
            if (!allowLoopbackHttp || !IsLiteralLoopback(endpoint))
            {
                return RemoteMcpDecision.Skip(
                    "http is allowed only for a loopback host with RemoteMcp:AllowLoopbackHttp"
                );
            }
        }
        else if (endpoint.Scheme != Uri.UriSchemeHttps)
        {
            return RemoteMcpDecision.Skip("its url is not https");
        }

        var key = keys.Where(k => KeyAdmits(k, endpoint)).OrderBy(k => k.Id, StringComparer.Ordinal).FirstOrDefault();
        var needsCredential = row.Headers.Any(IsCredentialHeader) || CredentialExtraKeys.Any(row.Extra.ContainsKey);
        if (needsCredential && key is null)
        {
            return RemoteMcpDecision.Skip(NeedsEgressKey);
        }

        var headers = new List<KeyValuePair<string, string>>();
        var dropped = new List<string>();
        var droppedHeaders = new List<string>();
        foreach (var header in row.Headers)
        {
            if (IsCredentialHeader(header))
            {
                // The key supplies the credential (needsCredential already required one).
                continue;
            }

            // The names an egress key may not set either: Host would pick the TLS SNI, so a shared front door could
            // route the key's token to another origin; the framing names can split or hijack the request.
            if (EgressHostMatcher.ValidateHeaderName(header.Name) is not null)
            {
                droppedHeaders.Add(IsToken(header.Name) ? header.Name : InvalidHeaderName);
                continue;
            }

            if (header.Value.Contains("${", StringComparison.Ordinal))
            {
                foreach (Match variable in VariablePattern().Matches(header.Value))
                {
                    if (!dropped.Contains(variable.Groups["name"].Value))
                    {
                        dropped.Add(variable.Groups["name"].Value);
                    }
                }

                continue;
            }

            headers.Add(new KeyValuePair<string, string>(header.Name, header.Value));
        }

        return RemoteMcpDecision.Connect(endpoint, headers, key, dropped, droppedHeaders);
    }

    /// <summary>
    /// The auth webhook's destination gate for a predefined key (<c>AuthWebhookController.IsDestinationAllowed</c>):
    /// the key's own port, and its host pattern admits the url's host.
    /// </summary>
    internal static bool KeyAdmits(PredefinedKeyEntry key, Uri endpoint) =>
        key.Port == endpoint.Port && EgressHostMatcher.IsAllowed([key.Host], endpoint.Host);

    /// <summary>
    /// A header that carries a credential: its name holds <c>auth</c>, <c>token</c>, <c>key</c>, <c>secret</c>,
    /// <c>password</c>, <c>credential</c>, <c>cookie</c> or <c>session</c>, or its value starts <c>Bearer </c> or
    /// <c>Basic </c>. Literal or <c>${VAR}</c>, it is never sent: the matched egress key replaces it.
    /// </summary>
    internal static bool IsCredentialHeader(SandboxMcpHeader header) =>
        CredentialNameParts.Any(part => header.Name.Contains(part, StringComparison.OrdinalIgnoreCase))
        || header.Value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        || header.Value.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase);

    // localhost, 127.0.0.0/8 or ::1 as written: never a DNS lookup.
    private static bool IsLiteralLoopback(Uri endpoint) =>
        string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (
            endpoint.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
            && IPAddress.TryParse(endpoint.DnsSafeHost, out var address)
            && IPAddress.IsLoopback(address)
        );

    [GeneratedRegex(@"\$\{(?<name>[^}:]+)(?::-[^}]*)?\}", RegexOptions.CultureInvariant)]
    private static partial Regex VariablePattern();

    // A name safe to log: an RFC 7230 token, the charset EgressHostMatcher.ValidateHeaderName requires.
    private static bool IsToken(string name) => TokenPattern().IsMatch(name);

    [GeneratedRegex(@"^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}
