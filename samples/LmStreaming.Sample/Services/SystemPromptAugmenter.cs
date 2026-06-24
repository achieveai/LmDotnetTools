namespace LmStreaming.Sample.Services;

/// <summary>
/// Augments a chat mode's system prompt with runtime context the model would otherwise lack.
/// </summary>
public static class SystemPromptAugmenter
{
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
