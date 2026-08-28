using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace LmStreaming.Sample.Services;

/// <summary>
/// Augments a chat mode's system prompt with runtime context the model would otherwise lack.
/// </summary>
public static class SystemPromptAugmenter
{
    /// <summary>
    /// Property key in a thread's <c>ThreadMetadata.Properties</c> holding the caller-supplied system
    /// prompt appendix from <c>ProvisionConversationRequest.SystemPromptAppendix</c>. Written once at
    /// provision and read back on every agent (re)creation for that thread by
    /// <see cref="ReadAppendixAsync"/>, so the instructions survive a process restart and a mode/provider
    /// switch exactly like the thread's workspace binding does.
    /// <para>
    /// That sentence was false for the whole life of this field before the reader existed: the value was
    /// stored here and read by nothing, so a headless caller's methodology, output contract and sub-agent
    /// dispatch instructions never reached the model, and every S2S review ran under the bare mode prompt.
    /// The doc-comment asserting the mechanism lived in the same file as the key nothing read, which is
    /// why nobody checked. If you are about to add another provisioned property, add its reader in the
    /// same change and name the reader here — prose is not a wire.
    /// </para>
    /// </summary>
    public const string AppendixPropertyKey = "sample.systemPromptAppendix";

    /// <summary>
    /// Reads the caller instructions recorded for a thread at provision, or <c>null</c> when there are
    /// none (every conversation the UI creates, and every conversation provisioned before this field was
    /// honoured).
    /// <para>
    /// <b>Absence</b> never throws: a missing thread, a missing property or a non-string value all mean
    /// "no caller instructions", which is the behavior every existing conversation depends on; an agent
    /// build is not worth failing over an addendum that was never recorded.
    /// </para>
    /// <para>
    /// <b>Failure</b> does throw, and deliberately so — this is not a catch-all. A null
    /// <paramref name="store"/> is a wiring bug and throws <see cref="ArgumentNullException"/>, and
    /// whatever the store's own <c>LoadMetadataAsync</c> raises propagates unchanged:
    /// <c>FileConversationStore</c> swallows only <c>JsonException</c> around its read, so IO and access
    /// failures — and any backing-store failure from another implementation — reach the caller. A
    /// degraded store surfaces as a failed agent build rather than as a silently bare prompt, which is
    /// the distinction #528 existed to restore.
    /// </para>
    /// <para>
    /// The value is extracted with <see cref="ThreadPropertyValue.AsString"/> and NOT with a bare
    /// <c>raw is string</c> test. The production store round-trips the property bag through JSON, so a
    /// string written at provision is read back as a <c>JsonElement</c>; a plain type test therefore
    /// returns null for every value that has actually been persisted, while every in-memory-store unit
    /// test still passes. See <see cref="ThreadPropertyValue"/>.
    /// </para>
    /// </summary>
    public static async Task<string?> ReadAppendixAsync(
        IConversationStore store,
        string threadId,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(store);

        if (string.IsNullOrWhiteSpace(threadId))
        {
            return null;
        }

        var metadata = await store.LoadMetadataAsync(threadId, ct).ConfigureAwait(false);
        if (metadata?.Properties is not { } properties || !properties.TryGetValue(AppendixPropertyKey, out var raw))
        {
            return null;
        }

        return ThreadPropertyValue.AsString(raw);
    }

    /// <summary>
    /// The system prompt a thread's agent actually runs with: the prompt built so far, plus whatever
    /// instructions the caller recorded at provision, appended last.
    /// <para>
    /// This exists as one named call rather than a read-then-append pair at the call site on purpose. The
    /// agent factory in <c>Program.cs</c> is a single long inline lambda, so whatever is written there is
    /// hard to reach from a unit test — dropping the appendix from it left the entire unit suite green,
    /// which is precisely how this field shipped inert. Collapsing read + append into one tested unit
    /// shrinks the uncoverable surface to a single call; the call itself is pinned end-to-end by
    /// <c>SystemPromptCompositionTests</c>, which reads the prompt back off the outbound provider request.
    /// </para>
    /// </summary>
    public static async Task<string> ComposeAsync(
        IConversationStore store,
        string threadId,
        string? systemPrompt,
        ILogger? logger = null,
        CancellationToken ct = default
    )
    {
        var appendix = await ReadAppendixAsync(store, threadId, ct).ConfigureAwait(false);
        var composed = AppendCallerInstructions(systemPrompt, appendix);

        // Logged HERE, at composition, and deliberately NOT at provision. Provision already records what
        // was SENT, and "we sent it" versus "it was applied" are the two claims that were conflated for the
        // entire life of this bug — a fleet where the appendix is silently dropped again would look
        // identical in the provision log. AppendixChars is the discriminator: 0 means this agent is running
        // on the mode prompt alone.
        logger?.LogInformation(
            "Thread {ThreadId}: composed system prompt {ComposedChars} chars "
                + "(mode+workspace {BaseChars}, caller appendix {AppendixChars}).",
            threadId,
            composed.Length,
            systemPrompt?.Length ?? 0,
            appendix?.Length ?? 0
        );

        return composed;
    }

    /// <summary>
    /// Appends a headless caller's own instructions to the mode's system prompt. <b>Additive by design:</b>
    /// the mode prompt, the workspace-path suffix and the discovered CLAUDE.md/AGENTS.md block all still
    /// apply — the caller is adding a task on top of the workspace agent, not replacing it — so this goes
    /// LAST, where recency gives it the strongest pull on the model. Returns the original prompt unchanged
    /// when there is nothing to append.
    /// </summary>
    /// <param name="systemPrompt">The prompt built so far (may be null/empty).</param>
    /// <param name="appendix">The caller's extra instructions (null/whitespace ⇒ no-op).</param>
    public static string AppendCallerInstructions(string? systemPrompt, string? appendix)
    {
        if (string.IsNullOrWhiteSpace(appendix))
        {
            return systemPrompt ?? string.Empty;
        }

        return string.IsNullOrEmpty(systemPrompt) ? appendix : $"{systemPrompt}\n\n{appendix}";
    }

    /// <summary>
    /// Prepends the current UTC date so the model is anchored to "today". Without it, a model can
    /// fall back to a training-era date, treat correctly-dated tool results (e.g. web_search hits
    /// dated in the future relative to its training) as unreliable, and loop re-searching and
    /// re-verifying instead of answering. Mirrors what production agent harnesses inject.
    /// </summary>
    /// <param name="systemPrompt">The mode's existing system prompt (may be null/empty).</param>
    /// <param name="now">The current instant; the UTC calendar date is used.</param>
    public static string PrependCurrentDate(string? systemPrompt, DateTimeOffset now)
    {
        var dateLine = $"The current date is {now.UtcDateTime:yyyy-MM-dd} (UTC).";
        return string.IsNullOrEmpty(systemPrompt) ? dateLine : $"{dateLine}\n\n{systemPrompt}";
    }
}
