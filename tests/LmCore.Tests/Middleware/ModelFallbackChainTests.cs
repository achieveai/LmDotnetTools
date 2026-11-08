using System.Net;
using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Middleware;

/// <summary>
///     Model fallback over real provider-shaped streams (#930 P1 item 8). A provider stream is lazy: its
///     request, and so its outage, happens on the first read, not in the call that returns it. Fallback
///     has to look there, must not fall back on the caller's own cancellation or on a request the next
///     model would reject too, and must credit a fallback reply's usage to the model that ran.
/// </summary>
public class ModelFallbackChainTests
{
    private static readonly IMessage[] Prompt = [new TextMessage { Text = "hi", Role = Role.User }];

    [Fact]
    public async Task ALazyStreamThatFailsOnItsFirstRead_FallsThroughToTheNextAgent()
    {
        var backup = new ScriptedAgent(Reply("from the backup"));
        var middleware = new ModelFallbackMiddleware(
            new Dictionary<string, IAgent[]> { ["model-a"] = [new ScriptedAgent(Outage(503)), backup] },
            new ScriptedAgent(Reply("from the default"))
        );

        var replies = await ReadAllAsync(
            await middleware.InvokeStreamingAsync(Context("model-a"), new ScriptedAgent(Reply("unused")))
        );

        replies.OfType<TextMessage>().Single().Text.Should().Be("from the backup");
        backup.Calls.Should().Be(1);
    }

    [Fact]
    public async Task AnOutage_FallsThroughToTheNextModel_WithThatModelsRequest_AndCreditsItsUsageToIt()
    {
        var logger = new HopLogger();
        var backup = new ScriptedAgent(Reply("from the backup", withUsage: true));
        var middleware = new ModelFallbackMiddleware(
            [new FallbackTarget("backup-model", backup, options => options with { BuiltInTools = null })],
            logger
        );
        var primaryRequest = new GenerateReplyOptions
        {
            ModelId = "primary-model",
            BuiltInTools = [new object()],
            Temperature = 0.3f,
        };

        var replies = await ReadAllAsync(
            await middleware.InvokeStreamingAsync(
                new MiddlewareContext(Prompt, primaryRequest),
                new ScriptedAgent(Outage(503))
            )
        );

        replies.OfType<TextMessage>().Single().Text.Should().Be("from the backup");
        backup.LastOptions!.ModelId.Should().Be("backup-model");
        backup.LastOptions.BuiltInTools.Should().BeNull("the target reshapes the request for its own provider");
        backup.LastOptions.Temperature.Should().Be(0.3f, "everything the target does not reshape is kept");
        replies
            .OfType<UsageMessage>()
            .Single()
            .Usage.GetExtraProperty<string>(ModelFallbackMiddleware.UsageModelProperty)
            .Should()
            .Be("backup-model", "the spend belongs to the model that ran, not the one that was asked for");
        logger
            .Hops.Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(new Hop("primary-model", "backup-model", "HTTP 503"));
    }

    [Fact]
    public async Task UsageAProviderAlreadyAttributed_KeepsItsModel()
    {
        var reportedUsage = new UsageMessage
        {
            Usage = new Usage { PromptTokens = 5 }.SetExtraProperty(
                ModelFallbackMiddleware.UsageModelProperty,
                "backup-model-2026-11"
            ),
        };
        var middleware = new ModelFallbackMiddleware([
            new FallbackTarget("backup-model", new ScriptedAgent(Messages(reportedUsage))),
        ]);

        var replies = await ReadAllAsync(
            await middleware.InvokeStreamingAsync(Context("primary-model"), new ScriptedAgent(Outage(502)))
        );

        replies
            .OfType<UsageMessage>()
            .Single()
            .Usage.GetExtraProperty<string>(ModelFallbackMiddleware.UsageModelProperty)
            .Should()
            .Be("backup-model-2026-11");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheCallersCancellation_DoesNotFallBack(bool throwsOnTheCall)
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var backup = new ScriptedAgent(Reply("from the backup"));
        // An agent may notice the cancelled token when called, or only on its first read.
        var cancelled = throwsOnTheCall
            ? new ScriptedAgent(Reply("unused"), throwsOnCall: new OperationCanceledException(cts.Token))
            : new ScriptedAgent(CallerCancelled());
        var middleware = new ModelFallbackMiddleware(
            new Dictionary<string, IAgent[]> { ["model-a"] = [cancelled, backup] },
            backup
        );

        var act = async () =>
            await ReadAllAsync(
                await middleware.InvokeStreamingAsync(Context("model-a"), new ScriptedAgent(Reply("unused")), cts.Token)
            );

        _ = await act.Should().ThrowAsync<OperationCanceledException>();
        backup.Calls.Should().Be(0, "a turn the caller stopped must not start again on another model");
    }

    [Fact]
    public async Task ABadRequest_DoesNotFallBack_AndReachesTheCallerAsIs()
    {
        var backup = new ScriptedAgent(Reply("from the backup"));
        var middleware = new ModelFallbackMiddleware([new FallbackTarget("backup-model", backup)]);

        var act = async () =>
            await ReadAllAsync(
                await middleware.InvokeStreamingAsync(Context("primary-model"), new ScriptedAgent(Outage(400)))
            );

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        backup.Calls.Should().Be(0);
    }

    [Fact]
    public async Task AFailureAfterTheReplyStarted_ReachesTheCaller_WithoutFallingBack()
    {
        var backup = new ScriptedAgent(Reply("from the backup"));
        var middleware = new ModelFallbackMiddleware([new FallbackTarget("backup-model", backup)]);
        var received = new List<IMessage>();

        var act = async () =>
        {
            var stream = await middleware.InvokeStreamingAsync(
                Context("primary-model"),
                new ScriptedAgent(FailsAfterFirstMessage)
            );
            await foreach (var message in stream)
            {
                received.Add(message);
            }
        };

        _ = await act.Should().ThrowAsync<HttpRequestException>();
        received.OfType<TextMessage>().Select(m => m.Text).Should().Equal("partial");
        backup.Calls.Should().Be(0, "output from one model cannot be continued by another");
    }

    [Fact]
    public async Task EveryModelDown_ReportsTheLastOutage()
    {
        var middleware = new ModelFallbackMiddleware([
            new FallbackTarget("backup-1", new ScriptedAgent(Outage(503))),
            new FallbackTarget("backup-2", new ScriptedAgent(Outage(429))),
        ]);

        var act = async () =>
            await ReadAllAsync(
                await middleware.InvokeStreamingAsync(Context("primary-model"), new ScriptedAgent(Outage(500)))
            );

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.StatusCode.Should()
            .Be(HttpStatusCode.TooManyRequests);
    }

    [Theory]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(529, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    [InlineData(422, false)]
    public void IsProviderOutage_FallsBackOnTransientStatusesOnly(int status, bool expected)
    {
        ModelFallbackMiddleware
            .IsProviderOutage(new HttpRequestException("x", null, (HttpStatusCode)status))
            .Should()
            .Be(expected);
    }

    [Fact]
    public void IsProviderOutage_TreatsTransportFailuresAndTimeoutsAsOutages_AndBugsAsNot()
    {
        ModelFallbackMiddleware.IsProviderOutage(new HttpRequestException("connection refused")).Should().BeTrue();
        ModelFallbackMiddleware.IsProviderOutage(new TaskCanceledException("HttpClient.Timeout")).Should().BeTrue();
        ModelFallbackMiddleware.IsProviderOutage(new TimeoutException()).Should().BeTrue();
        ModelFallbackMiddleware.IsProviderOutage(new InvalidOperationException()).Should().BeFalse();
        ModelFallbackMiddleware.IsProviderOutage(new ArgumentException()).Should().BeFalse();
    }

    [Fact]
    public async Task DisposingTheFallbackAgent_DisposesEveryAgentItHoldsOnce()
    {
        var primary = new ScriptedAgent(Reply("x"));
        var backup = new ScriptedAgent(Reply("y"));
        var agent = new ModelFallbackAgent(
            primary,
            [new FallbackTarget("backup-1", backup), new FallbackTarget("backup-2", backup)]
        );

        await agent.DisposeAsync();

        primary.Disposals.Should().Be(1);
        backup.Disposals.Should().Be(1);
    }

    private static MiddlewareContext Context(string modelId) =>
        new(Prompt, new GenerateReplyOptions { ModelId = modelId });

    private static async Task<List<IMessage>> ReadAllAsync(IAsyncEnumerable<IMessage> stream)
    {
        var all = new List<IMessage>();
        await foreach (var message in stream)
        {
            all.Add(message);
        }

        return all;
    }

    private static Func<CancellationToken, IAsyncEnumerable<IMessage>> Reply(string text, bool withUsage = false) =>
        withUsage
            ? Messages(
                new TextMessage { Text = text, Role = Role.Assistant },
                new UsageMessage
                {
                    Usage = new Usage { PromptTokens = 10, CompletionTokens = 3 },
                }
            )
            : Messages(new TextMessage { Text = text, Role = Role.Assistant });

    private static Func<CancellationToken, IAsyncEnumerable<IMessage>> Messages(params IMessage[] messages) =>
        ct => Yield(messages, ct);

    private static async IAsyncEnumerable<IMessage> Yield(
        IMessage[] messages,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        foreach (var message in messages)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return message;
        }
    }

    /// <summary>A provider stream whose request fails on the first read, as AnthropicAgent's does.</summary>
    private static Func<CancellationToken, IAsyncEnumerable<IMessage>> Outage(int status) =>
        _ => FailOnFirstRead(new HttpRequestException($"status {status}", null, (HttpStatusCode)status));

    private static Func<CancellationToken, IAsyncEnumerable<IMessage>> CallerCancelled() =>
        ct => FailOnFirstRead(new OperationCanceledException(ct));

    private static async IAsyncEnumerable<IMessage> FailOnFirstRead(Exception failure)
    {
        await Task.Yield();
        throw failure;
#pragma warning disable CS0162 // An iterator needs a yield to be one.
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<IMessage> FailsAfterFirstMessage(
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        await Task.Yield();
        yield return new TextMessage { Text = "partial", Role = Role.Assistant };
        throw new HttpRequestException("stream reset", null, HttpStatusCode.ServiceUnavailable);
    }

    private sealed class ScriptedAgent(
        Func<CancellationToken, IAsyncEnumerable<IMessage>> stream,
        Exception? throwsOnCall = null
    ) : IStreamingAgent, IDisposable
    {
        public int Calls { get; private set; }

        public int Disposals { get; private set; }

        public GenerateReplyOptions? LastOptions { get; private set; }

        public Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            Calls++;
            LastOptions = options;
            return throwsOnCall is null
                ? Task.FromResult(stream(cancellationToken))
                : Task.FromException<IAsyncEnumerable<IMessage>>(throwsOnCall);
        }

        public Task<IEnumerable<IMessage>> GenerateReplyAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public void Dispose() => Disposals++;
    }

    private sealed record Hop(string FromModel, string ToModel, string FallbackReason);

    /// <summary>Keeps the structured fields of each fallback-hop warning.</summary>
    private sealed class HopLogger : ILogger
    {
        public List<Hop> Hops { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (logLevel != LogLevel.Warning || state is not IReadOnlyList<KeyValuePair<string, object?>> fields)
            {
                return;
            }

            string? Field(string name) => fields.FirstOrDefault(f => f.Key == name).Value?.ToString();
            Hops.Add(new Hop(Field("FromModel")!, Field("ToModel")!, Field("FallbackReason")!));
        }
    }
}
