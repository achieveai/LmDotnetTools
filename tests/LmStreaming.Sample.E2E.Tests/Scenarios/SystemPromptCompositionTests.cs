using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.E2E.Tests.Infrastructure;
using LmStreaming.Sample.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
/// Pins the claim no other test makes: the system prompt the host COMPOSES is the system prompt the
/// model RECEIVES.
/// <para>
/// Every other test of this machinery stops one hop short. <c>ConversationsControllerTests</c> proves
/// provision writes the appendix into thread metadata and that <c>SystemPromptAugmenter.ComposeAsync</c>
/// returns it in the right position — but it asserts on a returned STRING, never on an outbound request.
/// <c>LmStreamingS2SClientTests</c> proves the daemon SENDS the appendix, which is delivery to the host,
/// not application to the model. Between the composed string and the wire sits the agent-factory lambda
/// in <c>Program.cs</c> (~1100 lines, no unit-test seam), and that gap is exactly where this field spent
/// its entire life inert: stored, and read by nothing.
/// </para>
/// <para>
/// The gap was invisible rather than merely uncovered. The real factory IS executed by this E2E harness —
/// <c>E2EWebAppFactory</c> boots the production DI graph and replaces only <c>ITestAgentBuilder</c> — so
/// the call site ran in every scenario here. But <c>AppendCallerInstructions</c> is a no-op on a
/// null/blank appendix, and no test had ever set one, so deleting the call produced byte-identical
/// behavior everywhere. A call site that no test can distinguish from its own deletion is not covered by
/// the tests that happen to execute it.
/// </para>
/// <para>
/// So this test supplies the one input that makes the call site observable, and reads the prompt back off
/// the outbound LLM request rather than from any host-side return value.
/// </para>
/// </summary>
public sealed class SystemPromptCompositionTests
{
    /// <summary>
    /// Deliberately unlike anything in a mode prompt, a workspace suffix or a discovered CLAUDE.md block,
    /// so a match cannot come from any source but the provisioned appendix.
    /// </summary>
    private const string AppendixMarker =
        "CALLER-APPENDIX-MARKER: obey the caller's review methodology and output contract.";

    /// <summary>
    /// Substring of the sample's default mode prompt. Its presence proves the appendix is ADDITIVE — the
    /// host-built prompt survives — which is the property that separates this fix from one that replaces
    /// the mode prompt wholesale.
    /// </summary>
    private const string ModePromptMarker = "helpful assistant";

    [Theory]
    [InlineData("test")]
    [InlineData("test-anthropic")]
    public async Task Provisioned_appendix_reaches_the_model_last_in_the_composed_system_prompt(string providerMode)
    {
        // Captured from inside the role predicate, which the scripted handler invokes with the parsed
        // OUTBOUND request. This is the whole point of the test: the assertion subject is the prompt as
        // the provider received it, not a value any host-side code handed back to us.
        string? promptTheModelReceived = null;

        var responder = ScriptedSseResponder
            .New()
            .ForRole(
                "parent",
                ctx =>
                {
                    promptTheModelReceived ??= ctx.SystemPrompt;
                    return true;
                }
            )
            .Turn(t => t.Text("ack"))
            .Build();

        var handler = providerMode == "test-anthropic" ? responder.AsAnthropicHandler() : responder.AsOpenAiHandler();

        var builder = new ScriptedBuilder(handler);
        using var factory = new E2EWebAppFactory(providerMode, builder);

        var threadId = $"appendix-{providerMode}-{Guid.NewGuid():N}";

        // Seed the appendix the way provision does, under the same key production reads. The provision
        // ENDPOINT's write is already pinned by
        // ConversationsControllerTests.Provision_PersistsTheCallerInstructions_AndTheAgentBuildReadsThemBack;
        // going through HTTP here would additionally require a resolvable workspace, mode and provider,
        // which would gate this test on environment rather than on the behavior under test. Both sides
        // reference SystemPromptAugmenter.AppendixPropertyKey, so there is no literal to drift apart.
        var store = factory.Services.GetRequiredService<IConversationStore>();
        await store.UpdateMetadataAsync(
            threadId,
            existing =>
            {
                var properties =
                    existing?.Properties?.ToBuilder() ?? ImmutableDictionary.CreateBuilder<string, object>();
                properties[SystemPromptAugmenter.AppendixPropertyKey] = AppendixMarker;

                return new ThreadMetadata
                {
                    ThreadId = threadId,
                    CurrentRunId = existing?.CurrentRunId,
                    LatestRunId = existing?.LatestRunId,
                    LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    SessionMappings = existing?.SessionMappings,
                    Properties = properties.ToImmutable(),
                };
            }
        );

        // Guard the fixture before trusting the wire assertion below. If the seed is not readable through
        // the exact reader production uses, a failure downstream would be this test's own setup rather
        // than the host dropping the appendix — two very different findings that look identical.
        var seeded = await SystemPromptAugmenter.ReadAppendixAsync(store, threadId);
        seeded.Should().Be(AppendixMarker, "the seed must be visible to production's own reader");

        var socket = await factory.ConnectWebSocketAsync(threadId);
        await using var client = new WebSocketTestClient(socket);
        await client.SendUserMessageAsync("say hello");
        using var frames = await client.CollectUntilDoneAsync(TimeSpan.FromSeconds(15));

        // Guard the instrument before trusting it: if the turn never reached the provider there would be
        // no captured prompt, and every assertion below would be vacuous rather than failing.
        frames.ConcatText().Should().Contain("ack");
        promptTheModelReceived
            .Should()
            .NotBeNull("the scripted provider must have received a request to capture a prompt from");

        var prompt = promptTheModelReceived!;

        // 1. The claim under test. Deleting the ComposeAsync call in Program.cs fails exactly here.
        prompt
            .Should()
            .Contain(
                AppendixMarker,
                "the caller's instructions must reach the model, not merely the thread's metadata"
            );

        // 2. Additive, not a replacement — the host-built prompt is still there.
        prompt.Should().Contain(ModePromptMarker);

        // 3. PrependCurrentDate reached the model too. Same class of call site, same failure mode: it is
        //    applied while the mode is resolved and nothing else observes it end-to-end.
        prompt.Should().Contain("The current date is");

        // 4. Ordering, end-to-end. The appendix is last because recency is load-bearing — the caller is
        //    adding a task on top of a workspace agent. ConversationsControllerTests pins this on the
        //    composed string; this pins it on what actually went over the wire.
        prompt.TrimEnd().Should().EndWith(AppendixMarker);
        prompt
            .IndexOf(AppendixMarker, StringComparison.Ordinal)
            .Should()
            .BeGreaterThan(
                prompt.IndexOf(ModePromptMarker, StringComparison.Ordinal),
                "the appendix must follow every host-built section, not precede them"
            );
    }
}
