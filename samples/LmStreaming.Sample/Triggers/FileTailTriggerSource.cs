using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;

namespace AchieveAi.LmDotnetTools.LmStreaming.Sample.Triggers;

/// <summary>
/// Notify/block source that fires when a new line appended to an allowed file matches an optional
/// regex. The path is canonicalized against host-supplied allowed roots at arm time; anything
/// outside them — including a symlink/junction inside a root whose real target resolves outside it
/// — is rejected as an arm-time <see cref="ArgumentException"/>, never a runtime failure. Not
/// restorable (a file offset can't be trusted across a process restart) — a restored <c>file_tail</c>
/// wait resolves <c>trigger_lost_on_restart</c> via the runtime's generic restore-capability check.
/// </summary>
public sealed class FileTailTriggerSource : ITriggerSource
{
    /// <summary>The registered kind token.</summary>
    public const string KindName = "file_tail";

    /// <summary>Human-readable args hint for the tool contract.</summary>
    public const string ArgsSchemaText =
        "{ path: \"<absolute path under an allowed root>\", pattern?: \"<regex; matches trigger a fire>\" }";

    /// <summary>Capabilities: block + notify, no restore.</summary>
    public static TriggerCapabilities Capabilities { get; } =
        new(SupportsBlock: true, SupportsNotify: true, SupportsRestore: false);

    // Per-line delivered-content cap (bytes, UTF-8). Bounds what a single fire can smuggle into a
    // delivered payload regardless of how long the underlying log line actually is.
    private const int MaxLineBytes = 4096;

    // Fires delivered per poll iteration. Bounds how much a single burst of appended lines can
    // inject in one go.
    private const int MaxLinesPerBatch = 20;

    // Bytes read from the file in a single poll iteration. Bounds memory: even a single pathological
    // line with no newline in sight can never make one iteration buffer more than this much data.
    private const int MaxReadBytesPerIteration = MaxLinesPerBatch * MaxLineBytes;

    // Regex match timeout — paired with RegexOptions.NonBacktracking below. NonBacktracking already
    // guarantees linear-time matching (immune to catastrophic backtracking), so the timeout is a
    // second, independent backstop rather than the primary defense.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(150);

    // Root-boundary comparison must match the filesystem's case sensitivity. On Windows/macOS a
    // case-variant path IS the same file, so compare case-insensitively; on a case-sensitive
    // filesystem (typical Linux) a case-variant sibling like "<root>-TAILS/x.log" is a GENUINELY
    // different directory from "<root>-tails" and must not be treated as in-root — using
    // OrdinalIgnoreCase there would be a confinement bypass.
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    // Consecutive failed poll ticks before the watcher says it is blind, and the gap between repeat
    // warnings after that. At the 150 ms debounce these are ~3 s and ~60 s: long enough that a
    // rotation race or a brief exclusive lock never logs, short enough that a real fault is visible
    // well inside a typical wait TTL rather than after it.
    private const int PollFaultWarnAfter = 20;
    private const int PollFaultRepeatEvery = 400;

    private readonly List<string> _allowedRoots;
    private readonly FileTailContentMode _contentMode;
    private readonly ILogger? _logger;

    /// <param name="allowedRoots">Host-fixed roots; every armed path must canonicalize inside one.</param>
    /// <param name="contentMode">
    /// How much of a matched line reaches the model. Defaults to
    /// <see cref="FileTailContentMode.Redacted"/>.
    /// </param>
    /// <param name="logger">
    /// Optional. Without it a poll loop that has gone structurally blind (ACL change, unmounted
    /// volume, deleted file) has no way to say so, and the wait's eventual TTL expiry reads as
    /// "nothing matched" rather than "nothing could be observed" (#161).
    /// </param>
    public FileTailTriggerSource(
        IReadOnlyList<string> allowedRoots,
        FileTailContentMode contentMode = FileTailContentMode.Redacted,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(allowedRoots);
        _contentMode = contentMode;
        _logger = logger;
        if (allowedRoots.Count == 0)
        {
            throw new ArgumentException("file_tail requires at least one allowed root.", nameof(allowedRoots));
        }

        // Canonicalize (and resolve any reparse point) up front so every arm-time comparison is
        // against a real, symlink-resolved root rather than a lexical one.
        _allowedRoots = [.. allowedRoots.Select(r => Path.TrimEndingDirectorySeparator(ResolveRealPath(Path.GetFullPath(r))))];
    }

    /// <inheritdoc />
    public ValueTask<IArmedTrigger> ArmAsync(
        TriggerArmRequest request,
        ITriggerEventSink eventSink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(eventSink);

        var (path, pattern) = ParseArgs(request.ArgsJson);
        var canonical = CanonicalizeAndValidate(path); // throws ArgumentException on any escape

        Regex? regex = null;
        if (!string.IsNullOrEmpty(pattern))
        {
            try
            {
                // NonBacktracking gives linear-time matching regardless of the (host-authored, but
                // still untrusted-shaped) pattern; the timeout below is a second, independent
                // backstop against runaway matches.
                regex = new Regex(pattern, RegexOptions.NonBacktracking, MatchTimeout);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"file_tail 'pattern' is not a valid regex: {ex.Message}", ex);
            }
            catch (NotSupportedException ex)
            {
                // NonBacktracking rejects a handful of backreference/lookaround constructs that
                // backtracking regex allows; surface that as a caller-correctable arg error too.
                throw new ArgumentException(
                    $"file_tail 'pattern' uses a construct unsupported by non-backtracking matching: {ex.Message}",
                    ex);
            }
        }

        // WaitId identifies the wait to the runtime — never the file path — so nothing that reads
        // this handle's WaitId (logs, diagnostics, a future ListWaits enrichment) can leak it.
        var handle = new FileTailArmedTrigger(
            request.WaitId, canonical, regex, eventSink, _contentMode, _logger);
        return ValueTask.FromResult<IArmedTrigger>(handle);
    }

    private string CanonicalizeAndValidate(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("file_tail 'path' must be an absolute path.");
        }

        // Path.GetFullPath collapses "." / ".." purely lexically (no filesystem access), so a
        // traversal escape like "<root>/../escape.log" already normalizes outside the root before
        // any symlink resolution is needed.
        var lexical = Path.GetFullPath(path);
        var real = ResolveRealPath(lexical);

        var inRoot = _allowedRoots.Any(root =>
            real.Equals(root, PathComparison)
            || real.StartsWith(root + Path.DirectorySeparatorChar, PathComparison));

        if (!inRoot)
        {
            throw new ArgumentException("file_tail 'path' is outside the allowed roots.");
        }

        return real;
    }

    /// <summary>
    /// Resolves <paramref name="full"/> to its real, symlink/junction-free location. Walks every
    /// path component from the drive/volume root downward (not just the file itself or its
    /// immediate parent) so a reparse point introduced at ANY ancestor level — not only the direct
    /// parent directory — is followed to its real target. A naive check of only the file's own
    /// link target and its immediate parent's link target misses this: a symlinked grandparent
    /// with a perfectly ordinary (non-linked) directory nested underneath it resolves to a real
    /// path outside the root even though neither the file nor its direct parent is itself a link.
    /// </summary>
    internal static string ResolveRealPath(string full)
    {
        var pathRoot = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(pathRoot))
        {
            return full;
        }

        var segments = full[pathRoot.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        // Seed with the root AS-IS (e.g. "C:\" on Windows, "/" on POSIX) rather than trimming its
        // trailing separator. Path.Combine treats a path ending in the bare volume-separator ':'
        // (e.g. "C:") as DRIVE-RELATIVE and will not insert a '\' before the next segment, so if the
        // seed ever lost its trailing separator, Path.Combine(seed, "logs") could produce the
        // drive-relative "C:logs" instead of the rooted "C:\logs" that the allowed-root confinement
        // check below depends on. Seeding with the untouched root guarantees every subsequent
        // Path.Combine call stays rooted on every platform.
        var resolved = pathRoot;

        foreach (var segment in segments)
        {
            resolved = Path.Combine(resolved, segment);
            resolved = ResolveOneLevel(resolved);
        }

        return resolved;
    }

    /// <summary>
    /// If <paramref name="candidate"/> is itself a symbolic link or junction, returns its fully
    /// resolved final target; otherwise returns it unchanged. Non-existent candidates (a file/dir
    /// that doesn't exist yet, e.g. the log file itself before it's first created) can't be reparse
    /// points, so they pass through unchanged — still checked against the allowed roots above.
    /// </summary>
    private static string ResolveOneLevel(string candidate)
    {
        try
        {
            if (Directory.Exists(candidate))
            {
                var info = new DirectoryInfo(candidate);
                if (info.LinkTarget != null)
                {
                    var target = info.ResolveLinkTarget(returnFinalTarget: true);
                    return target != null ? Path.GetFullPath(target.FullName) : candidate;
                }
            }
            else if (File.Exists(candidate))
            {
                var info = new FileInfo(candidate);
                if (info.LinkTarget != null)
                {
                    var target = info.ResolveLinkTarget(returnFinalTarget: true);
                    return target != null ? Path.GetFullPath(target.FullName) : candidate;
                }
            }
        }
        catch (IOException)
        {
            // Race (deleted/replaced mid-check) or an unresolvable reparse point — fall through to
            // the lexical candidate, which is still validated against the allowed roots by the
            // caller.
        }
        catch (UnauthorizedAccessException)
        {
            // No permission to inspect this component — treat as non-link; still root-checked.
        }

        return candidate;
    }

    private static (string Path, string? Pattern) ParseArgs(string argsJson)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"file_tail args is not valid JSON: {ex.Message}", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("file_tail args must be a JSON object.");
            }

            var path = root.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString()
                : null;
            var pattern = root.TryGetProperty("pattern", out var pat) && pat.ValueKind == JsonValueKind.String
                ? pat.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("file_tail requires a 'path'.");
            }

            return (path, pattern);
        }
    }

    /// <summary>
    /// Neutralizes control tokens so file content can't be confused with the
    /// <c>&lt;trigger&gt;...&lt;/trigger&gt;</c> envelope boundary the runtime wraps fired/notify
    /// payloads in, and caps the delivered length so one oversized line can't dominate a fire.
    /// </summary>
    /// <remarks>
    /// Named for what it does. It was called <c>Redact</c>, which implied a privacy guarantee it has
    /// never provided — escaping angle brackets does not remove a bearer token. Privacy redaction is
    /// <see cref="TriggerContentRedactor.Redact"/>, applied separately in
    /// <see cref="BuildPayload"/> (#161).
    /// </remarks>
    internal static string SanitizeEnvelopeContent(string line)
    {
        var capped = CapUtf8Bytes(line, MaxLineBytes);
        return capped.Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
    }

    /// <summary>
    /// Turns a matched line into the payload actually delivered, per the source's content mode.
    /// </summary>
    /// <remarks>
    /// Order matters and is not interchangeable: redact first, sanitize second. Sanitizing first
    /// would rewrite <c>&lt;</c>/<c>&gt;</c> inside a secret (an angle-bracketed address, a quoted
    /// connection string) and could break the very shape the redactor recognizes, forwarding the
    /// secret because it no longer looked like one.
    /// </remarks>
    internal static string BuildPayload(string line, FileTailContentMode mode) =>
        mode == FileTailContentMode.MetadataOnly
            ? $"[file_tail] a matching line ({Encoding.UTF8.GetByteCount(line)} bytes) was appended; "
                + "content withheld (metadata-only mode)."
            : SanitizeEnvelopeContent(TriggerContentRedactor.Redact(line));

    private static string CapUtf8Bytes(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return value;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        // A cut mid-multi-byte-sequence decodes with a replacement character rather than throwing.
        return Encoding.UTF8.GetString(bytes, 0, maxBytes) + "…[truncated]";
    }

    private static bool SafeMatch(Regex regex, string line)
    {
        try
        {
            return regex.IsMatch(line);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Per-arm handle. Polls the file for appended bytes on a debounce timer; disposal cancels the
    /// poll loop so no fire occurs afterward. Modeled on
    /// <see cref="AchieveAi.LmDotnetTools.LmMultiTurn.Triggers.Sources.TimerTriggerSource"/>'s
    /// yield-before-fire + dispose-cancels pattern.
    /// </summary>
    private sealed class FileTailArmedTrigger : IArmedTrigger
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _tailTask;
        private int _disposed;

        public FileTailArmedTrigger(
            string waitId,
            string path,
            Regex? regex,
            ITriggerEventSink sink,
            FileTailContentMode contentMode,
            ILogger? logger)
        {
            WaitId = waitId;

            // The tail baseline is captured HERE — synchronously, before ArmAsync returns — and not
            // inside RunAsync. RunAsync opens with `await Task.Yield()`, so anything read after that
            // yield is read on a thread-pool continuation that may not run until well after the
            // caller has resumed. A caller that arms a file_tail wait and then does the thing that
            // writes the log line (the normal ordering: arm, then act) would have those bytes
            // measured into the starting offset and silently classified as pre-existing history.
            // Precisely: the baseline settles at exactly the post-append length, so those particular
            // bytes are lost permanently — no timeout recovers them, because it is a lost signal
            // rather than a late one, and the truncation-reset in the poll loop cannot help either
            // (it only heals a baseline that is too LARGE, len < offset). The watcher itself stays
            // alive: a LATER append grows the file past that baseline and does fire normally. So the
            // damage is scoped but severe — it lands exactly on the one-shot "arm a wait, then do
            // the thing that logs" case, where the line written in the arm window is the only line
            // that will ever matter and the wait blocks indefinitely on a watcher that looks healthy.
            // Capturing before the yield makes ArmAsync's own return the "watcher is live" signal,
            // with no window behind it.
            //
            // The guard here is deliberately NOT identical to the in-loop one, on both axes:
            //   - It also catches UnauthorizedAccessException. The loop reaches its length read only
            //     after File.Exists has already succeeded once, whereas this runs cold on a
            //     caller-supplied path, so an ACL denial is reachable here and not there.
            //   - It resets to 0 where the loop PRESERVES the offset. That inversion is the point:
            //     the loop is protecting an offset it has already earned, so discarding it would
            //     re-deliver history. Here there is no earned offset yet, and 0 is the correct
            //     baseline for the case actually being guarded — a TOCTOU delete between File.Exists
            //     and the length read means the file the caller armed on is gone, so whatever
            //     appears at that path next should be read from its start, not skipped.
            // Either way this must not throw: an exception in the constructor would fault _tailTask
            // (or escape ArmAsync), and nobody observes it.
            long startOffset;
            try
            {
                startOffset = File.Exists(path) ? new FileInfo(path).Length : 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                startOffset = 0;
            }

            _tailTask = RunAsync(path, startOffset, regex, sink, contentMode, logger, _cts.Token);
        }

        public string WaitId { get; }

        private static async Task RunAsync(
            string path,
            long startOffset,
            Regex? regex,
            ITriggerEventSink sink,
            FileTailContentMode contentMode,
            ILogger? logger,
            CancellationToken ct)
        {
            // Yield first so the fire is always asynchronous — never synchronous within ArmAsync.
            // This is purely about when the fire is delivered; the baseline it is measured against
            // was already fixed by the constructor, above.
            await Task.Yield();

            long offset = startOffset;

            // Two streaks, not one: "the file is not there" and "the file is there but unreadable"
            // are different faults with different fixes, and collapsing them would report whichever
            // happened last. Both are structural blindness -- the loop keeps polling, observes
            // nothing, and without these the wait's eventual TTL expiry reads as "nothing matched".
            var missingStreak = new PollFaultStreak(PollFaultWarnAfter, PollFaultRepeatEvery);
            var ioStreak = new PollFaultStreak(PollFaultWarnAfter, PollFaultRepeatEvery);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(DebounceWindow, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (!File.Exists(path))
                {
                    if (missingStreak.RecordFailure())
                    {
                        logger?.LogWarning(
                            "file_tail watcher has seen no file at its armed path for {Ticks} consecutive polls; "
                                + "it can never fire while this holds. The path was validated at arm time, so the "
                                + "file was deleted, rotated away, or its volume unmounted.",
                            missingStreak.Consecutive);
                    }

                    continue;
                }

                if (missingStreak.RecordSuccess())
                {
                    logger?.LogInformation(
                        "file_tail watcher's armed path is readable again; resuming normal polling.");
                }

                long len;
                try
                {
                    len = new FileInfo(path).Length;
                }
                catch (IOException ex)
                {
                    // Transient (rotation/replace race) -- retry next tick. Counted, because a
                    // persistent one is otherwise indistinguishable from a healthy quiet file.
                    if (ioStreak.RecordFailure())
                    {
                        logger?.LogWarning(
                            ex,
                            "file_tail watcher has failed to read its armed file's length for {Ticks} consecutive "
                                + "polls; it can never fire while this holds.",
                            ioStreak.Consecutive);
                    }

                    continue;
                }

                if (len < offset)
                {
                    offset = 0; // file was truncated/rotated — restart from the beginning.
                }

                if (len <= offset)
                {
                    continue;
                }

                try
                {
                    offset = await PollOnceAsync(path, offset, len, regex, sink, contentMode, ct);
                    if (ioStreak.RecordSuccess())
                    {
                        logger?.LogInformation(
                            "file_tail watcher read its armed file again; resuming normal polling.");
                    }
                }
                catch (OperationCanceledException)
                {
                    return; // disposed/cancelled mid-read — exit cleanly, don't fault the task.
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Counted for the same reason as the length read above: an ACL change or an
                    // unmounted volume throws here forever, and continuing silently is exactly what
                    // makes the trigger inert without ever saying so.
                    if (ioStreak.RecordFailure())
                    {
                        logger?.LogWarning(
                            ex,
                            "file_tail watcher has failed to read its armed file for {Ticks} consecutive polls; "
                                + "it can never fire while this holds. A transient rotation race clears on its "
                                + "own; an ACL change or an unmounted volume does not.",
                            ioStreak.Consecutive);
                    }

                    // A rotation (rename+recreate) or a brief exclusive lock can race File.Exists →
                    // FileStream.Open and throw even though the file was just seen to exist. Tolerate
                    // it the same way the FileInfo.Length read above does — skip this tick and retry
                    // from the unchanged offset. A monitoring trigger must never die silently on a
                    // transient IO error.
                    continue;
                }
            }
        }

        /// <summary>
        /// Reads at most <see cref="MaxReadBytesPerIteration"/> new bytes, splits complete lines
        /// out of that bounded chunk, fires up to <see cref="MaxLinesPerBatch"/> matches, and
        /// returns the new offset (only ever advanced past bytes actually consumed). A single line
        /// that grows past the per-iteration cap without a newline is force-consumed in
        /// <see cref="MaxReadBytesPerIteration"/>-sized slices rather than left to buffer without
        /// bound — each such slice is itself capped again by <see cref="BuildPayload"/> before delivery.
        /// </summary>
        private static async Task<long> PollOnceAsync(
            string path,
            long offset,
            long len,
            Regex? regex,
            ITriggerEventSink sink,
            FileTailContentMode contentMode,
            CancellationToken ct)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(offset, SeekOrigin.Begin);

            var toRead = (int)Math.Min(len - offset, MaxReadBytesPerIteration);
            var buffer = new byte[toRead];
            var read = await fs.ReadAsync(buffer, ct);
            if (read <= 0)
            {
                return offset;
            }

            var lastNewline = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
            if (lastNewline < 0)
            {
                if (toRead >= MaxReadBytesPerIteration)
                {
                    // A pending "line" has grown at least this large with no terminator in sight:
                    // force-consume what we've buffered as one (capped-on-delivery) unit so memory
                    // never grows past MaxReadBytesPerIteration regardless of how long the real
                    // line eventually turns out to be.
                    var forced = Encoding.UTF8.GetString(buffer, 0, read);
                    await FireIfMatchAsync(forced, regex, sink, contentMode, ct);
                    return offset + read;
                }

                return offset; // still-partial line — wait for more data or a newline.
            }

            // Walk newline positions in the raw BYTES (not the decoded string) so the returned
            // offset is byte-accurate even for multi-byte UTF-8 content, and advance it only past
            // lines actually processed: if the batch cap is hit mid-burst, the remainder is left in
            // the file to be re-read next poll rather than silently skipped.
            var fired = 0;
            var lineStart = 0;
            var consumed = 0;
            for (var i = 0; i <= lastNewline; i++)
            {
                if (buffer[i] != (byte)'\n')
                {
                    continue;
                }

                var line = Encoding.UTF8.GetString(buffer, lineStart, i - lineStart).TrimEnd('\r');
                lineStart = i + 1;
                consumed = i + 1; // only advance past lines we've now handled

                if (line.Length == 0)
                {
                    continue;
                }

                if (await FireIfMatchAsync(line, regex, sink, contentMode, ct))
                {
                    fired++;
                    if (fired >= MaxLinesPerBatch)
                    {
                        break; // leave the rest of this burst for the next poll — don't drop it.
                    }
                }
            }

            return offset + consumed;
        }

        private static async Task<bool> FireIfMatchAsync(
            string line,
            Regex? regex,
            ITriggerEventSink sink,
            FileTailContentMode contentMode,
            CancellationToken ct)
        {
            if (regex != null && !SafeMatch(regex, line))
            {
                return false;
            }

            await sink.FireAsync(new TriggerFireEvent(BuildPayload(line, contentMode)), ct);
            return true;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _cts.CancelAsync();

            // Do NOT await _tailTask here (same reasoning as TimerTriggerSource): disposal is
            // typically invoked from within the runtime's own fire-handling callback, and awaiting
            // our own still-running task would deadlock. Dispose the CTS once it settles instead.
            TriggerDisposal.DisposeAfter(_tailTask, _cts);
        }
    }
}
