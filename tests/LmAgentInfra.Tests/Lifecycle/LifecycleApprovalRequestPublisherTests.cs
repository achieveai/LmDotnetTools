using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Lifecycle;
using AchieveAi.LmDotnetTools.LmAgentInfra.Webhooks;
using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmLifecycle.Approval;
using AchieveAi.LmDotnetTools.LmLifecycle.Serialization;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Lifecycle;

/// <summary>
/// ADR 0003 + ADR 0005 — the leg that carries an approval request to one approver.
/// </summary>
/// <remarks>
/// <para>
/// Everything here turns on one asymmetry: this publisher's caller,
/// <see cref="RemoteToolApprovalGate"/>, reads a throw as "that approver was not reached" and moves
/// on, but reads an <see cref="OperationCanceledException"/> as "the run is going away" and abandons
/// the approval entirely. So <em>which</em> exception comes out is the contract, not an
/// implementation detail — a slow endpoint that surfaced as cancellation would look exactly like an
/// abandoned run, and every remaining approver would go unasked.
/// </para>
/// <para>
/// Nothing sleeps: the attempt timeout is expired by hand through <see cref="ManualTimeProvider"/>,
/// which is the reason the publisher takes a clock at all.
/// </para>
/// </remarks>
public sealed class LifecycleApprovalRequestPublisherTests
{
    private const string CallbackHost = "approver.example.com";
    private const string Callback = $"https://{CallbackHost}/approvals";
    private const string AppId = "app-a";
    private const string Secret = "0123456789abcdef0123456789abcdef";
    private const string ArgumentsJson = """{"path":"/etc/hosts"}""";

    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_destination_that_left_the_allow_list_is_refused_before_anything_is_sent()
    {
        var harness = new Harness(options => options.AllowedCallbackHosts = ["elsewhere.example.com"]);

        var act = async () => await harness.Publisher.PublishAsync(Subscriber(), Request(), CancellationToken.None);

        // The third of ADR 0005's three re-authorization moments. The subscription was admitted under
        // whatever allow-list was configured then; an operator narrowing it around an incident must
        // stop tool arguments reaching the host they removed, without having to hunt down and delete
        // every subscription that already named it.
        _ = await act.Should().ThrowAsync<InvalidOperationException>();
        harness.Sender.Attempts.Should().BeEmpty();
    }

    [Fact]
    public async Task The_attempt_timeout_surfaces_as_a_refusal_rather_than_a_cancellation()
    {
        var harness = new Harness();
        harness.Sender.Block = true;

        var publish = harness.Publisher.PublishAsync(Subscriber(), Request(), CancellationToken.None).AsTask();
        await harness.Sender.WaitForAttemptsAsync(1);

        harness.Clock.Advance(harness.Options.AttemptTimeout);

        var act = async () => await publish;

        // OperationCanceledException does not derive from InvalidOperationException, so this
        // assertion is also the one that matters: the gate must not mistake a slow approver for a
        // cancelled run.
        var thrown = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        thrown.Message.Should().Contain(harness.Options.AttemptTimeout.ToString());
    }

    [Fact]
    public async Task Cancelling_the_run_still_comes_out_as_cancellation()
    {
        var harness = new Harness();
        harness.Sender.Block = true;

        using var cts = new CancellationTokenSource();
        var publish = harness.Publisher.PublishAsync(Subscriber(), Request(), cts.Token).AsTask();
        await harness.Sender.WaitForAttemptsAsync(1);

        await cts.CancelAsync();

        // The mirror image of the previous test. Converting this one too would leave the gate
        // dutifully asking the next approver on a run that no longer exists.
        var act = async () => await publish;
        _ = await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData(LifecycleDeliveryOutcome.Retryable)]
    [InlineData(LifecycleDeliveryOutcome.Permanent)]
    [InlineData(LifecycleDeliveryOutcome.Gone)]
    public async Task An_approver_that_does_not_accept_the_request_counts_as_unasked(LifecycleDeliveryOutcome outcome)
    {
        var harness = new Harness();
        harness.Sender.Result = outcome switch
        {
            LifecycleDeliveryOutcome.Retryable => LifecycleDeliveryResult.Retryable("transport"),
            LifecycleDeliveryOutcome.Permanent => LifecycleDeliveryResult.Permanent("http_status", 400),
            _ => LifecycleDeliveryResult.Gone(410),
        };

        var act = async () => await harness.Publisher.PublishAsync(Subscriber(), Request(), CancellationToken.None);

        // Retryable is included deliberately. This publisher does not retry — its caller is already
        // looping over approvers, and an approval request carries its own expiry, so a retry that
        // outlives it accomplishes nothing but delay for the approvers that are still healthy.
        var thrown = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        thrown.Message.Should().Contain(outcome.ToString());
        harness.Sender.Attempts.Should().ContainSingle();
    }

    [Fact]
    public async Task Each_delivery_carries_its_own_identity_distinct_from_the_request()
    {
        var harness = new Harness();
        var request = Request();

        await harness.Publisher.PublishAsync(Subscriber("sub-1"), request, CancellationToken.None);
        await harness.Publisher.PublishAsync(Subscriber("sub-2"), request, CancellationToken.None);

        var ids = harness.Sender.Attempts.Select(attempt => attempt.DeliveryId).ToArray();

        // The request id is what an approver answers about and is the same for all of them; the
        // delivery id names one POST and is what the receiver's replay cache keys on. Sharing either
        // way would make the second approver's delivery look like a replay of the first's and be
        // dropped unanswered.
        ids.Should().OnlyHaveUniqueItems();
        ids.Should().NotContain(request.RequestId);
        ids.Should().OnlyContain(id => !string.IsNullOrWhiteSpace(id));
    }

    [Fact]
    public async Task The_body_is_the_request_itself_as_the_approver_will_read_it()
    {
        var harness = new Harness();
        var request = Request();

        await harness.Publisher.PublishAsync(Subscriber(), request, CancellationToken.None);

        var sent = JsonSerializer.Deserialize<ToolApprovalRequest>(
            harness.Sender.Attempts.Should().ContainSingle().Which.Body,
            LifecycleSerializer.Options
        );

        // No envelope, no re-shaping. The gate has already tailored this instance for this approver —
        // notably whether Arguments is present at all — and anything added here would be added behind
        // the redaction decision that produced it.
        sent.Should().NotBeNull();
        sent!.RequestId.Should().Be(request.RequestId);
        sent.ToolName.Should().Be(request.ToolName);
        sent.Arguments.Should().Be(request.Arguments);
        sent.ExpiresAt.Should().Be(request.ExpiresAt);
    }

    [Fact]
    public async Task A_delivery_is_signed_with_the_subscriber_own_key()
    {
        var harness = new Harness();
        var subscriber = Subscriber();

        await harness.Publisher.PublishAsync(subscriber, Request(), CancellationToken.None);

        // The publisher hands the whole subscription to the sender rather than a URL, because the
        // sender signs with that subscription's secret. Passing an address alone would leave the
        // approver unable to tell this request from anything else that can reach its endpoint.
        harness.Sender.Attempts.Should().ContainSingle().Which.Subscription.Should().BeSameAs(subscriber);
    }

    private static ToolApprovalRequest Request() =>
        new()
        {
            RequestId = "req-1",
            ThreadId = "thread-1",
            RunId = "run-1",
            GenerationId = "gen-1",
            ToolCallId = "call-1",
            ToolName = "write_file",
            ArgumentsHash = "sha256:abc",
            Arguments = ArgumentsJson,
            ExpiresAt = Now.AddMinutes(5),
        };

    private static LifecycleSubscription Subscriber(string id = "sub-1") =>
        new(
            id,
            LifecycleOwnerKey.ForAppId(AppId),
            AppId,
            new Uri(Callback),
            new WebhookSigningSecret(Secret),
            [LifecycleCapabilities.ToolApprovalDecide],
            [],
            Now
        );

    private sealed class Harness
    {
        internal Harness(Action<LifecycleDeliveryOptions>? configure = null)
        {
            Options = new LifecycleDeliveryOptions { Enabled = true, AllowedCallbackHosts = [CallbackHost] };
            configure?.Invoke(Options);
            Options.Validate();

            Clock = new ManualTimeProvider(Now);
            Sender = new RecordingSender();
            Publisher = new LifecycleApprovalRequestPublisher(
                Sender,
                Options,
                Clock,
                NullLogger<LifecycleApprovalRequestPublisher>.Instance
            );
        }

        internal LifecycleDeliveryOptions Options { get; }

        internal ManualTimeProvider Clock { get; }

        internal RecordingSender Sender { get; }

        internal LifecycleApprovalRequestPublisher Publisher { get; }
    }

    /// <summary>Stands in for the signing transport, and can be made to hang so a timeout has something to expire.</summary>
    private sealed class RecordingSender : ILifecycleDeliverySender
    {
        private readonly Lock _sync = new();
        private readonly List<Attempt> _attempts = [];
        private readonly Gate _gate = new();

        internal bool Block { get; set; }

        internal LifecycleDeliveryResult Result { get; set; } = LifecycleDeliveryResult.Succeeded(202);

        internal IReadOnlyList<Attempt> Attempts
        {
            get
            {
                lock (_sync)
                {
                    return [.. _attempts];
                }
            }
        }

        public async Task<LifecycleDeliveryResult> SendAsync(
            LifecycleSubscription subscription,
            string deliveryId,
            ReadOnlyMemory<byte> body,
            CancellationToken cancellationToken
        )
        {
            lock (_sync)
            {
                _attempts.Add(new Attempt(subscription, deliveryId, body.ToArray()));
            }

            _gate.Signal();

            if (Block)
            {
                // Infinite, so no wall clock is involved: this task can only end by cancellation,
                // which is precisely what the test is about to cause.
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return Result;
        }

        internal Task WaitForAttemptsAsync(int count) =>
            _gate.WaitAsync(() =>
            {
                lock (_sync)
                {
                    return _attempts.Count >= count;
                }
            });

        internal sealed record Attempt(LifecycleSubscription Subscription, string DeliveryId, byte[] Body);
    }
}
