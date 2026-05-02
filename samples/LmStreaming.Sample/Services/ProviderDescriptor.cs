namespace LmStreaming.Sample.Services;

/// <summary>
/// Describes a single LLM provider exposed to the client provider-switcher dropdown.
/// </summary>
/// <param name="Id">Provider id used in WS query string and persisted in metadata. Lowercase.</param>
/// <param name="DisplayName">Human-friendly label rendered in the dropdown.</param>
/// <param name="Available">Whether this provider can be selected on a new conversation.</param>
public sealed record ProviderDescriptor(string Id, string DisplayName, bool Available);
