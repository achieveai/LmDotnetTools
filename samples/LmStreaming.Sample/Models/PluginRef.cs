namespace LmStreaming.Sample.Models;

/// <summary>
/// A single plugin identity as persisted in a workspace's explicit selection. Deliberately a
/// separate type from the Sandbox SDK's <c>SandboxPluginRef</c> (which is a validating, SDK-owned
/// type): this record is the app's own JSON-persistence shape, mapped to/from the SDK type at the
/// registry boundary rather than shared across layers.
/// </summary>
public sealed record PluginRef(string Marketplace, string Plugin);
