using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Identity;
using Microsoft.AspNetCore.DataProtection;

namespace LmStreaming.Sample.FileBrowser;

/// <summary>One minted workspace read grant: the opaque token and the instant it stops validating.</summary>
/// <param name="Token">The opaque, signed, time-limited token. Goes in a URL PATH segment.</param>
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
/// <c>Authorization</c> header the client's <c>apiFetch</c> attaches, so the credential has to travel in the
/// URL — and it has to travel in the URL's PATH, because RFC 3986 relative-reference resolution replaces the
/// query, which would strip a <c>?grant=</c> from every relative subresource the page loads.
/// </summary>
/// <remarks>
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
