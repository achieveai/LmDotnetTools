using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmLifecycle.Approval;
using AchieveAi.LmDotnetTools.LmLifecycle.Payloads;

namespace AchieveAi.LmDotnetTools.LmLifecycle.Serialization;

/// <summary>
/// The single authority for how lifecycle types are encoded.
/// </summary>
/// <remarks>
/// <para>
/// Property names, discriminators, timestamp format, null handling, and the resulting UTF-8 bytes
/// are all decided here and nowhere else. Every property name is pinned with an explicit
/// <see cref="JsonPropertyNameAttribute"/> rather than derived from a naming policy, so renaming a
/// C# member can never silently change the wire format.
/// </para>
/// <para>
/// This is the only use of <c>System.Text.Json</c> source generation in the repository, which
/// otherwise serializes reflectively. The exception is deliberate: the encoder must be
/// deterministic — the same value must produce the same bytes on every call and on both
/// <c>net8.0</c> and <c>net9.0</c> — because a signed delivery is signed over its bytes and a retry
/// must re-send an identical body. Golden-fixture tests assert the exact bytes on both frameworks,
/// so drift in the encoder fails the build rather than corrupting signatures in production.
/// </para>
/// <para>
/// Nulls are omitted rather than written. Absent and null are therefore the same thing on the wire;
/// an empty collection is written as <c>[]</c> and remains distinct from absent.
/// </para>
/// <para>
/// <b>Every serialized member declares <c>set</c>, never <c>init</c>, and that is load-bearing.</b>
/// Source generation cannot assign an init-only property, so it treats each one as a constructor
/// parameter and builds the object from a full argument list. A member the JSON omits is then
/// assigned <c>default</c> — overwriting the property's declared initializer. The observable effect
/// is that a decoded payload's non-nullable <see cref="string"/> members come back <see
/// langword="null"/>, breaking their own nullable annotations and defaulting a consumer straight
/// into a <see cref="NullReferenceException"/>. With <c>set</c>, the generator emits a plain object
/// creator, initializers run, and an absent member keeps its declared default. Changing these
/// members back to <c>init</c> reintroduces the bug silently.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Default,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false
)]
[JsonSerializable(typeof(LifecycleEventEnvelope))]
[JsonSerializable(typeof(LifecycleDeliveryEnvelope))]
[JsonSerializable(typeof(LifecycleCorrelation))]
[JsonSerializable(typeof(RunStartedPayload))]
[JsonSerializable(typeof(ContextLoadedPayload))]
[JsonSerializable(typeof(TurnCompletedPayload))]
[JsonSerializable(typeof(ToolCompletedPayload))]
[JsonSerializable(typeof(RunCompletedPayload))]
[JsonSerializable(typeof(SandboxCreatedPayload))]
[JsonSerializable(typeof(ToolApprovalRequest))]
[JsonSerializable(typeof(ToolApprovalDecision))]
[JsonSerializable(typeof(JsonElement))]
public partial class LifecycleJsonContext : JsonSerializerContext { }
