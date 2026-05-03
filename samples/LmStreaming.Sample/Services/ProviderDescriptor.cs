namespace LmStreaming.Sample.Services;

/// <summary>
/// Describes a single LLM provider exposed to the client provider-switcher dropdown.
/// </summary>
/// <param name="Id">Provider id used in WS query string and persisted in metadata. Lowercase.</param>
/// <param name="DisplayName">Human-friendly label rendered in the dropdown.</param>
/// <param name="Available">Whether this provider can be selected on a new conversation.</param>
/// <param name="KnownLimitation">
/// Optional human-facing note rendered next to the provider in the UI when the provider is
/// available but has a documented limitation that prevents end-to-end use today (typically a
/// link to a follow-up issue). <c>null</c> for providers without such a caveat.
/// </param>
public sealed record ProviderDescriptor(
    string Id,
    string DisplayName,
    bool Available,
    string? KnownLimitation = null);
