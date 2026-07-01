namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// The hidden marker the comment publishers embed in every posted review comment so a later poll can
/// recognize a comment a crashed attempt already posted (the <see cref="IReviewCommentPublisher"/>
/// backstop scan — plan §11). It is an HTML comment, invisible in rendered GitHub/ADO markdown, carrying
/// the exact versioned idempotency key. GitHub and ADO share the format so the scan/post logic is
/// identical across providers.
/// </summary>
internal static class IdempotencyMarker
{
    private const string Prefix = "<!-- idempotency-key:";
    private const string Suffix = " -->";

    /// <summary>Appends the hidden marker for <paramref name="idempotencyKey"/> to a comment body.</summary>
    public static string Embed(string body, string idempotencyKey) =>
        $"{body}\n\n{Prefix}{idempotencyKey}{Suffix}";

    /// <summary>True when <paramref name="commentBody"/> carries the marker for <paramref name="idempotencyKey"/>.</summary>
    public static bool Matches(string? commentBody, string idempotencyKey) =>
        commentBody is not null && commentBody.Contains($"{Prefix}{idempotencyKey}{Suffix}", StringComparison.Ordinal);
}
