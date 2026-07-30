using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmLifecycle.Serialization;

/// <summary>
/// Encodes every timestamp in the lifecycle contract as UTC in a single fixed ISO 8601 form.
/// </summary>
/// <remarks>
/// <para>
/// The wire format is <c>yyyy-MM-ddTHH:mm:ss.fffffffZ</c> — always seven fractional digits, always
/// the <c>Z</c> designator, never a numeric offset.
/// </para>
/// <para>
/// This exists for determinism, not aesthetics. Two <see cref="DateTimeOffset"/> values denoting
/// the same instant in different offsets are equal, and the default encoding would render them as
/// different bytes. Since a delivery is signed over its bytes and a retry must re-send an identical
/// body, "equal values encode identically" has to hold. Normalizing to UTC and pinning the
/// fractional precision is what makes it hold, on both target frameworks.
/// </para>
/// </remarks>
public sealed class CanonicalTimestampConverter : JsonConverter<DateTimeOffset>
{
    private const string CanonicalFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    /// <inheritdoc />
    public override DateTimeOffset Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException(
                $"Expected an ISO 8601 timestamp string but found {reader.TokenType}."
            );
        }

        // Accept any valid ISO 8601 instant on the way in — a peer may legitimately send an offset
        // other than Z. Normalizing here means a round trip through this type always emits the
        // canonical form regardless of what arrived.
        if (!reader.TryGetDateTimeOffset(out var value))
        {
            throw new JsonException("Value is not a valid ISO 8601 timestamp.");
        }

        return value.ToUniversalTime();
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer,
        DateTimeOffset value,
        JsonSerializerOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStringValue(
            value.ToUniversalTime().ToString(CanonicalFormat, CultureInfo.InvariantCulture)
        );
    }
}
