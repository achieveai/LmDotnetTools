using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;
using LmStreaming.Sample.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// The per-model <c>LmStreaming:ReasoningReplay</c> switch (#769) must reach the request a sub-agent sends
/// when it runs on an explicitly chosen Copilot Responses model. That agent is built by the conversation's
/// characteristics factory in <c>Program.cs</c>, not by the provider factory, so it reads the switch in a
/// second place.
/// </summary>
/// <remarks>
/// The registry is replaced, so no Copilot discovery runs. The Copilot agent factory is replaced with one
/// that builds the real Responses agent over a client that only records the request, so no token is read
/// and nothing leaves the process.
/// </remarks>
public sealed class ReasoningReplayCompositionTests
{
    private const string Model = "gpt-5.6-terra";

    private static AgentProfile DefaultMode => SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;

    private sealed class CapturingClient : IOpenAiResponsesClient
    {
        public List<ResponseCreateRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ResponseEvent> StreamResponseAsync(
            ResponseCreateRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            lock (Requests)
            {
                Requests.Add(request);
            }

            await Task.Yield();
            yield return new ResponseLifecycleEvent { Type = ResponseEventTypes.ResponseCompleted };
        }

        public void Dispose() { }
    }

    private sealed class ReplayWebAppFactory : WebApplicationFactory<Program>
    {
        private readonly CapturingClient _client;

        public ReplayWebAppFactory(CapturingClient client)
        {
            _client = client;

            // Keeps startup provider discovery side-effect-free (no API key, no network).
            Environment.SetEnvironmentVariable("LM_PROVIDER_MODE", "test");
            Environment.SetEnvironmentVariable("CLAUDE_CLI_PATH", null);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Avoids the Vite dev-server auto-spawn.
            builder.UseEnvironment("Production");

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IConversationStore>();
                services.AddSingleton<IConversationStore>(new InMemoryConversationStore());

                services.RemoveAll<ProviderRegistry>();
                services.AddSingleton(
                    new ProviderRegistry([
                        new CopilotModelInfo(Model, Model, CopilotModelVendor.OpenAI, CopilotModelTransport.Responses),
                    ])
                );

                services.RemoveAll<Program.CopilotModelAgentFactory>();
                services.AddSingleton<Program.CopilotModelAgentFactory>(
                    (model, _, _, replayReasoning) =>
                        new OpenAiResponsesAgent(model.Id, _client, replayReasoning: replayReasoning)
                );
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                Environment.SetEnvironmentVariable("LM_PROVIDER_MODE", null);
            }
        }
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task ASubAgentOnAChosenModel_SendsTheStoredBlobOnlyWhenThatModelsSwitchIsOn(
        bool replay,
        int reasoningItems
    )
    {
        var client = new CapturingClient();
        await using var host = new ReplayWebAppFactory(client);
        host.Services.GetRequiredService<IConfiguration>()[$"LmStreaming:ReasoningReplay:{Model}"] = replay.ToString();

        var loop = (MultiTurnAgentLoop)
            host
                .Services.GetRequiredService<MultiTurnAgentPool>()
                .GetOrCreateAgent($"replay-{Guid.NewGuid():N}", DefaultMode, Model, requestResponseDumpFileName: null);
        var templates = loop.SubAgentManager!.ChildOptions.Templates.Values.Where(t =>
                t.CharacteristicsAgentFactory != null
            )
            .ToList();
        templates.Should().NotBeEmpty("guard: the conversation must offer a sub-agent");
        var template = templates[0];

        var provider = template.CharacteristicsAgentFactory!(
            new SubAgentCharacteristics(Model, null) { IsModelExplicitlySelected = true }
        );
        var stream = await provider.Agent.GenerateReplyStreamingAsync(
            [
                new TextMessage { Role = Role.User, Text = "go" },
                new ReasoningMessage
                {
                    Role = Role.Assistant,
                    Reasoning = "BLOB",
                    Visibility = ReasoningVisibility.Encrypted,
                    EncryptedOrigin = "responses:" + Model,
                },
                new TextMessage { Role = Role.Assistant, Text = "done" },
                new TextMessage { Role = Role.User, Text = "next" },
            ],
            new GenerateReplyOptions
            {
                ModelId = Model,
                ExtraProperties = ImmutableDictionary<string, object?>.Empty.Add(
                    "Reasoning",
                    new ResponseReasoningOptions { Summary = "auto" }
                ),
            }
        );
        await foreach (var _ in stream) { }

        var request = client.Requests.Should().ContainSingle("only the sub-agent was called").Subject;
        request.Input.Where(i => i.Type == "reasoning").Should().HaveCount(reasoningItems);
    }
}
