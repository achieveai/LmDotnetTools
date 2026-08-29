using AchieveAi.LmDotnetTools.AnthropicProvider.Models;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests;

/// <summary>
///     Regression coverage for mode-driven selection of server-side built-in tools (e.g. Anthropic
///     <c>web_search</c>). Built-ins must follow the active mode's <c>enabledTools</c> exactly like
///     function tools — we never inject a built-in the mode didn't ask for. This guards the removal of
///     the old <c>isWorkspaceMode ? allBuiltInTools : Filter(...)</c> override in <c>Program.cs</c>,
///     which force-kept <c>web_search</c> even when a mode's <c>enabledTools</c> named no web tool
///     (Workspace Agent's list carries only the TaskManager family) — surfacing as a
///     <c>web_search</c> tool prepended ahead of every real tool in the request.
/// </summary>
public sealed class ModeToolFilterTests
{
    private static List<object> WebSearchBuiltIns() => [new AnthropicWebSearchTool()];

    [Fact]
    public void NullEnabledTools_keeps_all_builtins()
    {
        // null == "all tools enabled" (system/default modes).
        var result = ModeToolFilter.FilterBuiltInTools(WebSearchBuiltIns(), enabledTools: null);

        result.Should().NotBeNull();
        result!.OfType<AnthropicWebSearchTool>().Should().ContainSingle();
    }

    [Fact]
    public void EmptyEnabledTools_drops_all_builtins()
    {
        // enabledTools: [] means "no tools at all" (the medical-knowledge shape; Workspace Agent
        // has since moved to a task-family list). It MUST also yield no server-side built-ins —
        // this is the exact behavior the removed override used to violate (it prepended web_search
        // regardless).
        var result = ModeToolFilter.FilterBuiltInTools(WebSearchBuiltIns(), enabledTools: []);

        result.Should().BeNull();
    }

    [Fact]
    public void EnabledTools_containing_web_search_keeps_it()
    {
        var result = ModeToolFilter.FilterBuiltInTools(WebSearchBuiltIns(), enabledTools: ["web_search"]);

        result.Should().NotBeNull();
        result!.OfType<AnthropicWebSearchTool>().Should().ContainSingle();
    }

    [Fact]
    public void EnabledTools_without_web_search_drops_it()
    {
        // A mode that enables other tools but not web_search (e.g. Math Helper -> ["calculate"]).
        var result = ModeToolFilter.FilterBuiltInTools(WebSearchBuiltIns(), enabledTools: ["calculate"]);

        result.Should().BeNull();
    }

    [Fact]
    public void NoProviderBuiltins_stays_null()
    {
        // Providers with no server-side built-ins (GetBuiltInToolsForProvider returns null) never get
        // any, regardless of the mode.
        ModeToolFilter.FilterBuiltInTools(null, enabledTools: null).Should().BeNull();
        ModeToolFilter.FilterBuiltInTools(null, enabledTools: ["web_search"]).Should().BeNull();
    }

    [Fact]
    public void WorkspaceAgentMode_declares_web_search_via_dedicated_field_not_enabledTools()
    {
        var workspace = SystemChatModes.GetById(SystemChatModes.WorkspaceAgentModeId);

        workspace.Should().NotBeNull();
        // Function tools are curated via the sandbox MCP gateway, so enabledTools carries only the
        // shared todo-board family (no sample demo tools, and no web names either)...
        workspace!.EnabledTools.Should().NotContain(["get_weather", "calculate", "web_search", "WebSearch"]);
        // ...while web_search is declared on the dedicated built-in field, decoupled from enabledTools.
        workspace.EnabledBuiltInTools.Should().Contain("web_search");

        // Resolution mirrors Program.cs: EnabledBuiltInTools governs (falling back to EnabledTools only
        // when null). So web_search survives even though enabledTools does not name it.
        var allowList = workspace.EnabledBuiltInTools ?? workspace.EnabledTools;
        var result = ModeToolFilter.FilterBuiltInTools(WebSearchBuiltIns(), allowList);

        result.Should().NotBeNull();
        result!.OfType<AnthropicWebSearchTool>().Should().ContainSingle();
    }
}
