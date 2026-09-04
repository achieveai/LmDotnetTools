using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Audit;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LmMultiTurn.Tests.Audit;

public class MultiTurnAuditCaptureTests
{
    private const string RequestTail = "REQUEST-TAIL-6f9a";

    private static readonly JsonSerializerOptions MessageJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new IMessageJsonConverter() },
    };

    [Fact]
    public async Task SettledRequestIsCapturedBeforeDispatch_AndCompleteResponsesAreNotCapped()
    {
        const string systemPrompt = "Follow the complete request exactly.";
        var userText = new string('u', 14_000 - RequestTail.Length) + RequestTail;
        var firstResponseText = new string('a', 36_000) + "-FIRST-END";
        var secondResponseText = new string('b', 36_000) + "-SECOND-END";
        var sink = new RecordingAuditSink();
        var providerSawCapturedRequest = false;

        var provider = new DelegatingStreamingAgent(
            (sent, _, ct) =>
            {
                providerSawCapturedRequest = sink.Records.Any(record => record.RecordType == "model_request");
                sent.Should().HaveCount(2, "the provider and audit sink must observe the same settled request");
                return Task.FromResult(
                    Emit(
                        [
                            new TextMessage { Text = firstResponseText, Role = Role.Assistant },
                            new TextMessage { Text = secondResponseText, Role = Role.Assistant },
                        ],
                        ct
                    )
                );
            }
        );

        var services = new MultiTurnLifecycleServices
        {
            AuditSink = sink,
            AuditScope = new MultiTurnAuditScope("engagement-17", "round-3"),
            ProviderId = "audit-provider",
        };
        await using var loop = new MultiTurnAgentLoop(
            provider,
            new FunctionRegistry(),
            "audit-thread",
            systemPrompt: systemPrompt,
            defaultOptions: new GenerateReplyOptions { ModelId = "audit-model" },
            lifecycleServices: services
        );
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = loop.RunAsync(cts.Token);

        _ = await DrainAsync(
            loop.ExecuteRunAsync(
                new UserInput([new TextMessage { Text = userText, Role = Role.User }], InputId: "audit-input"),
                cts.Token
            )
        );

        providerSawCapturedRequest.Should().BeTrue("request audit acceptance must happen before provider dispatch");

        var request = sink.Records.Should().ContainSingle(record => record.RecordType == "model_request").Subject;
        request.Scope.Should().Be(new MultiTurnAuditScope("engagement-17", "round-3"));
        request.ThreadId.Should().Be("audit-thread");
        request.RunId.Should().NotBeNullOrWhiteSpace();
        request.GenerationId.Should().NotBeNullOrWhiteSpace();
        request.ModelId.Should().Be("audit-model");
        request.ProviderId.Should().Be("audit-provider");
        request.Outcome.Should().Be(AuditCaptureOutcome.Complete);
        request.ByteCount.Should().Be(request.Content.Length);

        var requestMessages = DeserializeRequestMessages(request.Content);
        requestMessages.Should().HaveCount(2, "dropping the final request message must be detected");
        requestMessages[0]
            .Should()
            .BeOfType<TextMessage>()
            .Which.Should()
            .Match<TextMessage>(m => m.Role == Role.System && m.Text == systemPrompt);
        requestMessages[1]
            .Should()
            .BeOfType<TextMessage>()
            .Which.Should()
            .Match<TextMessage>(m =>
                m.Role == Role.User && m.Text.EndsWith(RequestTail, StringComparison.Ordinal) && m.Text == userText
            );

        var responses = sink.Records.Where(record => record.RecordType == "model_response").ToList();
        responses.Should().HaveCount(2, "each complete canonical provider message is an exact source record");
        responses.Sum(record => record.ByteCount).Should().BeGreaterThan(64 * 1024);
        responses.Should().OnlyContain(record => record.Outcome == AuditCaptureOutcome.Complete);
        responses.Should().OnlyContain(record => record.Role == "assistant");

        DeserializeMessage(responses[0].Content.Span)
            .Should()
            .BeOfType<TextMessage>()
            .Which.Text.Should()
            .Be(firstResponseText);
        DeserializeMessage(responses[1].Content.Span)
            .Should()
            .BeOfType<TextMessage>()
            .Which.Text.Should()
            .Be(secondResponseText);

        await cts.CancelAsync();
    }

    [Fact]
    public async Task WrapUpRequestIsCapturedBeforeDispatch_AndCanonicalResponseIsCapturedBeforeHistory()
    {
        var sink = new RecordingAuditSink();
        var calls = 0;
        var wrapUpRequestWasCaptured = false;
        var provider = new DelegatingStreamingAgent(
            (sent, options, ct) =>
            {
                calls++;
                var request = sent.ToList();
                var isWrapUp = request
                    .OfType<TextMessage>()
                    .Any(message =>
                        message.Role == Role.User
                        && message.Text.Contains("maximum number of tool-use turns", StringComparison.Ordinal)
                    );

                if (isWrapUp)
                {
                    wrapUpRequestWasCaptured = sink.Records.Any(record =>
                        record.RecordType == MultiTurnAuditRecordTypes.ModelRequest
                        && record.GenerationId == options.GenerationId
                    );
                    return Task.FromResult(
                        Emit(
                            [
                                new ToolCallMessage
                                {
                                    FunctionName = "continue_work",
                                    FunctionArgs = "{}",
                                    ToolCallId = "call-rejected-wrap-tool",
                                    Role = Role.Assistant,
                                },
                                new TextMessage { Text = "Exact wrap-up response.", Role = Role.Assistant },
                            ],
                            ct
                        )
                    );
                }

                return Task.FromResult(
                    Emit(
                        [
                            new ToolCallMessage
                            {
                                FunctionName = "continue_work",
                                FunctionArgs = "{}",
                                ToolCallId = "call-wrap-audit",
                                Role = Role.Assistant,
                            },
                        ],
                        ct
                    )
                );
            }
        );
        var registry = new FunctionRegistry();
        registry.AddFunction(
            new FunctionContract
            {
                Name = "continue_work",
                Description = "Continue work",
                Parameters = [],
            },
            (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("continued"))
        );
        var services = new MultiTurnLifecycleServices
        {
            AuditSink = sink,
            AuditScope = new MultiTurnAuditScope("engagement-wrap", "round-wrap"),
        };
        await using var loop = new MultiTurnAgentLoop(
            provider,
            registry,
            "wrap-audit-thread",
            maxTurnsPerRun: 1,
            lifecycleServices: services
        );
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = loop.RunAsync(cts.Token);

        _ = await DrainAsync(
            loop.ExecuteRunAsync(
                new UserInput([new TextMessage { Text = "Keep working", Role = Role.User }], InputId: "wrap-input"),
                cts.Token
            )
        );

        calls.Should().Be(2, "the capped run dispatches one normal turn and one wrap-up turn");
        wrapUpRequestWasCaptured.Should().BeTrue("audit acceptance must precede wrap-up provider dispatch");

        var requests = sink
            .Records.Where(record => record.RecordType == MultiTurnAuditRecordTypes.ModelRequest)
            .ToList();
        requests.Should().HaveCount(2);
        var wrapUpRequest = requests[1];
        DeserializeRequestMessages(wrapUpRequest.Content)
            .OfType<TextMessage>()
            .Should()
            .Contain(message =>
                message.Role == Role.User
                && message.Text.Contains("maximum number of tool-use turns", StringComparison.Ordinal)
            );

        var wrapUpResponses = sink
            .Records.Where(record =>
                record.RecordType == MultiTurnAuditRecordTypes.ModelResponse
                && record.GenerationId == wrapUpRequest.GenerationId
            )
            .ToList();
        wrapUpResponses.Should().HaveCount(2, "history rejection must not erase complete provider source evidence");
        DeserializeMessage(wrapUpResponses[0].Content.Span)
            .Should()
            .BeOfType<ToolCallMessage>()
            .Which.ToolCallId.Should()
            .Be("call-rejected-wrap-tool");
        DeserializeMessage(wrapUpResponses[1].Content.Span)
            .Should()
            .BeOfType<TextMessage>()
            .Which.Text.Should()
            .Be("Exact wrap-up response.");

        await cts.CancelAsync();
    }

    [Fact]
    public async Task InterruptedWrapUpStreamCapturesCompletedResponseThenTypedGap_NotLocalFallback()
    {
        const string rawExceptionText = "private wrap-up transport details";
        var sink = new RecordingAuditSink();
        var provider = new DelegatingStreamingAgent(
            (sent, _, ct) =>
            {
                var isWrapUp = sent.OfType<TextMessage>()
                    .Any(message =>
                        message.Role == Role.User
                        && message.Text.Contains("maximum number of tool-use turns", StringComparison.Ordinal)
                    );
                return Task.FromResult(
                    isWrapUp
                        ? EmitThenThrow(
                            [new TextMessage { Text = "Completed before severing.", Role = Role.Assistant }],
                            new HttpIOException(HttpRequestError.ResponseEnded, rawExceptionText),
                            ct
                        )
                        : Emit(
                            [
                                new ToolCallMessage
                                {
                                    FunctionName = "continue_work",
                                    FunctionArgs = "{}",
                                    ToolCallId = "call-wrap-gap",
                                    Role = Role.Assistant,
                                },
                            ],
                            ct
                        )
                );
            }
        );
        var registry = new FunctionRegistry();
        registry.AddFunction(
            new FunctionContract
            {
                Name = "continue_work",
                Description = "Continue work",
                Parameters = [],
            },
            (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("continued"))
        );
        var services = new MultiTurnLifecycleServices
        {
            AuditSink = sink,
            AuditScope = new MultiTurnAuditScope("engagement-wrap-gap", "round-wrap-gap"),
        };
        await using var loop = new MultiTurnAgentLoop(
            provider,
            registry,
            "wrap-gap-thread",
            maxTurnsPerRun: 1,
            lifecycleServices: services
        );
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = loop.RunAsync(cts.Token);

        var messages = await DrainAsync(
            loop.ExecuteRunAsync(
                new UserInput([new TextMessage { Text = "Continue", Role = Role.User }], InputId: "wrap-gap-input"),
                cts.Token
            )
        );

        messages.OfType<RunCompletedMessage>().Should().ContainSingle();
        var wrapRequest = sink.Records.Last(record => record.RecordType == MultiTurnAuditRecordTypes.ModelRequest);
        var wrapRecords = sink.Records.Where(record => record.GenerationId == wrapRequest.GenerationId).ToList();
        wrapRecords.Select(record => record.RecordType).Should().Equal("model_request", "model_response", "stream_gap");
        DeserializeMessage(wrapRecords[1].Content.Span)
            .Should()
            .BeOfType<TextMessage>()
            .Which.Text.Should()
            .Be("Completed before severing.");
        wrapRecords[2].Outcome.Should().Be(AuditCaptureOutcome.Gap);
        wrapRecords[2].Content.Length.Should().Be(0);
        wrapRecords[2].GapReason.Should().Be("HttpIOException:ResponseEnded");
        wrapRecords[2].GapReason.Should().NotContain(rawExceptionText);
        var auditedResponseText = sink
            .Records.Where(record => record.RecordType == MultiTurnAuditRecordTypes.ModelResponse)
            .Select(record => DeserializeMessage(record.Content.Span))
            .OfType<TextMessage>()
            .Select(message => message.Text);
        auditedResponseText
            .Should()
            .NotContain(text => text.Contains("stopped before the task was fully completed", StringComparison.Ordinal));

        await cts.CancelAsync();
    }

    [Fact]
    public async Task NonRetryableWrapUpFailureCapturesCompletedResponseThenTypedGap()
    {
        const string rawExceptionText = "private wrap-up JSON details";
        var sink = new RecordingAuditSink();
        var provider = new DelegatingStreamingAgent(
            (sent, _, ct) =>
            {
                var isWrapUp = sent.OfType<TextMessage>()
                    .Any(message =>
                        message.Role == Role.User
                        && message.Text.Contains("maximum number of tool-use turns", StringComparison.Ordinal)
                    );
                return Task.FromResult(
                    isWrapUp
                        ? EmitThenThrow(
                            [new TextMessage { Text = "Complete wrap-up evidence.", Role = Role.Assistant }],
                            new JsonException(rawExceptionText),
                            ct
                        )
                        : Emit(
                            [
                                new ToolCallMessage
                                {
                                    FunctionName = "continue_work",
                                    FunctionArgs = "{}",
                                    ToolCallId = "call-wrap-json-gap",
                                    Role = Role.Assistant,
                                },
                            ],
                            ct
                        )
                );
            }
        );
        var registry = new FunctionRegistry();
        registry.AddFunction(
            new FunctionContract
            {
                Name = "continue_work",
                Description = "Continue work",
                Parameters = [],
            },
            (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("continued"))
        );
        await using var loop = new MultiTurnAgentLoop(
            provider,
            registry,
            "wrap-json-gap-thread",
            maxTurnsPerRun: 1,
            lifecycleServices: new MultiTurnLifecycleServices
            {
                AuditSink = sink,
                AuditScope = new MultiTurnAuditScope("engagement-wrap-json", "round-wrap-json"),
            }
        );
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = loop.RunAsync(cts.Token);

        _ = await DrainAsync(
            loop.ExecuteRunAsync(
                new UserInput([new TextMessage { Text = "Continue", Role = Role.User }], InputId: "wrap-json"),
                cts.Token
            )
        );

        var wrapRequest = sink.Records.Last(record => record.RecordType == MultiTurnAuditRecordTypes.ModelRequest);
        var records = sink.Records.Where(record => record.GenerationId == wrapRequest.GenerationId).ToList();
        records.Select(record => record.RecordType).Should().Equal("model_request", "model_response", "stream_gap");
        records[2].GapReason.Should().Be("JsonException").And.NotContain(rawExceptionText);
        records[2].Outcome.Should().Be(AuditCaptureOutcome.Gap);
        records[2].Content.Length.Should().Be(0);

        await cts.CancelAsync();
    }

    [Fact]
    public async Task NonRetryableStreamFailureCapturesCompletedResponseThenTypedGap()
    {
        const string rawExceptionText = "private unknown transport details";
        var sink = new RecordingAuditSink();
        var provider = new DelegatingStreamingAgent(
            (_, _, ct) =>
                Task.FromResult(
                    EmitThenThrow(
                        [new TextMessage { Text = "Complete before unknown failure.", Role = Role.Assistant }],
                        new HttpIOException(HttpRequestError.Unknown, rawExceptionText),
                        ct
                    )
                )
        );
        await using var loop = new MultiTurnAgentLoop(
            provider,
            new FunctionRegistry(),
            "unknown-gap-thread",
            lifecycleServices: new MultiTurnLifecycleServices
            {
                AuditSink = sink,
                AuditScope = new MultiTurnAuditScope("engagement-unknown", "round-unknown"),
            }
        );
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = loop.RunAsync(cts.Token);

        _ = await DrainAsync(
            loop.ExecuteRunAsync(
                new UserInput([new TextMessage { Text = "Start", Role = Role.User }], InputId: "unknown-gap"),
                cts.Token
            )
        );

        var generation = sink
            .Records.First(record => record.RecordType == MultiTurnAuditRecordTypes.ModelRequest)
            .GenerationId;
        var records = sink.Records.Where(record => record.GenerationId == generation).ToList();
        records.Select(record => record.RecordType).Should().Equal("model_request", "model_response", "stream_gap");
        records[2].GapReason.Should().Be("HttpIOException:Unknown").And.NotContain(rawExceptionText);
        records[2].Outcome.Should().Be(AuditCaptureOutcome.Gap);
        records[2].Content.Length.Should().Be(0);

        await cts.CancelAsync();
    }

    [Fact]
    public async Task ProviderOwnedCancellationWithLiveCallerCapturesTypedGap_AndCompletesOnlyThatRunAsFailed()
    {
        var sink = new RecordingAuditSink();
        var calls = 0;
        using var providerCancellation = new CancellationTokenSource();
        providerCancellation.Cancel();
        var provider = new DelegatingStreamingAgent(
            (_, _, ct) =>
            {
                calls++;
                return Task.FromResult(
                    calls == 1
                        ? EmitThenThrow(
                            [new TextMessage { Text = "Complete before provider timeout.", Role = Role.Assistant }],
                            new OperationCanceledException(providerCancellation.Token),
                            ct
                        )
                        : Emit([new TextMessage { Text = "Healthy next run.", Role = Role.Assistant }], ct)
                );
            }
        );
        await using var loop = new MultiTurnAgentLoop(
            provider,
            new FunctionRegistry(),
            "provider-cancellation-thread",
            lifecycleServices: new MultiTurnLifecycleServices
            {
                AuditSink = sink,
                AuditScope = new MultiTurnAuditScope("engagement-provider-cancellation", "round-provider-cancellation"),
            }
        );
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = loop.RunAsync(cts.Token);

        var first = await DrainAsync(
            loop.ExecuteRunAsync(
                new UserInput([new TextMessage { Text = "First", Role = Role.User }], InputId: "provider-cancel-1"),
                cts.Token
            )
        );
        var second = await DrainAsync(
            loop.ExecuteRunAsync(
                new UserInput([new TextMessage { Text = "Second", Role = Role.User }], InputId: "provider-cancel-2"),
                cts.Token
            )
        );

        var firstGeneration = sink
            .Records.First(record => record.RecordType == MultiTurnAuditRecordTypes.ModelRequest)
            .GenerationId;
        sink.Records.Where(record => record.GenerationId == firstGeneration)
            .Select(record => record.RecordType)
            .Should()
            .Equal("model_request", "model_response", "stream_gap");
        first
            .OfType<RunCompletedMessage>()
            .Should()
            .ContainSingle()
            .Which.IsError.Should()
            .BeTrue("provider-owned cancellation is a run failure, not caller cancellation");
        second.OfType<TextMessage>().Should().ContainSingle(message => message.Text == "Healthy next run.");
        second.OfType<RunCompletedMessage>().Should().ContainSingle().Which.IsError.Should().BeFalse();

        await cts.CancelAsync();
    }

    [Fact]
    public async Task InterruptedStreamCapturesCompletedResponseThenTypedGap_WithoutRawExceptionText()
    {
        const string rawExceptionText = "secret upstream body must not enter the audit gap";
        var sink = new RecordingAuditSink();
        var calls = 0;
        var provider = new DelegatingStreamingAgent(
            (_, _, ct) =>
            {
                calls++;
                return Task.FromResult(
                    calls == 1
                        ? EmitThenThrow(
                            [new TextMessage { Text = "First complete answer.", Role = Role.Assistant }],
                            new HttpIOException(HttpRequestError.ResponseEnded, rawExceptionText),
                            ct
                        )
                        : Emit([new TextMessage { Text = "Recovered answer.", Role = Role.Assistant }], ct)
                );
            }
        );
        var services = new MultiTurnLifecycleServices
        {
            AuditSink = sink,
            AuditScope = new MultiTurnAuditScope("engagement-gap", "round-gap"),
        };
        await using var loop = new MultiTurnAgentLoop(
            provider,
            new FunctionRegistry(),
            "gap-thread",
            lifecycleServices: services
        );
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = loop.RunAsync(cts.Token);

        _ = await DrainAsync(
            loop.ExecuteRunAsync(
                new UserInput([new TextMessage { Text = "Start", Role = Role.User }], InputId: "gap-input"),
                cts.Token
            )
        );

        var firstGeneration = sink
            .Records.First(record => record.RecordType == MultiTurnAuditRecordTypes.ModelRequest)
            .GenerationId;
        var firstAttemptRecords = sink.Records.Where(record => record.GenerationId == firstGeneration).ToList();
        firstAttemptRecords
            .Select(record => record.RecordType)
            .Should()
            .Equal("model_request", "model_response", "stream_gap");

        var complete = firstAttemptRecords[1];
        complete.Outcome.Should().Be(AuditCaptureOutcome.Complete);
        DeserializeMessage(complete.Content.Span)
            .Should()
            .BeOfType<TextMessage>()
            .Which.Text.Should()
            .Be("First complete answer.");

        var gap = firstAttemptRecords[2];
        gap.Outcome.Should().Be(AuditCaptureOutcome.Gap);
        gap.Content.Length.Should().Be(0, "a gap has no fabricated source bytes");
        gap.ByteCount.Should().Be(0);
        gap.GapReason.Should().Be("HttpIOException:ResponseEnded");
        gap.GapReason.Should().NotContain(rawExceptionText);

        await cts.CancelAsync();
    }

    [Fact]
    public async Task InterruptedStreamDoesNotSwallowToolResultAuditFailureDuringSettlement()
    {
        var provider = new DelegatingStreamingAgent(
            (_, _, ct) =>
                Task.FromResult<IAsyncEnumerable<IMessage>>(
                    EmitThenThrow(
                        [
                            new ToolCallMessage
                            {
                                FunctionName = "lookup",
                                FunctionArgs = "{}",
                                ToolCallId = "call-interrupted-audit-failure",
                                Role = Role.Assistant,
                            },
                        ],
                        new HttpIOException(HttpRequestError.ResponseEnded, "provider response ended"),
                        ct
                    )
                )
        );
        var registry = new FunctionRegistry();
        registry.AddFunction(
            new FunctionContract
            {
                Name = "lookup",
                Description = "Lookup",
                Parameters = [],
            },
            (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("must be audited"))
        );
        var services = new MultiTurnLifecycleServices
        {
            AuditSink = new FailingAuditSink(MultiTurnAuditRecordTypes.ToolResult),
            AuditScope = new MultiTurnAuditScope("engagement-settlement", "round-settlement"),
        };
        await using var loop = new MultiTurnAgentLoop(
            provider,
            registry,
            "settlement-audit-failure-thread",
            lifecycleServices: services
        );
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = loop.RunAsync(cts.Token);

        var messages = await DrainAsync(
            loop.ExecuteRunAsync(
                new UserInput([new TextMessage { Text = "Use lookup", Role = Role.User }], InputId: "settlement-fail"),
                cts.Token
            )
        );

        messages
            .OfType<RunCompletedMessage>()
            .Should()
            .ContainSingle()
            .Which.ErrorMessage.Should()
            .Contain("Audit capture failed for record type 'tool_result'");

        await cts.CancelAsync();
    }

    [Fact]
    public async Task FinalLocalToolResultIsCapturedAsItsOwnTypedSourceRecord()
    {
        var sink = new RecordingAuditSink();
        var calls = 0;
        var provider = new DelegatingStreamingAgent(
            (_, _, ct) =>
            {
                calls++;
                return Task.FromResult(
                    calls == 1
                        ? Emit(
                            [
                                new ToolCallMessage
                                {
                                    FunctionName = "lookup",
                                    FunctionArgs = "{}",
                                    ToolCallId = "call-audit-tool",
                                    Role = Role.Assistant,
                                },
                            ],
                            ct
                        )
                        : Emit([new TextMessage { Text = "Tool handled.", Role = Role.Assistant }], ct)
                );
            }
        );
        var registry = new FunctionRegistry();
        registry.AddFunction(
            new FunctionContract
            {
                Name = "lookup",
                Description = "Return an exact result",
                Parameters = [],
            },
            (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("TOOL-RESULT-TAIL-81bd"))
        );
        var services = new MultiTurnLifecycleServices
        {
            AuditSink = sink,
            AuditScope = new MultiTurnAuditScope("engagement-tool", "round-tool"),
        };
        await using var loop = new MultiTurnAgentLoop(provider, registry, "tool-thread", lifecycleServices: services);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = loop.RunAsync(cts.Token);

        _ = await DrainAsync(
            loop.ExecuteRunAsync(
                new UserInput([new TextMessage { Text = "Use lookup", Role = Role.User }], InputId: "tool-input"),
                cts.Token
            )
        );

        var record = sink
            .Records.Should()
            .ContainSingle(candidate => candidate.RecordType == MultiTurnAuditRecordTypes.ToolResult)
            .Subject;
        record.Role.Should().Be("user");
        record.Outcome.Should().Be(AuditCaptureOutcome.Complete);
        var result = DeserializeMessage(record.Content.Span).Should().BeOfType<ToolCallResultMessage>().Subject;
        result.ToolCallId.Should().Be("call-audit-tool");
        result.Result.Should().Be("TOOL-RESULT-TAIL-81bd");
        result.ExecutionTarget.Should().Be(ExecutionTarget.LocalFunction);

        await cts.CancelAsync();
    }

    [Fact]
    public async Task DeferredToolAuditCapturesFinalTypedResolution_NotThePlaceholder()
    {
        var sink = new RecordingAuditSink();
        var calls = 0;
        var provider = new DelegatingStreamingAgent(
            (_, _, ct) =>
            {
                calls++;
                return Task.FromResult(
                    calls == 1
                        ? Emit(
                            [
                                new ToolCallMessage
                                {
                                    FunctionName = "wait_for_result",
                                    FunctionArgs = "{}",
                                    ToolCallId = "call-deferred-audit",
                                    Role = Role.Assistant,
                                },
                            ],
                            ct
                        )
                        : Emit([new TextMessage { Text = "Resolution received.", Role = Role.Assistant }], ct)
                );
            }
        );
        var registry = new FunctionRegistry();
        registry.AddFunction(
            new FunctionContract
            {
                Name = "wait_for_result",
                Description = "Wait externally",
                Parameters = [],
            },
            (_, _, _) => Task.FromResult<ToolHandlerResult>(new ToolHandlerResult.Deferred())
        );
        var services = new MultiTurnLifecycleServices
        {
            AuditSink = sink,
            AuditScope = new MultiTurnAuditScope("engagement-deferred", "round-deferred"),
        };
        await using var loop = new MultiTurnAgentLoop(
            provider,
            registry,
            "deferred-audit-thread",
            lifecycleServices: services
        );
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = loop.RunAsync(cts.Token);

        _ = await DrainAsync(
            loop.ExecuteRunAsync(
                new UserInput([new TextMessage { Text = "Wait", Role = Role.User }], InputId: "deferred-input"),
                cts.Token
            )
        );
        await loop.ResolveToolCallAsync(
            "call-deferred-audit",
            "DEFERRED-RESULT-TAIL-7cb1",
            contentBlocks:
            [
                new TextToolResultBlock { Text = "exact typed block" },
                new ImageToolResultBlock { Data = "AQIDBA==", MimeType = "image/png" },
            ],
            ct: cts.Token
        );

        var record = sink
            .Records.Should()
            .ContainSingle(candidate => candidate.RecordType == MultiTurnAuditRecordTypes.ToolResult)
            .Subject;
        var result = DeserializeMessage(record.Content.Span).Should().BeOfType<ToolCallResultMessage>().Subject;
        result.ToolCallId.Should().Be("call-deferred-audit");
        result.IsDeferred.Should().BeFalse();
        result.Result.Should().Be("DEFERRED-RESULT-TAIL-7cb1");
        result.ContentBlocks.Should().HaveCount(2);
        result.ContentBlocks![0].Should().BeOfType<TextToolResultBlock>().Which.Text.Should().Be("exact typed block");
        result
            .ContentBlocks[1]
            .Should()
            .BeOfType<ImageToolResultBlock>()
            .Which.Should()
            .Match<ImageToolResultBlock>(block => block.Data == "AQIDBA==" && block.MimeType == "image/png");

        await cts.CancelAsync();
    }

    [Fact]
    public async Task DeferredToolAuditFailureIsStoreFailed_RemainsRetryable_AndThrowingOverloadPreservesFailure()
    {
        var sinkFailure = new IOException("simulated deferred audit failure");
        var sink = new ControllableAuditSink { ToolResultFailure = sinkFailure };
        await using var loop = await CreateDeferredAuditLoopAsync(sink, "call-deferred-store-failure");

        var refused = await loop.TryResolveToolCallAsync("call-deferred-store-failure", "exact result");

        refused.Should().Be(ResolveToolCallOutcome.StoreFailed);
        (await loop.GetDeferredToolCallsAsync())
            .Should()
            .ContainSingle(call => call.ToolCallId == "call-deferred-store-failure");

        var thrown = await Assert.ThrowsAsync<AuditCaptureException>(() =>
            loop.ResolveToolCallAsync("call-deferred-store-failure", "exact result")
        );
        thrown.RecordType.Should().Be(MultiTurnAuditRecordTypes.ToolResult);
        thrown.InnerException.Should().BeSameAs(sinkFailure);

        sink.ToolResultFailure = null;
        var retried = await loop.TryResolveToolCallAsync("call-deferred-store-failure", "exact result");

        retried.Should().Be(ResolveToolCallOutcome.Resolved);
        (await loop.GetDeferredToolCallsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task DeferredToolAuditCallerCancellationIsCancelled_AndHealthyRetryResolves()
    {
        using var caller = new CancellationTokenSource();
        var sink = new ControllableAuditSink { CancelToolResultWith = caller };
        await using var loop = await CreateDeferredAuditLoopAsync(sink, "call-deferred-cancelled");

        var refused = await loop.TryResolveToolCallAsync("call-deferred-cancelled", "exact result", ct: caller.Token);

        refused.Should().Be(ResolveToolCallOutcome.Cancelled);
        (await loop.GetDeferredToolCallsAsync())
            .Should()
            .ContainSingle(call => call.ToolCallId == "call-deferred-cancelled");

        sink.CancelToolResultWith = null;
        var retried = await loop.TryResolveToolCallAsync("call-deferred-cancelled", "exact result");

        retried.Should().Be(ResolveToolCallOutcome.Resolved);
        (await loop.GetDeferredToolCallsAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData(LifecycleAgentKinds.Claude)]
    [InlineData(LifecycleAgentKinds.Codex)]
    [InlineData(LifecycleAgentKinds.Copilot)]
    [InlineData("future-loop")]
    public void AuditEnabledUnsupportedAgentKindFailsClosed(string agentKind)
    {
        var services = new MultiTurnLifecycleServices
        {
            AuditSink = new RecordingAuditSink(),
            AuditScope = new MultiTurnAuditScope("engagement-kind", "round-kind"),
        };

        var act = () => MultiTurnLifecycleServices.ForAgent(services, agentKind);

        act.Should().Throw<NotSupportedException>().WithMessage($"*{agentKind}*audit*");
    }

    [Theory]
    [InlineData(LifecycleAgentKinds.Raw)]
    [InlineData(LifecycleAgentKinds.Claude)]
    [InlineData(LifecycleAgentKinds.Codex)]
    [InlineData(LifecycleAgentKinds.Copilot)]
    [InlineData("future-loop")]
    public void AuditDisabledAgentKindsRemainConstructible(string agentKind)
    {
        var services = new MultiTurnLifecycleServices();

        var stamped = MultiTurnLifecycleServices.ForAgent(services, agentKind);

        stamped.AgentKind.Should().Be(agentKind);
        stamped.IsAuditEnabled.Should().BeFalse();
    }

    [Fact]
    public void AuditSerializationCanonicalizesMetadataWithoutChangingMessageShape()
    {
        var metadataA = ImmutableDictionary<string, object>
            .Empty.Add("zeta", "last")
            .Add("alpha", 1)
            .Add("nested", new Dictionary<string, object> { ["second"] = 2, ["first"] = 1 });
        var metadataB = ImmutableDictionary<string, object>
            .Empty.Add("nested", new Dictionary<string, object> { ["first"] = 1, ["second"] = 2 })
            .Add("alpha", 1)
            .Add("zeta", "last");
        var first = new TextMessage
        {
            Text = "canonical",
            Role = Role.User,
            Metadata = metadataA,
        };
        var second = new TextMessage
        {
            Text = "canonical",
            Role = Role.User,
            Metadata = metadataB,
        };

        var firstMessage = AuditMessageSerializer.SerializeMessage(first);
        var secondMessage = AuditMessageSerializer.SerializeMessage(second);
        var firstRequest = AuditMessageSerializer.SerializeRequest([first]);
        var secondRequest = AuditMessageSerializer.SerializeRequest([second]);

        firstMessage.Should().Equal(secondMessage);
        firstRequest.Should().Equal(secondRequest);
        DeserializeMessage(firstMessage)
            .Should()
            .BeOfType<TextMessage>()
            .Which.Should()
            .Match<TextMessage>(message =>
                message.Text == "canonical"
                && message.Metadata != null
                && (string)message.Metadata["zeta"] == "last"
                && ((JsonElement)message.Metadata["nested"]).GetProperty("second").GetInt32() == 2
            );
    }

    [Fact]
    public async Task HashRecordIdAndTimestampMatchDeterministicContract()
    {
        var capturedAt = new DateTimeOffset(2026, 9, 2, 12, 34, 56, TimeSpan.Zero);
        var sink = new RecordingAuditSink();
        var provider = new DelegatingStreamingAgent(
            (_, _, ct) => Task.FromResult(Emit([new TextMessage { Text = "deterministic", Role = Role.Assistant }], ct))
        );
        var scope = new MultiTurnAuditScope("engagement-deterministic", "round-deterministic");
        var services = new MultiTurnLifecycleServices
        {
            AuditSink = sink,
            AuditScope = scope,
            TimeProvider = new FixedTimeProvider(capturedAt),
        };
        await using var loop = new MultiTurnAgentLoop(
            provider,
            new FunctionRegistry(),
            "deterministic-thread",
            lifecycleServices: services
        );
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = loop.RunAsync(cts.Token);

        _ = await DrainAsync(
            loop.ExecuteRunAsync(
                new UserInput([new TextMessage { Text = "hash me", Role = Role.User }], InputId: "deterministic-input"),
                cts.Token
            )
        );

        var request = sink.Records.First(record => record.RecordType == MultiTurnAuditRecordTypes.ModelRequest);
        var expectedContentHash = Convert.ToHexString(SHA256.HashData(request.Content.Span)).ToLowerInvariant();
        var identity = string.Join(
            "\n",
            scope.EngagementId,
            scope.RoundId,
            request.ThreadId,
            request.RunId,
            request.GenerationId,
            request.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.RecordType,
            expectedContentHash
        );
        var expectedRecordId = Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();

        request.ContentSha256.Should().Be(expectedContentHash).And.MatchRegex("^[0-9a-f]{64}$");
        request.RecordId.Should().Be(expectedRecordId).And.MatchRegex("^[0-9a-f]{64}$");
        request.CapturedAtUtc.Should().Be(capturedAt);

        await cts.CancelAsync();
    }

    [Fact]
    public async Task LegacyConstructorWithoutAuditStillDispatchesNormally()
    {
        var dispatchCount = 0;
        var provider = new DelegatingStreamingAgent(
            (_, _, ct) =>
            {
                dispatchCount++;
                return Task.FromResult(Emit([new TextMessage { Text = "legacy response", Role = Role.Assistant }], ct));
            }
        );
        await using var loop = new MultiTurnAgentLoop(provider, new FunctionRegistry(), "legacy-no-audit-thread");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = loop.RunAsync(cts.Token);

        var messages = await DrainAsync(
            loop.ExecuteRunAsync(
                new UserInput([new TextMessage { Text = "Legacy request", Role = Role.User }], InputId: "legacy-input"),
                cts.Token
            )
        );

        dispatchCount.Should().Be(1);
        messages.OfType<TextMessage>().Should().ContainSingle(message => message.Text == "legacy response");
        messages.OfType<RunCompletedMessage>().Should().ContainSingle().Which.IsError.Should().BeFalse();

        await cts.CancelAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PartialAuditConfigurationIsRejected(bool configureSink)
    {
        var services = new MultiTurnLifecycleServices
        {
            AuditSink = configureSink ? new RecordingAuditSink() : null,
            AuditScope = configureSink ? null : new MultiTurnAuditScope("engagement-partial", "round-partial"),
        };
        var provider = new DelegatingStreamingAgent(
            (_, _, ct) => Task.FromResult(Emit([new TextMessage { Text = "unused", Role = Role.Assistant }], ct))
        );

        var act = () =>
            new MultiTurnAgentLoop(
                provider,
                new FunctionRegistry(),
                "partial-audit-thread",
                lifecycleServices: services
            );

        act.Should().Throw<ArgumentException>().WithMessage("*AuditSink*AuditScope*");
    }

    [Fact]
    public async Task RequestSerializationFailureDoesNotCreatePostDispatchGap()
    {
        var sink = new RecordingAuditSink();
        var dispatchCount = 0;
        var provider = new DelegatingStreamingAgent(
            (_, _, ct) =>
            {
                dispatchCount++;
                return Task.FromResult(Emit([new TextMessage { Text = "must not run", Role = Role.Assistant }], ct));
            }
        );
        var services = new MultiTurnLifecycleServices
        {
            AuditSink = sink,
            AuditScope = new MultiTurnAuditScope("engagement-serialize", "round-request"),
        };
        await using var loop = new MultiTurnAgentLoop(
            provider,
            new FunctionRegistry(),
            "request-serialize-fail-thread",
            lifecycleServices: services
        );
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = loop.RunAsync(cts.Token);

        _ = await DrainAsync(
            loop.ExecuteRunAsync(
                new UserInput(
                    [
                        new TextMessage
                        {
                            Text = "Do not dispatch",
                            Role = Role.User,
                            Metadata = ImmutableDictionary<string, object>.Empty.Add("unsupported", typeof(string)),
                        },
                    ],
                    InputId: "request-serialize-fail"
                ),
                cts.Token
            )
        );

        dispatchCount.Should().Be(0);
        sink.Records.Should().BeEmpty("no provider dispatch occurred, so there is no stream to mark as severed");

        await cts.CancelAsync();
    }

    [Fact]
    public async Task RequestAuditFailurePreventsProviderDispatch()
    {
        var dispatchCount = 0;
        var provider = new DelegatingStreamingAgent(
            (_, _, ct) =>
            {
                dispatchCount++;
                return Task.FromResult(Emit([new TextMessage { Text = "must not run", Role = Role.Assistant }], ct));
            }
        );
        var services = new MultiTurnLifecycleServices
        {
            AuditSink = new FailingAuditSink(MultiTurnAuditRecordTypes.ModelRequest),
            AuditScope = new MultiTurnAuditScope("engagement-fail", "round-request"),
        };
        await using var loop = new MultiTurnAgentLoop(
            provider,
            new FunctionRegistry(),
            "request-fail-thread",
            lifecycleServices: services
        );
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = loop.RunAsync(cts.Token);

        var messages = await DrainAsync(
            loop.ExecuteRunAsync(
                new UserInput(
                    [new TextMessage { Text = "Do not dispatch", Role = Role.User }],
                    InputId: "request-fail"
                ),
                cts.Token
            )
        );

        dispatchCount.Should().Be(0, "a request without durable audit acceptance must never reach a provider");
        messages
            .OfType<RunCompletedMessage>()
            .Should()
            .ContainSingle()
            .Which.ErrorMessage.Should()
            .Contain("Audit capture failed for record type 'model_request'");

        await cts.CancelAsync();
    }

    [Fact]
    public async Task WrapUpResponseAuditFailureRemainsTyped_AndIsNotSwallowedByBestEffortFallback()
    {
        var providerCalls = 0;
        var provider = new DelegatingStreamingAgent(
            (_, _, ct) =>
            {
                providerCalls++;
                return Task.FromResult(
                    Emit(
                        providerCalls == 1
                            ?
                            [
                                new ToolCallMessage
                                {
                                    FunctionName = "continue_work",
                                    FunctionArgs = "{}",
                                    ToolCallId = "call-wrap-fail",
                                    Role = Role.Assistant,
                                },
                            ]
                            : [new TextMessage { Text = "UNCAPTURED-WRAP-UP", Role = Role.Assistant }],
                        ct
                    )
                );
            }
        );
        var registry = new FunctionRegistry();
        registry.AddFunction(
            new FunctionContract
            {
                Name = "continue_work",
                Description = "Continue work",
                Parameters = [],
            },
            (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("continued"))
        );
        var logger = new CapturingLogger();
        var services = new MultiTurnLifecycleServices
        {
            AuditSink = new FailingAuditSink(
                MultiTurnAuditRecordTypes.ModelResponse,
                failOccurrence: 2,
                failure: new HttpIOException(HttpRequestError.ResponseEnded, "private audit-sink transport detail")
            ),
            AuditScope = new MultiTurnAuditScope("engagement-fail", "round-wrap-response"),
        };
        await using var loop = new MultiTurnAgentLoop(
            provider,
            registry,
            "wrap-response-fail-thread",
            maxTurnsPerRun: 1,
            logger: logger,
            lifecycleServices: services
        );
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = loop.RunAsync(cts.Token);

        var messages = await DrainAsync(
            loop.ExecuteRunAsync(
                new UserInput([new TextMessage { Text = "Reach wrap-up", Role = Role.User }], InputId: "wrap-fail"),
                cts.Token
            )
        );

        providerCalls.Should().Be(2);
        messages
            .OfType<RunCompletedMessage>()
            .Should()
            .ContainSingle()
            .Which.ErrorMessage.Should()
            .Contain("Audit capture failed for record type 'model_response'");
        logger.Exceptions.Should().ContainSingle(exception => exception is AuditCaptureException);
        messages
            .OfType<TextMessage>()
            .Should()
            .NotContain(message => message.Text == "UNCAPTURED-WRAP-UP")
            .And.NotContain(message =>
                message.Text.Contains("stopped before the task was fully completed", StringComparison.Ordinal)
            );

        await cts.CancelAsync();
    }

    [Fact]
    public async Task ResponseAuditFailureFailsTheRun_AndUncapturedOutputNeverEntersTheNextRequest()
    {
        var requests = new List<List<IMessage>>();
        var provider = new DelegatingStreamingAgent(
            (sent, _, ct) =>
            {
                requests.Add([.. sent]);
                return Task.FromResult(
                    Emit(
                        [
                            new TextUpdateMessage { Text = "UNCAPTURED-DELTA", Role = Role.Assistant },
                            new TextMessage { Text = "UNCAPTURED-ANSWER", Role = Role.Assistant },
                        ],
                        ct
                    )
                );
            }
        );
        var services = new MultiTurnLifecycleServices
        {
            AuditSink = new FailingAuditSink(MultiTurnAuditRecordTypes.ModelResponse),
            AuditScope = new MultiTurnAuditScope("engagement-fail", "round-response"),
        };
        await using var loop = new MultiTurnAgentLoop(
            provider,
            new FunctionRegistry(),
            "response-fail-thread",
            lifecycleServices: services
        );
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = loop.RunAsync(cts.Token);

        var first = await DrainAsync(
            loop.ExecuteRunAsync(
                new UserInput([new TextMessage { Text = "First", Role = Role.User }], InputId: "response-fail-1"),
                cts.Token
            )
        );
        var second = await DrainAsync(
            loop.ExecuteRunAsync(
                new UserInput([new TextMessage { Text = "Second", Role = Role.User }], InputId: "response-fail-2"),
                cts.Token
            )
        );

        first
            .OfType<RunCompletedMessage>()
            .Should()
            .ContainSingle()
            .Which.ErrorMessage.Should()
            .Contain("Audit capture failed for record type 'model_response'");
        first
            .OfType<TextMessage>()
            .Should()
            .NotContain(message => message.Text == "UNCAPTURED-ANSWER", "audit rejection must precede publication");
        first
            .OfType<TextUpdateMessage>()
            .Should()
            .NotContain(message => message.Text == "UNCAPTURED-DELTA", "audit rejection must cover partial output too");
        second.OfType<RunCompletedMessage>().Should().ContainSingle().Which.IsError.Should().BeTrue();
        requests.Should().HaveCount(2);
        requests[1]
            .OfType<TextMessage>()
            .Should()
            .NotContain(message => message.Text == "UNCAPTURED-ANSWER", "uncaptured output cannot become context");

        await cts.CancelAsync();
    }

    private static List<IMessage> DeserializeRequestMessages(ReadOnlyMemory<byte> content)
    {
        using var document = JsonDocument.Parse(content);
        var messages = document.RootElement.GetProperty("messages");
        return
        [
            .. messages
                .EnumerateArray()
                .Select(entry =>
                {
                    entry.GetProperty("runtime_type").GetString().Should().NotBeNullOrWhiteSpace();
                    return JsonSerializer.Deserialize<IMessage>(
                        entry.GetProperty("message").GetRawText(),
                        MessageJsonOptions
                    )!;
                }),
        ];
    }

    private static async Task<MultiTurnAgentLoop> CreateDeferredAuditLoopAsync(
        IMultiTurnAuditSink sink,
        string toolCallId
    )
    {
        var provider = new DelegatingStreamingAgent(
            (_, _, ct) =>
                Task.FromResult<IAsyncEnumerable<IMessage>>(
                    Emit(
                        [
                            new ToolCallMessage
                            {
                                FunctionName = "wait_for_result",
                                FunctionArgs = "{}",
                                ToolCallId = toolCallId,
                                Role = Role.Assistant,
                            },
                        ],
                        ct
                    )
                )
        );
        var registry = new FunctionRegistry();
        registry.AddFunction(
            new FunctionContract
            {
                Name = "wait_for_result",
                Description = "Wait externally",
                Parameters = [],
            },
            (_, _, _) => Task.FromResult<ToolHandlerResult>(new ToolHandlerResult.Deferred())
        );
        var loop = new MultiTurnAgentLoop(
            provider,
            registry,
            $"thread-{toolCallId}",
            lifecycleServices: new MultiTurnLifecycleServices
            {
                AuditSink = sink,
                AuditScope = new MultiTurnAuditScope("engagement-deferred-failure", "round-deferred-failure"),
            }
        );
        _ = loop.RunAsync();
        _ = await DrainAsync(
            loop.ExecuteRunAsync(
                new UserInput([new TextMessage { Text = "Wait", Role = Role.User }], InputId: $"input-{toolCallId}")
            )
        );
        return loop;
    }

    private static IMessage DeserializeMessage(ReadOnlySpan<byte> content) =>
        JsonSerializer.Deserialize<IMessage>(content, MessageJsonOptions)!;

    private static async Task<List<IMessage>> DrainAsync(IAsyncEnumerable<IMessage> stream)
    {
        var messages = new List<IMessage>();
        await foreach (var message in stream)
        {
            messages.Add(message);
        }

        return messages;
    }

    private static async IAsyncEnumerable<IMessage> Emit(
        IEnumerable<IMessage> messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct
    )
    {
        foreach (var message in messages)
        {
            ct.ThrowIfCancellationRequested();
            yield return message;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<IMessage> EmitThenThrow(
        IEnumerable<IMessage> messages,
        Exception failure,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct
    )
    {
        await foreach (var message in Emit(messages, ct))
        {
            yield return message;
        }

        throw failure;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class DelegatingStreamingAgent(
        Func<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken, Task<IAsyncEnumerable<IMessage>>> reply
    ) : IStreamingAgent
    {
        public Task<IEnumerable<IMessage>> GenerateReplyAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException("The multi-turn loop uses the streaming provider method.");

        public Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        ) => reply(messages, options ?? new GenerateReplyOptions(), cancellationToken);
    }

    private sealed class ControllableAuditSink : IMultiTurnAuditSink
    {
        public Exception? ToolResultFailure { get; set; }

        public CancellationTokenSource? CancelToolResultWith { get; set; }

        public ValueTask RecordAsync(ModelTurnAuditRecord record, CancellationToken cancellationToken = default)
        {
            if (record.RecordType != MultiTurnAuditRecordTypes.ToolResult)
            {
                return ValueTask.CompletedTask;
            }

            if (CancelToolResultWith is { } cancellation)
            {
                cancellation.Cancel();
                return ValueTask.FromException(new OperationCanceledException(cancellationToken));
            }

            return ToolResultFailure is { } failure ? ValueTask.FromException(failure) : ValueTask.CompletedTask;
        }
    }

    private sealed class FailingAuditSink(
        string failingRecordType,
        int? failOccurrence = null,
        Exception? failure = null
    ) : IMultiTurnAuditSink
    {
        private int _matchingRecordCount;

        public ValueTask RecordAsync(ModelTurnAuditRecord record, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (record.RecordType != failingRecordType)
            {
                return ValueTask.CompletedTask;
            }

            var occurrence = Interlocked.Increment(ref _matchingRecordCount);
            return failOccurrence == null || occurrence == failOccurrence
                ? ValueTask.FromException(failure ?? new IOException("simulated durable sink failure"))
                : ValueTask.CompletedTask;
        }
    }

    private sealed class CapturingLogger : ILogger<MultiTurnAgentLoop>
    {
        private readonly object _gate = new();
        private readonly List<Exception> _exceptions = [];

        public IReadOnlyList<Exception> Exceptions
        {
            get
            {
                lock (_gate)
                {
                    return [.. _exceptions];
                }
            }
        }

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
            if (exception == null)
            {
                return;
            }

            lock (_gate)
            {
                _exceptions.Add(exception);
            }
        }
    }

    private sealed class RecordingAuditSink : IMultiTurnAuditSink
    {
        private readonly object _gate = new();
        private readonly List<ModelTurnAuditRecord> _records = [];

        public IReadOnlyList<ModelTurnAuditRecord> Records
        {
            get
            {
                lock (_gate)
                {
                    return [.. _records];
                }
            }
        }

        public ValueTask RecordAsync(ModelTurnAuditRecord record, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _records.Add(record);
            }

            return ValueTask.CompletedTask;
        }
    }
}
