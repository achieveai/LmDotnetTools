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
    /// output needs no extra read round-trip; any larger stream carries <c>inline:null</c> and is
    /// reassembled EXACTLY through integrity-verified chunk reads instead.
    /// </summary>
    /// <remarks>
    /// The manifest sentinel line is <b>nested</b>-encoded: each inlined stream is base64 inside
    /// <c>manifest.json</c>, and the whole <c>manifest.json</c> is base64 again for the single wire line
    /// (<c>@@LMSBX-SENTINEL@@ MANIFEST &lt;base64(manifest.json)&gt;</c>). The worst case is therefore
    /// <b>both</b> streams inlined at this threshold. That must stay provably under the gateway's
    /// <see cref="GatewayOutputByteLimit"/> <c>exec</c> truncation with margin, so this threshold is
    /// bounded rather than "as large as fits":
    /// <list type="bullet">
    /// <item>per-stream inline base64 ≈ 4·⌈4096/3⌉ = 5464 bytes;</item>
    /// <item><c>manifest.json</c> ≈ 2·5464 + fixed envelope ≈ 11.3&#160;KB;</item>
    /// <item>outer base64 + sentinel prefix ≈ 4/3·11.3&#160;KB + 28 ≈ 15.1&#160;KB.</item>
    /// </list>
    /// 15.1&#160;KB &lt; <see cref="ManifestLineByteBudget"/> (16&#160;KB) &lt; <see cref="GatewayOutputByteLimit"/>
    /// (20&#160;KB), leaving ≈5&#160;KB of head-room; and the line has no embedded newline (chunk/inline
    /// base64 is joined with <c>tr -d '\n'</c>), so the 500-line limit never applies to it. The bound is
    /// asserted directly in <c>CommandManifestTransportTests</c>.
    /// </remarks>
    public const int InlineThresholdBytes = 4 * 1024;

    /// <summary>
    /// Maximum raw bytes fetched per chunk read. Its base64 encoding (~4/3 expansion, ≈16&#160;KB) stays
    /// under both the gateway's 20&#160;KB byte limit and its 500-line limit for a single read, so a
    /// chunk is always returned whole and never truncated.
    /// </summary>
    public const int ReadChunkBytes = 12 * 1024;

    /// <summary>The gateway's <c>exec</c> byte truncation cap (<c>MAX_OUTPUT_SIZE</c>) — output beyond this many bytes is discarded before the SDK sees it.</summary>
    public const int GatewayOutputByteLimit = 20 * 1024;

    /// <summary>The gateway's <c>exec</c> line truncation cap (<c>MAX_OUTPUT_LINES</c>) — output beyond this many lines is discarded before the SDK sees it.</summary>
    public const int GatewayOutputLineLimit = 500;

    /// <summary>
    /// The maximum size a manifest sentinel line is allowed to reach, i.e. the proven upper bound the
    /// <see cref="InlineThresholdBytes"/> is chosen against. Deliberately ≥4&#160;KB below
    /// <see cref="GatewayOutputByteLimit"/> so the worst-case (both streams inlined, nested base64) line
    /// still survives gateway truncation with margin rather than sitting right at the cliff edge.
    /// </summary>
    public const int ManifestLineByteBudget = 16 * 1024;

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
