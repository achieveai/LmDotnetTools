using AchieveAi.LmDotnetTools.Sandbox;
using LmStreaming.Sample.Services.RemoteMcp;

namespace LmStreaming.Sample.Tests.Services.RemoteMcp;

/// <summary>
/// SC7 Way 1: what the harness does with each plugin MCP server row. Credentials come only from the egress key whose
/// host and port admit the url; <c>${VAR}</c> is never expanded; https always, http only to a literal loopback host
/// behind <c>RemoteMcp:AllowLoopbackHttp</c>. Each case names the change that turns it red.
/// </summary>
public class RemoteMcpServerPlanTests
{
    private const string HttpRefused =
        "skip: http is allowed only for a loopback host with RemoteMcp:AllowLoopbackHttp";
    private const string NeedsKey = "skip: " + RemoteMcpServerPlan.NeedsEgressKey;

    // Registry order is not id order: "wild" comes before "api-wild".
    private static readonly IReadOnlyList<PredefinedKeyEntry> Keys =
    [
        Key("acme", "acme.test", 443),
        Key("other", "other.test", 443),
        Key("alt", "alt.test", 8443),
        Key("wild", "*.wild.test", 443),
        Key("api-wild", "api.wild.test", 443),
        Key("loop", "127.0.0.1", 5123),
    ];

    private static readonly Dictionary<string, (SandboxMcpServer Row, bool AllowLoopbackHttp, string Expected)> Cases =
        new(StringComparer.Ordinal)
        {
            // Red: send a credential header as authored, or match no key.
            ["a key for its host replaces the credential header; a literal header is kept"] = (
                Http("https://acme.test/mcp", ("Authorization", "Bearer ${ACME_TOKEN}"), ("X-Trace", "on")),
                false,
                "connect key=acme headers=[X-Trace:on] dropped=[]"
            ),
            // Red: attach a key only when the plugin asks for a credential.
            ["a key for its host is attached when the plugin asks for nothing"] = (
                Http("https://acme.test/mcp"),
                false,
                "connect key=acme headers=[] dropped=[]"
            ),
            ["no credential asked and no key: literal headers only"] = (
                Http("https://free.test/mcp", ("X-Trace", "on")),
                false,
                "connect key=none headers=[X-Trace:on] dropped=[]"
            ),
            // Red: drop the needs-a-key gate (it would connect without its credential).
            ["a ${VAR} credential header with no key for its host"] = (
                Http("https://nokey.test/mcp", ("X-Api-Key", "${K}")),
                false,
                NeedsKey
            ),
            // Red: send a literal credential as authored.
            ["a literal credential header with no key for its host"] = (
                Http("https://nokey.test/mcp", ("Cookie", "sid=abc")),
                false,
                NeedsKey
            ),
            // Red: judge a credential by its header name only.
            ["a Bearer value under a neutral header name"] = (
                Http("https://nokey.test/mcp", ("X-Upstream", "Bearer abc")),
                false,
                NeedsKey
            ),
            // Red: ignore extra.
            ["extra.oauth with no key for its host"] = (
                Http("https://nokey.test/mcp", extra: "oauth"),
                false,
                NeedsKey
            ),
            ["extra.headersHelper with a key for its host"] = (
                Http("https://acme.test/mcp", extra: "headersHelper"),
                false,
                "connect key=acme headers=[] dropped=[]"
            ),
            // Red: expand or send a ${VAR} header; log a value instead of its variable names.
            ["a ${VAR} header is dropped and only its variable names are kept"] = (
                Http(
                    "https://free.test/mcp",
                    ("X-Region", "${REGION}"),
                    ("X-Mode", "${MODE:-fast}-${REGION}"),
                    ("X-Trace", "on")
                ),
                false,
                "connect key=none headers=[X-Trace:on] dropped=[REGION, MODE]"
            ),
            // Red: send a header that changes the request's target or framing while a key is attached (F-001: an
            // explicit Host becomes the TLS SNI, so a shared front door could route the key's token elsewhere).
            ["a framing header is never sent beside a key; only its name is kept"] = (
                Http(
                    "https://acme.test/mcp",
                    ("Host", "attacker.example"),
                    ("Transfer-Encoding", "chunked"),
                    ("X-Trace", "on")
                ),
                false,
                "connect key=acme headers=[X-Trace:on] dropped=[] droppedHeaders=[Host, Transfer-Encoding]"
            ),
            // Red: send a header whose name is not an RFC 7230 token, or log that name (it could hold anything).
            ["a header with an invalid name is dropped and its name is not kept"] = (
                Http("https://free.test/mcp", ("X Bad", "1"), ("X-Trace", "on")),
                false,
                "connect key=none headers=[X-Trace:on] dropped=[] droppedHeaders=[(an invalid header name)]"
            ),
            // Red: match a key host as a suffix of the url's host.
            ["a key for another host is never matched"] = (
                Http("https://evilacme.test/mcp", ("Authorization", "Bearer ${T}")),
                false,
                NeedsKey
            ),
            // Red: drop the port check.
            ["a key for its host on another port is never matched"] = (
                Http("https://alt.test/mcp", ("Authorization", "Bearer ${T}")),
                false,
                NeedsKey
            ),
            ["a key on the url's own port matches"] = (
                Http("https://alt.test:8443/mcp", ("Authorization", "Bearer ${T}")),
                false,
                "connect key=alt headers=[] dropped=[]"
            ),
            ["a wildcard key matches a subdomain"] = (
                Http("https://x.wild.test/mcp", ("Authorization", "Bearer ${T}")),
                false,
                "connect key=wild headers=[] dropped=[]"
            ),
            // Red: take the first key in registry order.
            ["two keys match: the first by id wins"] = (
                Http("https://api.wild.test/mcp", ("Authorization", "Bearer ${T}")),
                false,
                "connect key=api-wild headers=[] dropped=[]"
            ),
            // Red: let the flag open http to any host.
            ["http to a public host, even with the flag"] = (Http("http://acme.test/mcp"), true, HttpRefused),
            // Red: resolve the name, or prefix-match 127.
            ["http to a name that resolves to loopback, with the flag"] = (
                Http("http://127.0.0.1.nip.io/mcp"),
                true,
                HttpRefused
            ),
            // Red: default the flag on.
            ["http to loopback without the flag"] = (Http("http://127.0.0.1:5123/mcp"), false, HttpRefused),
            ["http to loopback with the flag, with the key for its host and port"] = (
                Http("http://127.0.0.1:5123/mcp", ("Authorization", "Bearer ${SC7_TOKEN}")),
                true,
                "connect key=loop headers=[] dropped=[]"
            ),
            ["http to localhost with the flag"] = (
                Http("http://localhost:5124/mcp"),
                true,
                "connect key=none headers=[] dropped=[]"
            ),
            ["http to ::1 with the flag"] = (
                Http("http://[::1]:5124/mcp"),
                true,
                "connect key=none headers=[] dropped=[]"
            ),
            // Red: expand the url's variable.
            ["a url that names a variable"] = (Http("https://${HOST}/mcp"), false, "skip: its url names a variable"),
            // Red: send the url's user info (a credential) to its host.
            ["a url with user info"] = (Http("https://u:p@acme.test/mcp"), false, "skip: its url carries user info"),
            ["a url that is not absolute"] = (Http("acme.test/mcp"), false, "skip: its url is not an absolute url"),
            ["a url that is neither http nor https"] = (
                Http("ftp://acme.test/mcp"),
                false,
                "skip: its url is not https"
            ),
            ["no url"] = (Http(null), false, "skip: it declares no url"),
            ["sse"] = (
                Row("sse", supported: true, "https://acme.test/sse"),
                false,
                "routine skip: only http servers are connected"
            ),
            ["stdio"] = (Row("stdio", supported: false, null), false, "routine skip: stdio servers are not supported"),
            ["another type the gateway marks unsupported"] = (
                Row("ws", supported: false, "wss://acme.test/ws"),
                false,
                "routine skip: the gateway marks it unsupported"
            ),
        };

    public static TheoryData<string> CaseNames
    {
        get
        {
            var names = new TheoryData<string>();
            foreach (var name in Cases.Keys)
            {
                names.Add(name);
            }

            return names;
        }
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Decide_FollowsTheWay1Rules(string name)
    {
        var (row, allowLoopbackHttp, expected) = Cases[name];

        // These cases isolate URL/credential rules after the operator has trusted the destination.
        string[] origins = Uri.TryCreate(row.Url, UriKind.Absolute, out var endpoint)
            ? [endpoint.GetLeftPart(UriPartial.Authority)]
            : [];
        Render(RemoteMcpServerPlan.Decide(row, Keys, allowLoopbackHttp, origins)).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://public.example/mcp")]
    [InlineData("https://127.0.0.1/mcp")]
    [InlineData("https://10.0.0.1/mcp")]
    [InlineData("https://169.254.169.254/mcp")]
    public void PluginDestination_IsDeniedWithoutOperatorAdmission(string url)
    {
        RemoteMcpServerPlan
            .Decide(Http(url), [], false)
            .SkipReason.Should()
            .Be("its origin is not allowed by RemoteMcp:AllowedOrigins");
    }

    [Theory]
    [InlineData("https://acme.test", "https://acme.test/mcp", true)]
    [InlineData("https://ACME.test:443", "https://acme.test/mcp", true)]
    [InlineData("https://10.0.0.1", "https://10.0.0.1/mcp", true)]
    [InlineData("https://[::1]", "https://[::1]/mcp", true)]
    [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:8080/mcp", true)]
    [InlineData("https://acme.test", "https://acme.test:8443/mcp", false)]
    [InlineData("https://acme.test", "https://evilacme.test/mcp", false)]
    [InlineData("https://acme.test", "http://acme.test/mcp", false)]
    [InlineData("https://user:secret@acme.test", "https://acme.test/mcp", false)]
    [InlineData("https://acme.test/path", "https://acme.test/mcp", false)]
    [InlineData("https://acme.test?x=1", "https://acme.test/mcp", false)]
    [InlineData("https://acme.test#fragment", "https://acme.test/mcp", false)]
    [InlineData("not a URI", "https://acme.test/mcp", false)]
    public void OperatorOriginAdmission_IsExactAndRejectsMalformedConfiguration(string origin, string url, bool allowed)
    {
        var decision = RemoteMcpServerPlan.Decide(Http(url), [], true, [origin]);

        (decision.Endpoint is not null).Should().Be(allowed);
    }

    /// <summary>
    /// The model-facing name is already the name the function registry registers: the registry collapses <c>__</c>
    /// and replaces unsafe characters, and a renamed tool can be neither hidden by the tiers nor found by
    /// <c>ExecuteTool</c> (defect seen in H1: <c>mcp__plugin_acme_api__lookup</c> was listed as
    /// <c>mcp_plugin_acme_api_lookup</c>).
    /// </summary>
    [Theory]
    [InlineData("acme", "api", "lookup", "mcp_plugin_acme_api_lookup")]
    [InlineData("my__plugin", "srv.v2", "do/it", "mcp_plugin_my_plugin_srv_v2_do_it")]
    public void ToolName_IsTheNameTheRegistryKeeps(string plugin, string server, string tool, string expected)
    {
        var name = RemoteMcpConnector.ToolName(plugin, server, tool);

        name.Should().Be(expected);
        AchieveAi.LmDotnetTools.LmCore.Middleware.FunctionCollisionDetector.SanitizeName(name).Should().Be(name);
    }

    private static string Render(RemoteMcpDecision decision) =>
        decision.SkipReason is { } reason
            ? (decision.RoutineSkip ? "routine skip: " : "skip: ") + reason
            : $"connect key={decision.Key?.Id ?? "none"} "
                + $"headers=[{string.Join(", ", decision.Headers.Select(h => $"{h.Key}:{h.Value}"))}] "
                + $"dropped=[{string.Join(", ", decision.DroppedVariables)}]"
                + (
                    decision.DroppedHeaders.Count > 0
                        ? $" droppedHeaders=[{string.Join(", ", decision.DroppedHeaders)}]"
                        : string.Empty
                );

    private static SandboxMcpServer Http(string? url, params (string Name, string Value)[] headers) =>
        Http(url, extra: null, headers);

    private static SandboxMcpServer Http(string? url, string? extra, params (string Name, string Value)[] headers) =>
        new()
        {
            Name = "api",
            Plugin = "acme",
            Marketplace = "mk",
            Type = "http",
            Supported = true,
            Url = url,
            Headers = [.. headers.Select(h => new SandboxMcpHeader { Name = h.Name, Value = h.Value })],
            Extra = extra is null
                ? []
                : new Dictionary<string, JsonElement> { [extra] = JsonDocument.Parse("{}").RootElement.Clone() },
        };

    private static SandboxMcpServer Row(string type, bool supported, string? url) =>
        new()
        {
            Name = "api",
            Plugin = "acme",
            Marketplace = "mk",
            Type = type,
            Supported = supported,
            Url = url,
        };

    private static PredefinedKeyEntry Key(string id, string host, int port) =>
        new()
        {
            Id = id,
            Host = host,
            Port = port,
            Kind = PredefinedKeyKind.CustomHeaders,
            Headers = [new PredefinedHeader("Authorization", "Bearer key-" + id)],
        };
}
