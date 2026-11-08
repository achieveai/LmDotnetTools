using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Http;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmTestUtils;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
///     A provider retry reaches the loop's subscribers as a transient <c>provider_retry</c> frame
///     (#930 P1 item 1), so the UI can say "rate limited, retrying" instead of looking hung. The agent here
///     behaves like the real provider agents: the request, and its retry, happen lazily on the first
///     fetch from the stream, not in the call that returns it.
/// </summary>
public sealed class ProviderRetryFrameTests
{
    /// <summary>Sends its HTTP request through the shared retry helper on the first fetch.</summary>
    private sealed class LazyHttpAgent(HttpClient client) : IStreamingAgent
    {
        public Task<IEnumerable<IMessage>> GenerateReplyAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(StreamAsync(cancellationToken));

        private async IAsyncEnumerable<IMessage> StreamAsync([EnumeratorCancellation] CancellationToken ct)
        {
            _ = await HttpRetryHelper.ExecuteHttpWithRetryAsync(
                () => client.GetAsync("http://provider.test/v1/messages", ct),
                response => response.Content.ReadAsStringAsync(ct),
                NullLogger.Instance,
                RetryOptions.Default,
                ct
            );
            yield return new TextMessage { Text = "after retry", Role = Role.Assistant };
        }
    }

    [Fact]
    public async Task ARateLimitedTurn_PublishesOneProviderRetryFrame_AndStillCompletes()
    {
        var handler = MockHttpHandlerBuilder.Create().RetryScenarioWithRetryAfter(1, "0").Build();
        await using var loop = new MultiTurnAgentLoop(
            new LazyHttpAgent(new HttpClient(handler)),
            new FunctionRegistry(),
            "retry-thread",
            defaultOptions: new GenerateReplyOptions { ModelId = "model-x" }
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _ = loop.RunAsync(cts.Token);

        var received = new List<IMessage>();
        var input = new UserInput([new TextMessage { Text = "Hi", Role = Role.User }], InputId: "input-1");
        await foreach (var msg in loop.ExecuteRunAsync(input, cts.Token))
        {
            received.Add(msg);
        }

        var frame = received.OfType<ProviderRetryMessage>().Should().ContainSingle().Subject;
        frame.Attempt.Should().Be(1);
        frame.MaxAttempts.Should().Be(3);
        frame.StatusCode.Should().Be((int)HttpStatusCode.TooManyRequests);
        frame.FromRetryAfter.Should().BeTrue();
        frame.ThreadId.Should().Be("retry-thread");
        frame.RunId.Should().NotBeNullOrEmpty();
        frame.GenerationId.Should().NotBeNullOrEmpty();
        received.OfType<TextMessage>().Should().Contain(m => m.Text == "after retry");

        await cts.CancelAsync();
    }

    [Fact]
    public void TheFrame_SerializesUnderItsOwnDiscriminator_AndRoundTrips()
    {
        IMessage frame = ProviderRetryMessage.FromNotice(
            new HttpRetryNotice(1, 3, HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(7), true)
        ) with
        {
            ThreadId = "t-1",
        };

        var json = JsonSerializer.Serialize(frame);
        var back = JsonSerializer.Deserialize<IMessage>(json);

        json.Should().Contain("\"$type\":\"provider_retry\"").And.Contain("\"delayMs\":7000");
        back.Should().BeOfType<ProviderRetryMessage>().Which.Should().Be(frame);
    }
}
