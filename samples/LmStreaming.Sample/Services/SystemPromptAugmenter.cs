namespace LmStreaming.Sample.Services;

/// <summary>
/// Augments a chat mode's system prompt with runtime context the model would otherwise lack.
/// </summary>
public static class SystemPromptAugmenter
{
    /// <summary>
    /// Property key in a thread's <c>ThreadMetadata.Properties</c> holding the caller-supplied system
    /// prompt appendix from <c>ProvisionConversationRequest.SystemPromptAppendix</c>. Written once at
    /// provision and read back on every agent (re)creation for that thread, so the instructions survive a
    /// process restart and a mode/provider switch exactly like the thread's workspace binding does.
    /// </summary>
    public const string AppendixPropertyKey = "sample.systemPromptAppendix";

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
