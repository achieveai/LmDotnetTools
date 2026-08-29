using System.Text.Json;
using CodeReviewDaemon.Sample.Eval;

namespace CodeReviewDaemon.Sample.Tests.Eval;

/// <summary>
/// The one place the eval readers turn a stored payload into a value, and the one place the decision
/// "which failures cost a row rather than the sweep" is made.
/// <para>
/// It is tested directly because the interesting half of it — the <see cref="NotSupportedException"/>
/// arm — is not reachable through either caller's payload type. <see cref="JsonSerializer"/> raises
/// that exception when it has no converter for the <i>shape</i> being asked for, not when the
/// <i>data</i> is malformed, and both callers deserialise into plain records it supports. Pinning
/// the arm through a shape that does trigger it is honest about what is being proved: that the
/// handler catches it, not that a judge row can produce it.
/// </para>
/// </summary>
public class EvalArtifactJsonTests
{
    private sealed record Payload(int? Score, string Rationale);

    [Fact]
    public void A_payload_that_fits_is_returned_with_no_failure()
    {
        var value = EvalArtifactJson.TryRead<Payload>("{\"score\":7,\"rationale\":\"because\"}", out var failure);

        value.Should().Be(new Payload(7, "because"));
        failure.Should().BeNull();
    }

    /// <summary>
    /// Case-insensitivity is part of the contract, not an accident of the options object: the
    /// daemon's own writers use <c>System.Text.Json</c> defaults, so the stored keys are
    /// PascalCase, and a reader that lost this option would read every field as absent while
    /// throwing nothing at all.
    /// </summary>
    [Fact]
    public void A_payload_whose_keys_differ_only_in_case_still_fits()
    {
        var value = EvalArtifactJson.TryRead<Payload>("{\"Score\":7,\"Rationale\":\"because\"}", out _);

        value.Should().Be(new Payload(7, "because"));
    }

    [Fact]
    public void Malformed_text_is_a_read_failure_rather_than_a_throw()
    {
        var value = EvalArtifactJson.TryRead<Payload>("{ this is not json", out var failure);

        value.Should().BeNull();
        failure.Should().BeOfType<JsonException>();
    }

    /// <summary>
    /// The arm this helper exists for. <see cref="NotSupportedException"/> does not derive from
    /// <see cref="JsonException"/>, so a <c>catch (JsonException)</c> lets it past — and at the call
    /// sites that meant one unreadable row taking the whole sweep's window with it, leaving the
    /// cursor un-advanced and every later sweep re-reading the same rows.
    /// </summary>
    [Fact]
    public void An_unsupported_shape_is_a_read_failure_rather_than_a_throw()
    {
        // System.Type is refused by the serializer by design — deserialising one from untrusted
        // input is a remote type-activation primitive — and the refusal is a NotSupportedException,
        // not a JsonException. It is the cleanest reachable trigger for this arm.
        var value = EvalArtifactJson.TryRead<Type>("\"System.String\"", out var failure);

        value.Should().BeNull();
        failure
            .Should()
            .BeOfType<NotSupportedException>(
                "the arm must be the unsupported-shape one and not the malformed-text one"
            );
    }

    /// <summary>
    /// A literal <c>null</c> payload is not a failure — it deserialises successfully to nothing, and
    /// the caller's "no usable payload" branch is the same either way. Pinned so a future
    /// "failure is non-null whenever the value is null" simplification cannot quietly reclassify it.
    /// </summary>
    [Fact]
    public void A_literal_null_payload_is_nothing_rather_than_a_failure()
    {
        var value = EvalArtifactJson.TryRead<Payload>("null", out var failure);

        value.Should().BeNull();
        failure.Should().BeNull();
    }
}
