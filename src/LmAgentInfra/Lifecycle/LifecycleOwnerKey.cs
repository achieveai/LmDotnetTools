using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Lifecycle;

/// <summary>
/// The scope every lifecycle subscription, delivery, and remote approval decision is confined to.
/// Two owners never see each other's events, and a decision minted under one owner can never settle
/// a request that belongs to another (ADR 0005).
/// <para>
/// The key is <b>minted, never parsed</b>. There is no constructor, no deserialization path, and no
/// JSON attribute on this type — it is not a member of any lifecycle payload and is not registered
/// on <c>LifecycleJsonContext</c>, so "an owner key is never serialized onto an envelope" holds
/// structurally rather than by convention. It is likewise never inferred from a thread id, run id,
/// session id, workspace id, tool call id, or source stream: those identify *what happened*, not
/// *who is entitled to hear about it*. The only way to obtain one is
/// <see cref="ForAppId(string)"/> from an already-authenticated app identity, or
/// <see cref="ForCredential(SandboxCredential)"/>.
/// </para>
/// <para>
/// Being a reference type is deliberate. A struct would have a <c>default</c> instance carrying an
/// empty value, and an empty owner compared against an empty owner matches — which is exactly how a
/// fail-closed check becomes a fail-open one. Here the absence of an owner is <c>null</c>, and the
/// nullable-reference analyzer makes forgetting to handle it a compile-time complaint.
/// </para>
/// </summary>
public sealed record LifecycleOwnerKey
{
    private LifecycleOwnerKey(string value) => Value = value;

    /// <summary>
    /// The opaque owner identifier. Safe to log and to correlate on; not a secret, and not a
    /// low-cardinality value — per ADR 0005 it must never be used as a metric dimension.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Mints an owner key for an app identity the caller has <b>already authenticated</b>. This
    /// method validates shape only; it performs no authentication of its own, so passing an app id
    /// straight off an unverified request would defeat the entire scoping model.
    /// </summary>
    /// <param name="appId">The authenticated app identity, typically
    /// <see cref="SandboxCredential.AppId"/>.</param>
    /// <exception cref="ArgumentException">The app id is null, empty, or whitespace.</exception>
    public static LifecycleOwnerKey ForAppId(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        return new LifecycleOwnerKey(appId);
    }

    /// <summary>
    /// Mints an owner key from a sandbox credential, using its
    /// <see cref="SandboxCredential.AppId"/>. The credential's key material is not read and never
    /// becomes part of the owner key.
    /// </summary>
    public static LifecycleOwnerKey ForCredential(SandboxCredential credential) => ForAppId(credential.AppId);

    /// <inheritdoc />
    public override string ToString() => Value;
}
