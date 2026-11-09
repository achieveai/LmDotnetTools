using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using ChatCompletionRequest = AchieveAi.LmDotnetTools.OpenAIProvider.Models.ChatCompletionRequest;

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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnOutage_FallsThroughToTheNextModel_WithThatModelsRequest_AndCreditsItsUsageToIt(bool streaming)
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

        var replies = await RunAsync(
            middleware,
            new MiddlewareContext(Prompt, primaryRequest),
            new ScriptedAgent(Outage(503)),
            streaming
        );

        replies.OfType<TextMessage>().Single().Text.Should().Be("from the backup");
        backup.LastOptions!.ModelId.Should().Be("backup-model");
        backup.LastOptions.BuiltInTools.Should().BeNull("the target reshapes the request for its own provider");
        backup.LastOptions.Temperature.Should().Be(0.3f, "everything the target does not reshape is kept");
        var usage = replies.OfType<UsageMessage>().Single().Usage;
        usage
            .GetExtraProperty<string>(Usage.ModelExtraProperty)
            .Should()
            .Be("backup-model", "the spend belongs to the model that ran, not the one that was asked for");
        usage.GetExtraProperty<string>("billing_note").Should().Be("kept", "crediting adds a key, it drops none");
        usage.PromptTokens.Should().Be(10, "crediting the model keeps the provider's counts");
        usage.CompletionTokens.Should().Be(3, "crediting the model keeps the provider's counts");
        logger
            .Hops.Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(new Hop("primary-model", "backup-model", "HTTP 503"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UsageAProviderAlreadyAttributed_KeepsItsModel(bool streaming)
    {
        var reportedUsage = new UsageMessage
        {
            Usage = new Usage { PromptTokens = 5 }.SetExtraProperty(Usage.ModelExtraProperty, "backup-model-2026-11"),
        };
        var middleware = new ModelFallbackMiddleware([
            new FallbackTarget("backup-model", new ScriptedAgent(Messages(reportedUsage))),
        ]);

        var replies = await RunAsync(middleware, Context("primary-model"), new ScriptedAgent(Outage(502)), streaming);

        replies
            .OfType<UsageMessage>()
            .Single()
            .Usage.GetExtraProperty<string>(Usage.ModelExtraProperty)
            .Should()
            .Be("backup-model-2026-11");
    }

    [Fact]
    public async Task AWorkingPrimary_NeverCallsAFallback()
    {
        var backup = new ScriptedAgent(Reply("from the backup"));
        var middleware = new ModelFallbackMiddleware([new FallbackTarget("backup-model", backup)]);

        var replies = await RunAsync(
            middleware,
            Context("primary-model"),
            new ScriptedAgent(Reply("from the primary")),
            streaming: true
        );

        replies.OfType<TextMessage>().Single().Text.Should().Be("from the primary");
        backup.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SeveralTargets_AreTriedInOrder_OnceEach_UntilOneAnswers(bool streaming)
    {
        var calls = new List<string>();
        var primary = new ScriptedAgent(Outage(503)) { Name = "primary", CallLog = calls };
        var first = new ScriptedAgent(Outage(429)) { Name = "first", CallLog = calls };
        var second = new ScriptedAgent(Reply("from the second")) { Name = "second", CallLog = calls };
        var third = new ScriptedAgent(Reply("from the third")) { Name = "third", CallLog = calls };
        var middleware = new ModelFallbackMiddleware([
            new FallbackTarget("first-model", first),
            new FallbackTarget("second-model", second),
            new FallbackTarget("third-model", third),
        ]);

        var replies = await RunAsync(middleware, Context("primary-model"), primary, streaming);

        replies.OfType<TextMessage>().Single().Text.Should().Be("from the second");
        calls.Should().Equal("primary", "first", "second");
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
            ? new ScriptedAgent(Reply("unused")) { ThrowsOnCall = new OperationCanceledException(cts.Token) }
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheCallersCancellation_DoesNotFallBack_OnTheChain(bool streaming)
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var cancellation = new OperationCanceledException(cts.Token);
        var backup = new ScriptedAgent(Reply("from the backup"));
        var middleware = new ModelFallbackMiddleware([new FallbackTarget("backup-model", backup)]);

        var act = () =>
            RunAsync(
                middleware,
                Context("primary-model"),
                new ScriptedAgent(Reply("unused")) { ThrowsOnCall = cancellation },
                streaming,
                cts.Token
            );

        (await act.Should().ThrowAsync<OperationCanceledException>()).Which.Should().BeSameAs(cancellation);
        backup.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACancelledLastResort_AfterAnEarlierOutage_ReportsTheCancellation_NotTheOutage(bool streaming)
    {
        using var cts = new CancellationTokenSource();
        var cancellation = new OperationCanceledException(cts.Token);
        var lastResort = new ScriptedAgent(Reply("unused")) { OnCall = cts.Cancel, ThrowsOnCall = cancellation };
        var middleware = new ModelFallbackMiddleware(
            new Dictionary<string, IAgent[]> { ["model-a"] = [new ScriptedAgent(Outage(503))] },
            lastResort
        );

        var act = () =>
            RunAsync(middleware, Context("model-a"), new ScriptedAgent(Reply("unused")), streaming, cts.Token);

        (await act.Should().ThrowAsync<OperationCanceledException>())
            .Which.Should()
            .BeSameAs(cancellation, "the caller stopped the turn; the earlier outage is not what ended it");
        lastResort.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACancellationTheCallerDidNotAskFor_FallsBack(bool streaming)
    {
        var backup = new ScriptedAgent(Reply("from the backup"));
        var middleware = new ModelFallbackMiddleware([new FallbackTarget("backup-model", backup)]);

        var replies = await RunAsync(
            middleware,
            Context("primary-model"),
            new ScriptedAgent(Reply("unused")) { ThrowsOnCall = new TaskCanceledException("HttpClient.Timeout") },
            streaming
        );

        replies.OfType<TextMessage>().Single().Text.Should().Be("from the backup");
        backup.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ACancellationDuringTheFailedAttemptsCleanup_StopsTheChain()
    {
        using var cts = new CancellationTokenSource();
        var logger = new HopLogger();
        var backup = new ScriptedAgent(Reply("from the backup"));
        var middleware = new ModelFallbackMiddleware([new FallbackTarget("backup-model", backup)], logger);
        var primary = new ScriptedAgent(_ => new BrokenStream(Status(503), onDispose: cts.Cancel));

        var act = async () =>
            await ReadAllAsync(await middleware.InvokeStreamingAsync(Context("primary-model"), primary, cts.Token));

        _ = await act.Should().ThrowAsync<OperationCanceledException>();
        backup.Calls.Should().Be(0);
        logger.Hops.Should().BeEmpty("no hop happened");
    }

    [Fact]
    public async Task ACleanupThatThrows_KeepsTheOriginalFailure_AndTheNextTargetStillAnswers()
    {
        var logger = new HopLogger();
        var middleware = new ModelFallbackMiddleware(
            [new FallbackTarget("backup-model", new ScriptedAgent(Reply("from the backup")))],
            logger
        );
        var primary = new ScriptedAgent(_ => new BrokenStream(
            Status(503),
            onDispose: () => throw new InvalidOperationException("cleanup")
        ));

        var replies = await ReadAllAsync(await middleware.InvokeStreamingAsync(Context("primary-model"), primary));

        replies.OfType<TextMessage>().Single().Text.Should().Be("from the backup");
        logger.Hops.Should().ContainSingle().Which.FallbackReason.Should().Be("HTTP 503");
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
    public async Task ACustomTrigger_DecidesWhichFailuresFallBack_ButNeverTheCallersCancellation()
    {
        var backup = new ScriptedAgent(Reply("from the backup"));
        var middleware = new ModelFallbackMiddleware(
            [new FallbackTarget("backup-model", backup)],
            shouldFallBack: ex => ex is InvalidOperationException or OperationCanceledException
        );

        var replies = await RunAsync(
            middleware,
            Context("primary-model"),
            new ScriptedAgent(Reply("unused")) { ThrowsOnCall = new InvalidOperationException("bad body") },
            streaming: true
        );
        replies.OfType<TextMessage>().Single().Text.Should().Be("from the backup");

        var outage = () => RunAsync(middleware, Context("primary-model"), new ScriptedAgent(Outage(503)), true);
        _ = await outage.Should().ThrowAsync<HttpRequestException>("this trigger does not cover an outage");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var cancelled = () =>
            RunAsync(middleware, Context("primary-model"), new ScriptedAgent(CallerCancelled()), true, cts.Token);
        _ = await cancelled.Should().ThrowAsync<OperationCanceledException>();
        backup.Calls.Should().Be(1, "only the first case fell back");
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
        ModelFallbackMiddleware.IsProviderOutage(new IOException("connection reset")).Should().BeTrue();
        ModelFallbackMiddleware.IsProviderOutage(new HttpIOException(HttpRequestError.ResponseEnded)).Should().BeTrue();
        ModelFallbackMiddleware.IsProviderOutage(new InvalidOperationException()).Should().BeFalse();
        ModelFallbackMiddleware.IsProviderOutage(new ArgumentException()).Should().BeFalse();
    }

    /// <summary>The real OpenAI client over a real socket: what a provider stream throws when its body breaks.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABodyThatBreaksBeforeAnyOutput_SurfacesAnIOException_ThatIsAnOutage(bool connectionReset)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var headersArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = BreakTheBodyAsync(listener, connectionReset, headersArrived.Task);
        using var http = new HttpClient(
            new HeadersArrivedSignal(new SocketsHttpHandler { UseProxy = false }, headersArrived)
        );
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new OpenClient(http, $"http://127.0.0.1:{port}/v1");

        var failure = await Record.ExceptionAsync(async () =>
        {
            await foreach (var _ in client.StreamingChatCompletionsAsync(new ChatCompletionRequest("m", []))) { }
        });
        await server;

        TestContextLogger.LogDebug(
            "A broken body surfaced as {ExceptionType} (connection reset: {ConnectionReset})",
            failure?.GetType().FullName,
            connectionReset
        );
        failure.Should().BeAssignableTo<IOException>();
        ModelFallbackMiddleware.IsProviderOutage(failure!).Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACancellationBeforeTheFirstRead_HandsOnNoMessage(bool throughTheEnumerationToken)
    {
        using var stop = new CancellationTokenSource();
        var middleware = new ModelFallbackMiddleware([
            new FallbackTarget("backup-model", new ScriptedAgent(Reply("from the backup"))),
        ]);
        var reply = await middleware.InvokeStreamingAsync(
            Context("primary-model"),
            new ScriptedAgent(Reply("first")),
            throughTheEnumerationToken ? CancellationToken.None : stop.Token
        );
        await stop.CancelAsync();
        var seen = new List<IMessage>();

        var read = async () =>
        {
            await foreach (
                var message in reply.WithCancellation(throughTheEnumerationToken ? stop.Token : CancellationToken.None)
            )
            {
                seen.Add(message);
            }
        };

        _ = await read.Should().ThrowAsync<OperationCanceledException>();
        seen.Should().BeEmpty();
    }

    [Theory]
    [InlineData("primary", false)]
    [InlineData("fallback", false)]
    [InlineData("primary", true)]
    [InlineData("fallback", true)]
    public async Task DisposingTheAgentsReplyBeforeReadingIt_OpensNoProviderStream(
        string answeredBy,
        bool throughTheHostsWrapper
    )
    {
        var probe = new StreamProbe();
        var answer = new ScriptedAgent(Tracked(probe, messages: Text("answer")));
        var primary = answeredBy == "fallback" ? new ScriptedAgent(Outage(503)) : answer;
        await using var fallback = new ModelFallbackAgent(primary, [new FallbackTarget("backup-model", answer)]);
        IStreamingAgent agent = throughTheHostsWrapper ? fallback.WithMessageTransformation() : fallback;

        var reply = await agent.GenerateReplyStreamingAsync(Prompt, new GenerateReplyOptions { ModelId = "m" });
        await reply.GetAsyncEnumerator().DisposeAsync();

        primary.Calls.Should().Be(0, "an unread reply must not have opened a stream that no wrapper could release");
        probe.Ended.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadingTheAgentsReply_CallsTheProviderOnce_AndReleasesItsStream(bool throughTheHostsWrapper)
    {
        var probe = new StreamProbe();
        var primary = new ScriptedAgent(Tracked(probe, messages: Text("answer")));
        await using var fallback = new ModelFallbackAgent(
            primary,
            [new FallbackTarget("backup-model", new ScriptedAgent(Reply("from the backup")))]
        );
        IStreamingAgent agent = throughTheHostsWrapper ? fallback.WithMessageTransformation() : fallback;

        var replies = await ReadAllAsync(
            await agent.GenerateReplyStreamingAsync(Prompt, new GenerateReplyOptions { ModelId = "m" })
        );

        replies.OfType<TextMessage>().Should().ContainSingle().Which.Text.Should().Be("answer");
        primary.Calls.Should().Be(1);
        probe.Ended.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACancelledReadOfTheAgentsReply_StopsWithoutFallingBack(bool throughTheEnumerationToken)
    {
        using var stop = new CancellationTokenSource();
        var backup = new ScriptedAgent(Reply("from the backup"));
        await using var agent = new ModelFallbackAgent(
            new ScriptedAgent(Reply("first")),
            [new FallbackTarget("backup-model", backup)]
        );
        var reply = await agent.GenerateReplyStreamingAsync(
            Prompt,
            new GenerateReplyOptions { ModelId = "m" },
            throughTheEnumerationToken ? CancellationToken.None : stop.Token
        );
        await stop.CancelAsync();

        var read = async () =>
        {
            await foreach (
                var _ in reply.WithCancellation(throughTheEnumerationToken ? stop.Token : CancellationToken.None)
            ) { }
        };

        _ = await read.Should().ThrowAsync<OperationCanceledException>();
        backup.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ASecondEnumerationOfTheReply_IsRejected_WithoutASecondProviderCall()
    {
        var primary = new ScriptedAgent(Reply("answer"));
        var middleware = new ModelFallbackMiddleware([
            new FallbackTarget("backup-model", new ScriptedAgent(Reply("from the backup"))),
        ]);
        var reply = await middleware.InvokeStreamingAsync(Context("primary-model"), primary);
        await using var first = reply.GetAsyncEnumerator();
        (await first.MoveNextAsync()).Should().BeTrue();

        var second = () => reply.GetAsyncEnumerator();

        second.Should().Throw<InvalidOperationException>();
        primary.Calls.Should().Be(1);
    }

    [Fact]
    public async Task DisposingTheReplyTwice_ReleasesTheProviderStreamOnce()
    {
        var stream = new DisposeCountingStream();
        var middleware = new ModelFallbackMiddleware([
            new FallbackTarget("backup-model", new ScriptedAgent(Reply("from the backup"))),
        ]);
        var reply = await middleware.InvokeStreamingAsync(Context("primary-model"), new ScriptedAgent(_ => stream));
        var enumerator = reply.GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).Should().BeTrue();

        await enumerator.DisposeAsync();
        await enumerator.DisposeAsync();

        stream.Disposals.Should().Be(1);
    }

    [Theory]
    [InlineData("primary")]
    [InlineData("fallback")]
    [InlineData("empty")]
    public async Task DisposingTheReplyAfterItsFirstRead_ReleasesTheProviderStream(string answeredBy)
    {
        var probe = new StreamProbe();
        var answer = answeredBy == "empty" ? Tracked(probe) : Tracked(probe, messages: Text("answer"));
        var middleware = new ModelFallbackMiddleware([new FallbackTarget("backup-model", new ScriptedAgent(answer))]);
        var primary = new ScriptedAgent(answeredBy == "fallback" ? Outage(503) : answer);

        var reply = await middleware.InvokeStreamingAsync(Context("primary-model"), primary);
        var reading = reply.GetAsyncEnumerator();
        _ = await reading.MoveNextAsync();
        await reading.DisposeAsync();

        probe.Ended.Should().Be(1, "the stream opened to find the first message is the reply's to release");
    }

    [Fact]
    public async Task DisposingTheMiddlewaresReplyBeforeReadingIt_OpensNoProviderStream()
    {
        var probe = new StreamProbe();
        var backup = new ScriptedAgent(Reply("from the backup"));
        var middleware = new ModelFallbackMiddleware([new FallbackTarget("backup-model", backup)]);
        var primary = new ScriptedAgent(Tracked(probe, messages: Text("answer")));

        var reply = await middleware.InvokeStreamingAsync(Context("primary-model"), primary);
        await reply.GetAsyncEnumerator().DisposeAsync();

        primary.Calls.Should().Be(0, "an unread reply must not hold a provider stream that nothing would release");
        backup.Calls.Should().Be(0);
        probe.Ended.Should().Be(0);
    }

    [Fact]
    public async Task DisposingAnUnreadReply_ThroughTheRegisteredMiddleware_CallsNoProvider()
    {
        var backup = new ScriptedAgent(Reply("from the backup"));
        var primary = new ScriptedAgent(Reply("answer"));
        var agent = primary.WithMiddleware(new ModelFallbackMiddleware([new FallbackTarget("backup-model", backup)]));

        var reply = await agent.GenerateReplyStreamingAsync(
            Prompt,
            new GenerateReplyOptions { ModelId = "primary-model" }
        );
        await reply.GetAsyncEnumerator().DisposeAsync();

        primary.Calls.Should().Be(0);
        backup.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public async Task AReadThatSucceedsAfterCancellation_HandsOnNothingMore_AndDoesNotFallBack(
        bool throughTheEnumerationToken,
        int cancelledOnRead
    )
    {
        using var stop = new CancellationTokenSource();
        var provider = new DisposeCountingStream
        {
            Messages = 2,
            OnRead = read =>
            {
                if (read == cancelledOnRead)
                {
                    stop.Cancel();
                }
            },
        };
        var backup = new ScriptedAgent(Reply("from the backup"));
        var middleware = new ModelFallbackMiddleware([new FallbackTarget("backup-model", backup)]);
        var reply = await middleware.InvokeStreamingAsync(
            Context("primary-model"),
            new ScriptedAgent(_ => provider),
            throughTheEnumerationToken ? CancellationToken.None : stop.Token
        );
        var seen = new List<IMessage>();

        var read = async () =>
        {
            await foreach (
                var message in reply.WithCancellation(throughTheEnumerationToken ? stop.Token : CancellationToken.None)
            )
            {
                seen.Add(message);
            }
        };

        _ = await read.Should().ThrowAsync<OperationCanceledException>();
        seen.Should().HaveCount(cancelledOnRead - 1);
        provider.Disposals.Should().Be(1);
        backup.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellingDuringABlockedRead_OfTheAgentsReply_StopsWithoutFallingBack(
        bool throughTheEnumerationToken
    )
    {
        using var stop = new CancellationTokenSource();
        var backup = new ScriptedAgent(Reply("from the backup"));
        var secondReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var agent = new ModelFallbackAgent(
            new ScriptedAgent(ct => BlocksAfterFirstMessage(secondReadStarted, ct)),
            [new FallbackTarget("backup-model", backup)]
        );
        var reply = await agent.GenerateReplyStreamingAsync(
            Prompt,
            new GenerateReplyOptions { ModelId = "m" },
            throughTheEnumerationToken ? CancellationToken.None : stop.Token
        );
        await using var reading = reply.GetAsyncEnumerator(
            throughTheEnumerationToken ? stop.Token : CancellationToken.None
        );
        (await reading.MoveNextAsync()).Should().BeTrue();

        var secondRead = reading.MoveNextAsync().AsTask();
        await secondReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        secondRead.IsCompleted.Should().BeFalse("the provider read is still pending when the token is cancelled");
        await stop.CancelAsync();

        _ = await FluentActions
            .Awaiting(() => secondRead)
            .Should()
            .ThrowAsync<OperationCanceledException>()
            .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        backup.Calls.Should().Be(0);
    }

    [Fact]
    public async Task AnEmptyReply_IsNotAFailure_AndIsReleasedOnce()
    {
        var probe = new StreamProbe();
        var backup = new ScriptedAgent(Reply("from the backup"));
        var middleware = new ModelFallbackMiddleware([new FallbackTarget("backup-model", backup)]);

        var replies = await RunAsync(middleware, Context("primary-model"), new ScriptedAgent(Tracked(probe)), true);

        replies.Should().BeEmpty();
        backup.Calls.Should().Be(0);
        probe.Ended.Should().Be(1);
    }

    [Fact]
    public async Task AFailedFirstRead_RunsTheProviderStreamsCleanup()
    {
        var probe = new StreamProbe();
        var middleware = new ModelFallbackMiddleware([
            new FallbackTarget("backup-model", new ScriptedAgent(Reply("from the backup"))),
        ]);
        var primary = new ScriptedAgent(Tracked(probe, failure: Status(503)));

        _ = await RunAsync(middleware, Context("primary-model"), primary, true);

        probe.Ended.Should().Be(1);
    }

    [Fact]
    public async Task StoppingAfterTheFirstMessage_ReleasesTheProviderStream()
    {
        var probe = new StreamProbe();
        var middleware = new ModelFallbackMiddleware([
            new FallbackTarget("backup-model", new ScriptedAgent(Reply("from the backup"))),
        ]);
        var primary = new ScriptedAgent(Tracked(probe, messages: [.. Text("one"), .. Text("two")]));

        await foreach (var _ in await middleware.InvokeStreamingAsync(Context("primary-model"), primary))
        {
            break;
        }

        probe.Ended.Should().Be(1);
    }

    [Fact]
    public async Task TheEnumerationsOwnToken_CancelsAProviderReadInProgress()
    {
        var middleware = new ModelFallbackMiddleware([
            new FallbackTarget("backup-model", new ScriptedAgent(Reply("from the backup"))),
        ]);
        var secondReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = await middleware.InvokeStreamingAsync(
            Context("primary-model"),
            new ScriptedAgent(ct => BlocksAfterFirstMessage(secondReadStarted, ct))
        );
        using var stop = new CancellationTokenSource();
        await using var reading = reply.GetAsyncEnumerator(stop.Token);
        (await reading.MoveNextAsync()).Should().BeTrue();

        var secondRead = reading.MoveNextAsync().AsTask();
        await secondReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        secondRead.IsCompleted.Should().BeFalse("the provider read is still pending when the token is cancelled");
        await stop.CancelAsync();

        _ = await FluentActions
            .Awaiting(() => secondRead)
            .Should()
            .ThrowAsync<OperationCanceledException>()
            .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    [Fact]
    public async Task AMappedAgentThatIsNotAStreamingAgent_FallsBackWhenItsRepliesFailToRead()
    {
        var backup = new ScriptedAgent(Reply("from the backup"));
        var middleware = new ModelFallbackMiddleware(
            new Dictionary<string, IAgent[]> { ["model-a"] = [new LazilyFailingAgent(), backup] },
            new ScriptedAgent(Reply("from the default"))
        );

        var replies = await RunAsync(middleware, Context("model-a"), new ScriptedAgent(Reply("unused")), true);

        replies.OfType<TextMessage>().Single().Text.Should().Be("from the backup");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WithNoModelId_TheWrappedAgentAnswers_NotTheDefault(bool streaming)
    {
        var defaultAgent = new ScriptedAgent(Reply("from the default"));
        var middleware = new ModelFallbackMiddleware(
            new Dictionary<string, IAgent[]> { ["model-a"] = [new ScriptedAgent(Reply("mapped"))] },
            defaultAgent
        );

        var replies = await RunAsync(
            middleware,
            new MiddlewareContext(Prompt, null),
            new ScriptedAgent(Reply("from the wrapped agent")),
            streaming
        );

        replies.OfType<TextMessage>().Single().Text.Should().Be("from the wrapped agent");
        defaultAgent.Calls.Should().Be(0);
    }

    [Fact]
    public async Task AModelMappedToNoAgents_IsAnsweredByTheDefault()
    {
        var middleware = new ModelFallbackMiddleware(
            new Dictionary<string, IAgent[]> { ["model-a"] = [] },
            new ScriptedAgent(Reply("from the default"))
        );

        var replies = await RunAsync(middleware, Context("model-a"), new ScriptedAgent(Reply("unused")), true);

        replies.OfType<TextMessage>().Single().Text.Should().Be("from the default");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WhenTheDefaultFailsToo_TheLastMappedAgentsFailureIsReported(bool streaming)
    {
        var lastMapped = new HttpRequestException("second mapped", null, HttpStatusCode.BadGateway);
        var middleware = new ModelFallbackMiddleware(
            new Dictionary<string, IAgent[]>
            {
                ["model-a"] =
                [
                    new ScriptedAgent(Reply("unused")) { ThrowsOnCall = new InvalidOperationException("first") },
                    new ScriptedAgent(Reply("unused")) { ThrowsOnCall = lastMapped },
                ],
            },
            new ScriptedAgent(Reply("unused")) { ThrowsOnCall = new InvalidOperationException("default") }
        );

        var act = () => RunAsync(middleware, Context("model-a"), new ScriptedAgent(Reply("unused")), streaming);

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.Should().BeSameAs(lastMapped);
    }

    [Fact]
    public async Task DisposingTheFallbackAgent_DisposesEveryAgentItHoldsOnce()
    {
        var primary = new ScriptedAgent(Reply("x"));
        var backup = new ScriptedAgent(Reply("y"));
        var asyncBackup = new AsyncDisposableAgent();
        var agent = new ModelFallbackAgent(
            primary,
            [
                new FallbackTarget("backup-1", backup),
                new FallbackTarget("backup-2", backup),
                new FallbackTarget("backup-3", asyncBackup),
            ]
        );

        await agent.DisposeAsync();

        primary.Disposals.Should().Be(1);
        backup.Disposals.Should().Be(1);
        asyncBackup.AsyncDisposals.Should().Be(1);
        asyncBackup.SyncDisposals.Should().Be(0, "an async-disposable agent is released through DisposeAsync only");
    }

    [Fact]
    public async Task OneAgentFailingToDispose_DoesNotStopTheOthers()
    {
        var firstFailure = new InvalidOperationException("primary dispose");
        var primary = new ScriptedAgent(Reply("x")) { ThrowsOnDispose = firstFailure };
        var middle = new ScriptedAgent(Reply("y")) { ThrowsOnDispose = new InvalidOperationException("middle") };
        var last = new ScriptedAgent(Reply("z"));
        var agent = new ModelFallbackAgent(
            primary,
            [new FallbackTarget("middle", middle), new FallbackTarget("last", last)]
        );

        var act = async () => await agent.DisposeAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(firstFailure);
        primary.Disposals.Should().Be(1);
        middle.Disposals.Should().Be(1);
        last.Disposals.Should().Be(1);
    }

    private static MiddlewareContext Context(string modelId) =>
        new(Prompt, new GenerateReplyOptions { ModelId = modelId });

    private static async Task<List<IMessage>> RunAsync(
        ModelFallbackMiddleware middleware,
        MiddlewareContext context,
        IStreamingAgent primary,
        bool streaming,
        CancellationToken cancellationToken = default
    ) =>
        streaming
            ? await ReadAllAsync(await middleware.InvokeStreamingAsync(context, primary, cancellationToken))
            : [.. await middleware.InvokeAsync(context, primary, cancellationToken)];

    private static async Task<List<IMessage>> ReadAllAsync(IAsyncEnumerable<IMessage> stream)
    {
        var all = new List<IMessage>();
        await foreach (var message in stream)
        {
            all.Add(message);
        }

        return all;
    }

    private static IMessage[] Text(string text) => [new TextMessage { Text = text, Role = Role.Assistant }];

    private static HttpRequestException Status(int status) => new($"status {status}", null, (HttpStatusCode)status);

    private static Func<CancellationToken, IAsyncEnumerable<IMessage>> Reply(string text, bool withUsage = false) =>
        withUsage
            ? Messages(
                new TextMessage { Text = text, Role = Role.Assistant },
                new UsageMessage
                {
                    Usage = new Usage
                    {
                        PromptTokens = 10,
                        CompletionTokens = 3,
                        ExtraProperties = ImmutableDictionary<string, object?>.Empty.Add("billing_note", "kept"),
                    },
                }
            )
            : Messages(Text(text));

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

    /// <summary>Counts how many times a provider stream's own cleanup (its finally) ran.</summary>
    private sealed class StreamProbe
    {
        public int Ended { get; set; }
    }

    private static Func<CancellationToken, IAsyncEnumerable<IMessage>> Tracked(
        StreamProbe probe,
        Exception? failure = null,
        IMessage[]? messages = null
    ) => ct => TrackedYield(probe, failure, messages ?? [], ct);

    private static async IAsyncEnumerable<IMessage> TrackedYield(
        StreamProbe probe,
        Exception? failure,
        IMessage[] messages,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        try
        {
            await Task.Yield();
            if (failure is not null)
            {
                throw failure;
            }

            foreach (var message in messages)
            {
                ct.ThrowIfCancellationRequested();
                yield return message;
            }
        }
        finally
        {
            probe.Ended++;
        }
    }

    /// <summary>A provider stream whose request fails on the first read, as AnthropicAgent's does.</summary>
    private static Func<CancellationToken, IAsyncEnumerable<IMessage>> Outage(int status) =>
        _ => FailOnFirstRead(Status(status));

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

    /// <summary>A reply that sends its first message, then waits on its read token for the next one.</summary>
    private static async IAsyncEnumerable<IMessage> BlocksAfterFirstMessage(
        TaskCompletionSource secondReadStarted,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        await Task.Yield();
        yield return new TextMessage { Text = "first", Role = Role.Assistant };
        secondReadStarted.TrySetResult();
        await Task.Delay(Timeout.Infinite, ct);
    }

    /// <summary>Accepts one request, answers with headers and half an SSE line, then breaks the connection.</summary>
    private static async Task BreakTheBodyAsync(TcpListener listener, bool connectionReset, Task headersArrived)
    {
        using var socket = await listener.AcceptSocketAsync();
        await ReadWholeRequestAsync(socket);
        await socket.SendAsync(
            Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\n\r\n5\r\n: par\r\n"
            )
        );
        await headersArrived.WaitAsync(TimeSpan.FromSeconds(5));
        if (connectionReset)
        {
            socket.LingerState = new LingerOption(true, 0);
        }

        socket.Close();
    }

    private static async Task ReadWholeRequestAsync(Socket socket)
    {
        var buffer = new byte[8192];
        var request = new StringBuilder();
        while (true)
        {
            var read = await socket.ReceiveAsync(buffer);
            _ = request.Append(Encoding.ASCII.GetString(buffer, 0, read));
            var text = request.ToString();
            var headersEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headersEnd < 0)
            {
                continue;
            }

            var length = Regex.Match(text, @"Content-Length: (\d+)", RegexOptions.IgnoreCase).Groups[1].Value;
            if (text.Length >= headersEnd + 4 + int.Parse(length))
            {
                return;
            }
        }
    }

    /// <summary>Lets the server break the connection only once the client has the response headers.</summary>
    private sealed class HeadersArrivedSignal(HttpMessageHandler inner, TaskCompletionSource arrived)
        : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var response = await base.SendAsync(request, cancellationToken);
            arrived.TrySetResult();
            return response;
        }
    }

    /// <summary>A provider stream that fails its first read and runs <paramref name="onDispose" /> on cleanup.</summary>
    private sealed class BrokenStream(Exception failure, Action onDispose)
        : IAsyncEnumerable<IMessage>,
            IAsyncEnumerator<IMessage>
    {
        public IMessage Current => throw new InvalidOperationException("Nothing was read.");

        public IAsyncEnumerator<IMessage> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;

        public ValueTask<bool> MoveNextAsync() => ValueTask.FromException<bool>(failure);

        public ValueTask DisposeAsync()
        {
            onDispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A provider stream that counts every dispose; an iterator would hide a second one.</summary>
    private sealed class DisposeCountingStream : IAsyncEnumerable<IMessage>, IAsyncEnumerator<IMessage>
    {
        private int _reads;

        public int Messages { get; init; } = 1;

        /// <summary>Runs inside each read, and the read still succeeds, as with a provider that ignores its token.</summary>
        public Action<int>? OnRead { get; init; }

        public int Disposals { get; private set; }

        public IMessage Current { get; } = new TextMessage { Text = "answer", Role = Role.Assistant };

        public IAsyncEnumerator<IMessage> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;

        public ValueTask<bool> MoveNextAsync()
        {
            _reads++;
            OnRead?.Invoke(_reads);
            return ValueTask.FromResult(_reads <= Messages);
        }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedAgent(Func<CancellationToken, IAsyncEnumerable<IMessage>> stream)
        : IStreamingAgent,
            IDisposable
    {
        public string Name { get; init; } = "agent";

        public List<string>? CallLog { get; init; }

        public Action? OnCall { get; init; }

        public Exception? ThrowsOnCall { get; init; }

        public Exception? ThrowsOnDispose { get; init; }

        public int Calls { get; private set; }

        public int Disposals { get; private set; }

        public GenerateReplyOptions? LastOptions { get; private set; }

        public Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            Called(options);
            return ThrowsOnCall is null
                ? Task.FromResult(stream(cancellationToken))
                : Task.FromException<IAsyncEnumerable<IMessage>>(ThrowsOnCall);
        }

        public async Task<IEnumerable<IMessage>> GenerateReplyAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            Called(options);
            if (ThrowsOnCall is not null)
            {
                throw ThrowsOnCall;
            }

            return await ReadAllAsync(stream(cancellationToken));
        }

        public void Dispose()
        {
            Disposals++;
            if (ThrowsOnDispose is not null)
            {
                throw ThrowsOnDispose;
            }
        }

        private void Called(GenerateReplyOptions? options)
        {
            Calls++;
            LastOptions = options;
            TestContextLogger.LogDebug("Agent {AgentName} called for model {ModelId}", Name, options?.ModelId);
            CallLog?.Add(Name);
            OnCall?.Invoke();
        }
    }

    /// <summary>A non-streaming agent whose buffered replies fail only when they are read.</summary>
    private sealed class LazilyFailingAgent : IAgent
    {
        public Task<IEnumerable<IMessage>> GenerateReplyAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(FailOnEnumeration());

        private static IEnumerable<IMessage> FailOnEnumeration()
        {
            throw new HttpRequestException("status 503", null, HttpStatusCode.ServiceUnavailable);
#pragma warning disable CS0162 // An iterator needs a yield to be one.
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class AsyncDisposableAgent : IStreamingAgent, IAsyncDisposable, IDisposable
    {
        public int AsyncDisposals { get; private set; }

        public int SyncDisposals { get; private set; }

        public Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<IEnumerable<IMessage>> GenerateReplyAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            AsyncDisposals++;
            return ValueTask.CompletedTask;
        }

        public void Dispose() => SyncDisposals++;
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
