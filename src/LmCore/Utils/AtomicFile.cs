using System.Text;
using System.Text.Json;

namespace AchieveAi.LmDotnetTools.LmCore.Utils;

/// <summary>
/// One implementation of the write-to-a-staging-file-then-rename-over-the-target pattern, so a concurrent
/// reader never observes a partially written file.
/// <para>
/// This lives in <c>LmCore</c> because that is the only project every caller already references:
/// <c>LmAgentInfra</c>, <c>LmConfig</c>, <c>Misc</c> and <c>LmMultiTurn</c> each reference <c>LmCore</c>
/// directly, and <c>LmCore</c> itself has no <c>ProjectReference</c> at all. It was previously
/// <c>LmAgentInfra.Auth.AtomicJsonFile</c>, which no other project could reach — the dependency runs
/// <c>LmAgentInfra</c> -&gt; <c>LmMultiTurn</c>, not the reverse — so four call sites each grew their own
/// copy of the tricky part and three of them got it wrong. Moving the type down is what makes one helper
/// reachable without adding a reference edge.
/// </para>
/// <para>
/// Callers own their own locking and their own reads. A per-instance lock is not a substitute for anything
/// here: it serializes writers inside one object and nothing across two instances over one directory, let
/// alone across processes.
/// </para>
/// </summary>
public static class AtomicFile
{
    /// <summary>
    /// Attempts at the final rename, and the backoff step multiplied by the attempt number: ~1.1 s in
    /// total across the budget (the delay fires on attempts 1-9; the tenth rethrows first).
    /// <para>
    /// Deliberately the same shape and budget as <c>DetachedStoreTeardown.Purge</c>, which already absorbs
    /// exactly this class of briefly-held handle during teardown. That the teardown path retried and every
    /// write path did not was the asymmetry worth closing, not a difference in the hazard.
    /// </para>
    /// </summary>
    private const int MoveAttempts = 10;

    /// <inheritdoc cref="MoveAttempts" />
    private const int MoveRetryDelayMs = 25;

    /// <summary>
    /// Stages a write through a unique temp file next to <paramref name="filePath"/> and renames it over
    /// the target, creating the parent directory if needed.
    /// <para>
    /// <paramref name="writeStaged"/> receives the staging path and must leave the fully written content
    /// there with every handle it opened closed — the rename cannot replace a file the callback still
    /// holds. Verification of the staged content belongs inside the callback: throwing from it aborts the
    /// write with the staging file cleaned up and nothing moved over the target.
    /// </para>
    /// <para>
    /// The callback shape exists because the four call sites genuinely differ in how they produce bytes —
    /// a raw secret, a serialized string with and without a BOM, and a buffered <see cref="FileStream"/>
    /// with a read-back check. Only the staging, renaming and cleanup are common, and only those are
    /// shared here; forcing one encoding on all of them would change bytes on disk for no benefit.
    /// </para>
    /// </summary>
    public static async Task WriteAsync(
        string filePath,
        Func<string, CancellationToken, Task> writeStaged,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        ArgumentNullException.ThrowIfNull(writeStaged);

        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            _ = Directory.CreateDirectory(dir);
        }

        // A fresh suffix per write, rather than a name derived from the target. Under a temp name shared by
        // every writer to a file, those writers contend for the temp path itself and one of them fails
        // before it ever reaches the rename. A unique name removes that contention by construction.
        var tempFilePath = $"{filePath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await writeStaged(tempFilePath, ct).ConfigureAwait(false);
            await MoveWithRetryAsync(tempFilePath, filePath, ct).ConfigureAwait(false);
        }
        catch
        {
            // A write that did not land must not leave its staging file behind. Unique names are what make
            // this necessary as well as safe: no later writer reuses or overwrites this path, so without an
            // explicit delete a failed write would litter the directory permanently. Secret-bearing callers
            // additionally depend on it to not leave a partial secret on disk.
            TryDeleteTempFile(tempFilePath);
            throw;
        }
    }

    /// <summary>
    /// Serializes <paramref name="value"/> to <paramref name="filePath"/> atomically as UTF-8 JSON.
    /// <para>
    /// Uses <see cref="Encoding.UTF8"/>, which emits a byte-order mark, because that is what every caller
    /// of this overload already wrote before it was shared and changing it would rewrite their files' bytes
    /// for no gain. A caller that needs BOM-free output uses <see cref="WriteAsync"/> directly.
    /// </para>
    /// </summary>
    public static Task WriteJsonAsync<T>(
        string filePath,
        T value,
        JsonSerializerOptions options,
        CancellationToken ct = default
    )
    {
        // Serialize before staging so a serialization failure never creates a temp file to clean up.
        var json = JsonSerializer.Serialize(value, options);

        return WriteAsync(
            filePath,
            (tempFilePath, token) => File.WriteAllTextAsync(tempFilePath, json, Encoding.UTF8, token),
            ct
        );
    }

    /// <summary>
    /// Renames the staging file over <paramref name="filePath"/>, retrying a bounded number of times while
    /// the destination is held.
    /// <para>
    /// The rename is atomic but not unconditional on Windows: <c>MoveFile</c> with <c>REPLACE_EXISTING</c>
    /// needs delete access on the destination, so it fails outright while ANY handle is open on it.
    /// <see cref="FileShare.Read"/> — precisely what a concurrent <c>File.ReadAllTextAsync</c> of the same
    /// file takes — is enough to produce an <see cref="UnauthorizedAccessException"/>, and so is a virus
    /// scanner or the search indexer touching a file that was just written. Every one of those holders is
    /// transient, so waiting for the condition is worth more than failing the caller.
    /// </para>
    /// <para>
    /// The final failure is rethrown as-is rather than wrapped: callers see the same exception types these
    /// paths have always thrown, and a genuine permanent holder — an ACL problem, a leaked writer — still
    /// surfaces with its own cause intact instead of behind a retry-flavoured message.
    /// </para>
    /// </summary>
    private static async Task MoveWithRetryAsync(string tempFilePath, string filePath, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tempFilePath, filePath, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= MoveAttempts)
                {
                    throw;
                }

                await Task.Delay(MoveRetryDelayMs * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Best-effort removal of a staging file whose write or rename failed. The swallow is safe here for a
    /// reason the write itself does not share: this runs while the caller's real exception is already
    /// unwinding, and replacing that failure with a cleanup failure would destroy the cause. The unique
    /// name makes any survivor inert — nothing else ever looks at that path.
    /// </summary>
    private static void TryDeleteTempFile(string tempFilePath)
    {
        try
        {
            File.Delete(tempFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately ignored; see the summary above.
        }
    }
}
