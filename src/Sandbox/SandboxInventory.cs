namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// What the gateway confirmed it actually loaded into a sandbox at creation time — the plugins,
/// skills, and sub-agents that are live in the session, reported atomically with the create result.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately not the same thing as <see cref="SandboxClient.PreviewMarketplacesAsync"/>
/// or the <see cref="SandboxCreateRequest.Marketplaces"/> a caller asked for. Those describe what is
/// <i>available</i> and what was <i>requested</i>; this describes what the gateway says is
/// <i>loaded</i>. A request that names three marketplaces may load none of them, so the two must
/// never be conflated — a subscriber that treats requested data as confirmed would report an
/// inventory the session does not have.
/// </para>
/// <para>
/// Reporting is fail-closed. <see cref="Status"/> is <see cref="SandboxInventoryStatuses.Confirmed"/>
/// only when the gateway explicitly said so; a gateway that omits the block, or that sends items
/// without claiming them confirmed, yields <see cref="SandboxInventoryStatuses.Unavailable"/> with a
/// reason and no items. Silence is never upgraded into a claim.
/// </para>
/// </remarks>
public sealed class SandboxInventory
{
    /// <summary>
    /// Whether the item list can be trusted as what the session loaded. See
    /// <see cref="SandboxInventoryStatuses"/>. Open vocabulary — an unrecognized value is preserved
    /// rather than rejected, but only the exact value <c>confirmed</c> means confirmed.
    /// </summary>
    public string Status { get; }

    /// <summary>
    /// Why the inventory could not be confirmed, when <see cref="Status"/> is not
    /// <see cref="SandboxInventoryStatuses.Confirmed"/>. Always populated in that case, so a
    /// consumer never has to guess between "nothing was loaded" and "nobody could tell us".
    /// </summary>
    public string? UnavailableReason { get; }

    /// <summary>
    /// The confirmed items, defensively copied at construction. Empty whenever <see cref="Status"/>
    /// is not <see cref="SandboxInventoryStatuses.Confirmed"/> — an unconfirmed list is dropped
    /// rather than surfaced, because a caller reading items without checking the status would
    /// otherwise silently treat unconfirmed data as confirmed.
    /// </summary>
    public IReadOnlyList<SandboxInventoryItem> Items { get; }

    /// <summary>Builds an inventory from a gateway-reported status, reason, and item list.</summary>
    /// <param name="status">The gateway's status value, or <see langword="null"/> when it reported none.</param>
    /// <param name="unavailableReason">The gateway's reason, when it supplied one.</param>
    /// <param name="items">The reported items. Kept only when <paramref name="status"/> is <c>confirmed</c>.</param>
    public SandboxInventory(string? status, string? unavailableReason, IReadOnlyList<SandboxInventoryItem>? items)
    {
        var confirmed = string.Equals(status, SandboxInventoryStatuses.Confirmed, StringComparison.Ordinal);
        Status = confirmed ? SandboxInventoryStatuses.Confirmed : SandboxInventoryStatuses.Unavailable;
        Items = confirmed && items is not null ? [.. items] : [];
        UnavailableReason =
            confirmed ? null
            : unavailableReason is { Length: > 0 } reason ? reason
            : DefaultReasonFor(status);
    }

    /// <summary>
    /// The inventory reported when the gateway said nothing at all — the shape every pre-inventory
    /// gateway produces, and the default a create result carries when the block is absent.
    /// </summary>
    public static SandboxInventory Unavailable(string reason) =>
        new(status: null, unavailableReason: reason, items: null);

    /// <summary>
    /// The reason a result carries when the gateway said nothing about inventory at all — an older
    /// gateway, or any result shape that does not include the block.
    /// </summary>
    internal const string NoInventoryReported = "The gateway did not report a loaded inventory with the create result.";

    /// <summary>
    /// Names the specific silence, so an operator can tell an old gateway apart from a gateway that
    /// answered but declined to confirm.
    /// </summary>
    private static string DefaultReasonFor(string? status) =>
        status is null or { Length: 0 }
            ? NoInventoryReported
            : $"The gateway reported inventory status '{status}', which does not confirm the loaded items.";
}

/// <summary>One plugin, skill, or sub-agent the gateway confirmed is loaded in the session.</summary>
/// <remarks>
/// Identity and version only. Manifests, descriptions, install paths, source repositories, and
/// publisher metadata are deliberately absent: a lifecycle subscriber needs to know <i>what</i> is
/// loaded, and carrying the rest would put private catalog content into an event stream that has a
/// different audience from the sandbox itself.
/// </remarks>
public sealed class SandboxInventoryItem
{
    /// <summary>What kind of thing this is — <c>plugin</c>, <c>skill</c>, or <c>agent</c>. Open vocabulary.</summary>
    public string Kind { get; }

    /// <summary>The gateway's identifier for the item, unique within its <see cref="Kind"/>.</summary>
    public string Id { get; }

    /// <summary>The loaded version, when the gateway tracks one for this kind.</summary>
    public string? Version { get; }

    /// <summary>Builds an item. Both <paramref name="kind"/> and <paramref name="id"/> are required.</summary>
    public SandboxInventoryItem(string kind, string id, string? version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Kind = kind;
        Id = id;
        Version = version;
    }
}

/// <summary>
/// The status values <see cref="SandboxInventory.Status"/> takes.
/// </summary>
/// <remarks>Open vocabulary — an unrecognized value is preserved, not rejected.</remarks>
public static class SandboxInventoryStatuses
{
    /// <summary>The gateway confirmed the listed items are loaded in the session.</summary>
    public const string Confirmed = "confirmed";

    /// <summary>
    /// The gateway could not confirm what is loaded. The reason says why, and the item list is
    /// empty.
    /// </summary>
    public const string Unavailable = "unavailable";
}

/// <summary>The item kinds a gateway reports in a confirmed inventory.</summary>
/// <remarks>Open vocabulary — an unrecognized value is preserved, not rejected.</remarks>
public static class SandboxInventoryKinds
{
    /// <summary>A plugin loaded from a selected marketplace.</summary>
    public const string Plugin = "plugin";

    /// <summary>A skill contributed by a loaded plugin.</summary>
    public const string Skill = "skill";

    /// <summary>A sub-agent contributed by a loaded plugin.</summary>
    public const string Agent = "agent";
}
