using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.Misc.Configuration;
using AchieveAi.LmDotnetTools.Misc.Web.Jina;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Tests for <see cref="WebToolRegistrationPolicy" /> — the narrow sample helper that registers the
/// Jina <c>WebFetch</c>/<c>WebSearch</c> fallback function tools into a per-conversation registry.
/// The policy keys off the provider allow-list and the mode's <c>EnabledTools</c> (the function-tool
/// list). The server-side built-in allow-list (<c>EnabledBuiltInTools</c>) is handled elsewhere
/// (<see cref="ModeToolFilter" />) and is intentionally NOT an input to this policy.
/// </summary>
public class WebToolRegistrationPolicyTests
{
    // 10+ characters so it mirrors a real key shape; never used to make a network request because
    // the policy only constructs and registers the tools, it never invokes them.
    private const string ApiKey = "k1234567890";

    // ---- Provider matrix: allow-listed providers (with key) receive both tools ----

    [Fact]
    public void Apply_RegistersBothTools_ForOpenAi_WhenKeyPresent()
    {
        var registry = new FunctionRegistry();
        var (provider, options) = Backend(ApiKey);

        _ = WebToolRegistrationPolicy.Apply(
            registry,
            "openai",
            enabledTools: null,
            provider,
            options,
            NullLoggerFactory.Instance
        );

        RegisteredNames(registry).Should().Contain("WebFetch").And.Contain("WebSearch");
    }

    [Theory]
    [InlineData("claude-sonnet-5")]
    [InlineData("claude-haiku-4.5")]
    [InlineData("gpt-5.5")]
    [InlineData("gpt-5.4-mini")]
    public void Apply_RegistersBothTools_ForDiscoveredCopilotModel_WhenKeyPresent(string providerId)
    {
        var registry = new FunctionRegistry();
        var (provider, options) = Backend(ApiKey);

        _ = WebToolRegistrationPolicy.Apply(
            registry,
            providerId,
            enabledTools: null,
            provider,
            options,
            NullLoggerFactory.Instance,
            isCopilotBackedModel: true
        );

        RegisteredNames(registry).Should().Contain("WebFetch").And.Contain("WebSearch");
    }

    // ---- Provider matrix: providers with native web (or mocks/test/unknown) receive nothing ----

    [Theory]
    [InlineData("anthropic")]
    [InlineData("test-anthropic")]
    [InlineData("claude")]
    [InlineData("claude-mock")]
    [InlineData("codex")]
    [InlineData("codex-mock")]
    [InlineData("test")]
    [InlineData("copilot")]
    [InlineData("copilot-mock")]
    [InlineData("unknown")]
    public void Apply_RegistersNothing_ForNonAllowListedProvider(string providerId)
    {
        var registry = new FunctionRegistry();
        var (provider, options) = Backend(ApiKey);

        var statuses = WebToolRegistrationPolicy.Apply(
            registry,
            providerId,
            enabledTools: null,
            provider,
            options,
            NullLoggerFactory.Instance
        );

        RegisteredNames(registry).Should().NotContain("WebFetch").And.NotContain("WebSearch");
        statuses.Should().BeEmpty();
    }

    // ---- Explicit Copilot regression (excluded from the allow-list by design) ----

    [Theory]
    [InlineData("copilot")]
    [InlineData("copilot-mock")]
    public void Apply_NeverRegistersJinaTools_ForCopilot(string providerId)
    {
        // Plain "copilot" returns early on the CopilotAgentLoop (CLI) path before the per-conversation
        // registry is built, so it never reaches this seam; "copilot-mock" is a deterministic mock.
        // Both are intentionally excluded from the Jina fallback allow-list.
        var registry = new FunctionRegistry();
        var (provider, options) = Backend(ApiKey);

        _ = WebToolRegistrationPolicy.Apply(
            registry,
            providerId,
            enabledTools: null,
            provider,
            options,
            NullLoggerFactory.Instance
        );

        RegisteredNames(registry).Should().NotContain("WebFetch").And.NotContain("WebSearch");
    }

    // ---- Casing: provider id is normalized (trim + lowercase) ----

    [Theory]
    [InlineData("OpenAI")]
    [InlineData(" openai ")]
    public void Apply_NormalizesProviderId_AndRegisters(string providerId)
    {
        var registry = new FunctionRegistry();
        var (provider, options) = Backend(ApiKey);

        _ = WebToolRegistrationPolicy.Apply(
            registry,
            providerId,
            enabledTools: null,
            provider,
            options,
            NullLoggerFactory.Instance
        );

        RegisteredNames(registry).Should().Contain("WebFetch").And.Contain("WebSearch");
    }

    // ---- EnabledTools gate (the function-tool allow-list: null = all) ----

    [Fact]
    public void Apply_RegistersNeither_WhenEnabledToolsEmpty()
    {
        var registry = new FunctionRegistry();
        var (provider, options) = Backend(ApiKey);

        _ = WebToolRegistrationPolicy.Apply(
            registry,
            "openai",
            enabledTools: [],
            provider,
            options,
            NullLoggerFactory.Instance
        );

        RegisteredNames(registry).Should().NotContain("WebFetch").And.NotContain("WebSearch");
    }

    [Fact]
    public void Apply_RegistersOnlyWebFetch_WhenEnabledToolsListsWebFetchOnly()
    {
        var registry = new FunctionRegistry();
        var (provider, options) = Backend(ApiKey);

        _ = WebToolRegistrationPolicy.Apply(
            registry,
            "openai",
            enabledTools: ["WebFetch"],
            provider,
            options,
            NullLoggerFactory.Instance
        );

        var names = RegisteredNames(registry);
        names.Should().Contain("WebFetch");
        names.Should().NotContain("WebSearch");
    }

    [Fact]
    public void Apply_RegistersBoth_WhenEnabledToolsListsBoth()
    {
        var registry = new FunctionRegistry();
        var (provider, options) = Backend(ApiKey);

        _ = WebToolRegistrationPolicy.Apply(
            registry,
            "openai",
            enabledTools: ["WebFetch", "WebSearch"],
            provider,
            options,
            NullLoggerFactory.Instance
        );

        RegisteredNames(registry).Should().Contain("WebFetch").And.Contain("WebSearch");
    }

    // ---- EnabledBuiltInTools divergence is irrelevant to the Jina function tools ----

    [Fact]
    public void Apply_RegistersNoJinaTools_WhenEnabledToolsEmpty_RegardlessOfBuiltInList()
    {
        // The policy only takes EnabledTools (the function-tool list). A mode may diverge its
        // EnabledBuiltInTools (e.g. EnabledTools = [] but EnabledBuiltInTools = ["web_search"]); that
        // built-in list is handled by ModeToolFilter elsewhere and is NOT a parameter here. An empty
        // EnabledTools therefore disables ALL Jina function tools even on an allow-listed provider,
        // proving the Jina tools key off EnabledTools rather than EnabledBuiltInTools.
        var registry = new FunctionRegistry();
        var (provider, options) = Backend(ApiKey);

        _ = WebToolRegistrationPolicy.Apply(
            registry,
            "openai",
            enabledTools: [],
            provider,
            options,
            NullLoggerFactory.Instance
        );

        RegisteredNames(registry).Should().NotContain("WebFetch").And.NotContain("WebSearch");
    }

    // ---- Missing key: WebFetch still registers, WebSearch does not (and reports a status) ----

    [Fact]
    public void Apply_RegistersOnlyWebFetch_AndReportsStatus_WhenKeyMissing()
    {
        var registry = new FunctionRegistry();
        var (provider, options) = Backend(key: null);

        var statuses = WebToolRegistrationPolicy.Apply(
            registry,
            "openai",
            enabledTools: null,
            provider,
            options,
            NullLoggerFactory.Instance
        );

        var names = RegisteredNames(registry);
        names.Should().Contain("WebFetch");
        names.Should().NotContain("WebSearch");
        statuses.Should().Contain("WebSearch disabled: JINA_API_KEY not set");
    }

    // ---- Collision: a pre-registered "webfetch" (lowercase) blocks WebFetch, not WebSearch ----

    [Fact]
    public void Apply_SkipsWebFetch_OnCaseInsensitiveCollision_ButRegistersWebSearch()
    {
        var registry = new FunctionRegistry();
        _ = registry.AddFunction(
            new FunctionContract
            {
                Name = "webfetch",
                Description = "pre-existing tool",
                ReturnType = typeof(string),
            },
            StubHandler,
            "Pre"
        );
        var (provider, options) = Backend(ApiKey);

        var statuses = WebToolRegistrationPolicy.Apply(
            registry,
            "openai",
            enabledTools: null,
            provider,
            options,
            NullLoggerFactory.Instance
        );

        // WebSearch is unaffected by the WebFetch collision.
        RegisteredNames(registry).Should().Contain("WebSearch");
        statuses.Should().Contain("WebFetch skipped: name already registered");

        // Only the original lowercase contract exists; the policy did not add a second one.
        var (contracts, _) = registry.Build();
        contracts
            .Count(c => string.Equals(c.Name, "webfetch", StringComparison.OrdinalIgnoreCase))
            .Should()
            .Be(1);
    }

    // ---- Per-conversation scoping: two registries, two providers, never both capabilities ----

    [Fact]
    public void Apply_ScopesPerRegistry_OpenAiGetsTools_AnthropicDoesNot()
    {
        var openAiRegistry = new FunctionRegistry();
        var anthropicRegistry = new FunctionRegistry();
        var (provider, options) = Backend(ApiKey);

        _ = WebToolRegistrationPolicy.Apply(
            openAiRegistry,
            "openai",
            enabledTools: null,
            provider,
            options,
            NullLoggerFactory.Instance
        );
        _ = WebToolRegistrationPolicy.Apply(
            anthropicRegistry,
            "anthropic",
            enabledTools: null,
            provider,
            options,
            NullLoggerFactory.Instance
        );

        RegisteredNames(openAiRegistry).Should().Contain("WebFetch").And.Contain("WebSearch");
        RegisteredNames(anthropicRegistry).Should().NotContain("WebFetch").And.NotContain("WebSearch");
    }

    // ---- Null provider: nothing is registered ----

    [Fact]
    public void Apply_RegistersNothing_WhenProviderNull()
    {
        var registry = new FunctionRegistry();
        var options = new WebToolsOptions { JinaApiKey = ApiKey };

        var statuses = WebToolRegistrationPolicy.Apply(
            registry,
            "openai",
            enabledTools: null,
            provider: null,
            options,
            NullLoggerFactory.Instance
        );

        RegisteredNames(registry).Should().BeEmpty();
        statuses.Should().BeEmpty();
    }

    private static (JinaWebProvider Provider, WebToolsOptions Options) Backend(string? key)
    {
        var options = new WebToolsOptions { JinaApiKey = key };
        return (new JinaWebProvider(options), options);
    }

    private static HashSet<string> RegisteredNames(FunctionRegistry registry)
    {
        var (contracts, _) = registry.Build();
        return contracts.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
    }

    private static Task<ToolHandlerResult> StubHandler(
        string argsJson,
        ToolCallContext context,
        CancellationToken cancellationToken
    )
    {
        return Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("ok"));
    }
}
