namespace LmStreaming.Sample.Models;

/// <summary>
/// Distinguishes "property omitted from the JSON body" (<see cref="IsSet"/> false, meaning "leave
/// unchanged") from "property present with a value" — including an explicit JSON <c>null</c>, which
/// is itself a meaningful tri-state value for a workspace's <c>PluginSelection</c> (legacy-all),
/// distinct from omission (unchanged).
/// Requires <see cref="OptionalJsonConverterFactory"/> via <c>[JsonConverter]</c> to populate
/// correctly — <see cref="System.Text.Json"/> never calls a converter's Read for an absent property,
/// which is exactly the mechanism this type relies on to detect omission.
/// <para>
/// INBOUND ONLY. This is a request-binding presence sentinel, not a serializable value. Writing is
/// lossy: <see cref="OptionalJsonConverter{T}"/> emits <see cref="Value"/> unconditionally, so
/// <see cref="Unset"/> serializes as <c>null</c> — turning "omitted / leave unchanged" into "explicit
/// null / clear the selection" on the way out. Never put a DTO carrying an <see cref="Optional{T}"/>
/// on an OUTBOUND request body, and never round-trip one through serialization. Suppressing an unset
/// property on write would need a custom <c>JsonTypeInfo</c> modifier; that machinery is deliberately
/// not built here because nothing outbound needs it yet.
/// </para>
/// </summary>
public readonly struct Optional<T>
{
    public Optional(T? value)
    {
        IsSet = true;
        Value = value;
    }

    /// <summary>The "property was omitted" value — the struct default, so an unassigned field is already unset.</summary>
    public static Optional<T> Unset => default;

    /// <summary>Whether the property was present in the payload at all.</summary>
    public bool IsSet { get; }

    /// <summary>The supplied value; meaningful only when <see cref="IsSet"/> is <see langword="true"/>.</summary>
    public T? Value { get; }
}
