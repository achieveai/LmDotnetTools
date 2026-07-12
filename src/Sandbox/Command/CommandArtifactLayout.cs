namespace AchieveAi.LmDotnetTools.Sandbox.Command;

/// <summary>
/// Internal, non-configurable constants for the command artifact protocol: where artifacts live, the
/// inline/chunk thresholds that keep every read under the gateway's <c>exec</c> truncation limits, and
/// the lease/stale-cleanup bounds. These are deliberately NOT public tuning knobs — the recovery
/// protocol's correctness depends on them, so exposing them would let a caller silently break it.
/// </summary>
internal static class CommandArtifactLayout
{
    /// <summary>
    /// Reserved per-session artifact root, relative to the sandbox workspace root
    /// (<c>$SANDBOX_WORKSPACE</c>, defaulting to <c>/workspace</c>). It lives inside the persisted
    /// workspace volume so artifacts survive a gateway container rematerialization, which is what makes
    /// recovery possible. The leading dot keeps it out of the way of ordinary workspace listings.
    /// </summary>
    public const string ArtifactRootRelative = ".lmsbx-sdk/ops";

    /// <summary>
    /// Streams at or below this many bytes are base64-inlined in the manifest, so a small command's
    /// output needs no extra read round-trip. Chosen well below the gateway's 20&#160;KB <c>exec</c>
    /// truncation so an inlined manifest sentinel line is never itself truncated.
    /// </summary>
    public const int InlineThresholdBytes = 8 * 1024;

    /// <summary>
    /// Maximum raw bytes fetched per chunk read. Its base64 encoding (~4/3 expansion, ≈16&#160;KB) stays
    /// under both the gateway's 20&#160;KB byte limit and its 500-line limit for a single read, so a
    /// chunk is always returned whole and never truncated.
    /// </summary>
    public const int ReadChunkBytes = 12 * 1024;

    /// <summary>Manifest bytes (the <c>exec</c> truncation cap) — an upper bound on how large a manifest sentinel line may be before it would truncate.</summary>
    public const int GatewayOutputByteLimit = 20 * 1024;

    /// <summary>Grace period added to the execution timeout when computing an operation's lease expiry.</summary>
    public const int LeaseGraceSeconds = 300;

    /// <summary>An artifact is eligible for stale cleanup only once it is at least this old (24 hours).</summary>
    public const long StaleAgeSeconds = 24 * 60 * 60;

    /// <summary>
    /// Upper bound on how many artifact directories a single stale-cleanup pass inspects. Cleanup is
    /// best-effort maintenance, not a guarantee, so it is bounded rather than an unbounded scan that
    /// could dominate a command's latency in a workspace with many stale operations.
    /// </summary>
    public const int StaleScanLimit = 256;
}
