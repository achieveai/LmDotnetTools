using System.Text.Json;

namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// One MCP server a plugin the sandbox admitted declares, exactly as its author wrote it (gateway issue #224,
/// <see cref="SandboxClient.GetMcpServersAsync"/>). The gateway does not proxy these servers: the client connects to
/// them itself. <c>${VAR}</c> references are not expanded.
/// </summary>
/// <remarks>
/// SECRET: <see cref="Url"/>, header values and <see cref="Extra"/> can carry credentials. <see cref="ToString"/>
/// names the server only, so logging a row never prints them.
/// </remarks>
public sealed class SandboxMcpServer
{
    /// <summary>The server's key in the plugin's <c>mcpServers</c> map.</summary>
    public required string Name { get; init; }

    /// <summary>The plugin that declares it.</summary>
    public required string Plugin { get; init; }

    /// <summary>The marketplace alias the plugin comes from.</summary>
    public required string Marketplace { get; init; }

    /// <summary>The transport type, lowercased: <c>http</c>, <c>sse</c> or <c>stdio</c> (no type is <c>stdio</c>).</summary>
    public required string Type { get; init; }

    /// <summary>True for <c>http</c>/<c>sse</c> with a non-empty <see cref="Url"/>.</summary>
    public required bool Supported { get; init; }

    /// <summary>Why the gateway marks it unsupported; <see langword="null"/> when <see cref="Supported"/>.</summary>
    public string? Reason { get; init; }

    /// <summary>The endpoint as authored. SECRET: may carry a credential or a <c>${VAR}</c>.</summary>
    public string? Url { get; init; }

    /// <summary>Every header as authored, sorted by name.</summary>
    public IReadOnlyList<SandboxMcpHeader> Headers { get; init; } = [];

    /// <summary>Every other key of the entry (for example <c>oauth</c>, <c>headersHelper</c>, <c>timeout</c>).</summary>
    public IReadOnlyDictionary<string, JsonElement> Extra { get; init; } = new Dictionary<string, JsonElement>();

    /// <summary>The server's identity only: never its url, headers or extra.</summary>
    public override string ToString() => $"{Marketplace}/{Plugin}/{Name} ({Type})";
}

/// <summary>One header of a <see cref="SandboxMcpServer"/>, as authored.</summary>
public sealed class SandboxMcpHeader
{
    /// <summary>The header name.</summary>
    public required string Name { get; init; }

    /// <summary>The value as authored. SECRET: may be a credential or contain <c>${VAR}</c> references.</summary>
    public required string Value { get; init; }

    /// <summary><c>VAR</c> when <see cref="Value"/> is exactly <c>${VAR}</c>; otherwise <see langword="null"/>.</summary>
    public string? Placeholder { get; init; }

    /// <summary>The header name only, never its value.</summary>
    public override string ToString() => Name;
}
