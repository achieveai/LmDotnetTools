namespace LmStreaming.Sample.Services;

/// <summary>
/// The agent factory refused to build a dedicated conversation's agent in a mode it may not have (see
/// <see cref="ConversationDedicatedSandbox.ModeRefusal"/>), for instance one that gained Mini App or browser debugging
/// after the conversation took it. Typed so a route answers it as
/// <see cref="ConversationDedicatedSandbox.ModeUnsupportedCode"/> rather than a bare 500. Derives from
/// <see cref="InvalidOperationException"/>, which the factory threw before, so existing callers still see it.
/// </summary>
public sealed class DedicatedSandboxModeUnsupportedException(string message) : InvalidOperationException(message);
