using System.Text.Json;

namespace CodeReviewDaemon.Sample.Eval;

/// <summary>
/// Reads an artifact payload without letting one bad row take down a whole sweep.
/// <para>
/// The eval readers walk history the daemon wrote under earlier schema versions, so a payload that
/// does not fit today's shape is expected input rather than an invariant violation. Every caller
/// wants the same thing — the value, or null and something to log — and each was implementing it
/// with its own <c>try</c>/<c>catch</c>. Two copies of a catch clause is how one of them ends up
/// catching less than the other, which is exactly what happened: both caught
/// <see cref="JsonException"/> only, and <see cref="NotSupportedException"/> — which
/// <see cref="JsonSerializer"/> raises for a shape it has no converter for, and which is <b>not</b>
/// a <c>JsonException</c> — would have escaped and cost the sweep its whole window rather than one
/// row.
/// </para>
/// </summary>
internal static class EvalArtifactJson
{
    /// <summary>
    /// Case-insensitive, matching what the daemon's own writers produce and what the earlier
    /// readers used. Shared so the two cannot drift into disagreeing about the same stored bytes.
    /// </summary>
    internal static readonly JsonSerializerOptions Options =
        new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Deserialises <paramref name="payload"/>, or returns null with <paramref name="failure"/> set.
    /// </summary>
    /// <param name="payload">The stored artifact payload.</param>
    /// <param name="failure">
    /// What went wrong, or null when nothing did. Returned rather than logged here so the caller
    /// names the row in its own terms — an artifact id and what its loss costs — instead of this
    /// helper guessing at a message for callers it does not know.
    /// </param>
    /// <returns>
    /// The value, or null. A payload of literal <c>null</c> also yields null with no
    /// <paramref name="failure"/>: it deserialises successfully to nothing, and the caller's
    /// "no usable payload" branch is the same one either way.
    /// </returns>
    public static T? TryRead<T>(string payload, out Exception? failure)
        where T : class
    {
        failure = null;

        try
        {
            return JsonSerializer.Deserialize<T>(payload, Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            failure = ex;
            return null;
        }
    }
}
