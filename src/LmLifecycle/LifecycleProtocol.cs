namespace AchieveAi.LmDotnetTools.LmLifecycle;

/// <summary>
/// Protocol version constants and major-version negotiation for the lifecycle wire contract.
/// </summary>
/// <remarks>
/// <para>
/// Producers and subscribers are upgraded independently. Rather than discovering an
/// incompatibility per event at runtime, peers agree on a shared protocol major
/// <em>at registration</em>; an incompatible peer is refused before any delivery is attempted.
/// </para>
/// <para>
/// Within a major version, fields may be <b>added</b> and are optional. Tightening a field's
/// requiredness or nullability, removing a field, or changing the meaning of an existing value
/// requires a new major.
/// </para>
/// </remarks>
public static class LifecycleProtocol
{
    /// <summary>
    /// The protocol major this build produces. Every envelope this assembly creates carries this
    /// value in <see cref="LifecycleEventEnvelope.SchemaMajor"/>.
    /// </summary>
    public const int CurrentMajor = 1;

    /// <summary>
    /// The protocol majors this build can both produce and consume, in ascending order.
    /// </summary>
    /// <remarks>
    /// A build supporting more than one major can interoperate with a peer one version behind.
    /// This build supports exactly <see cref="CurrentMajor"/>.
    /// </remarks>
    public static IReadOnlyList<int> SupportedMajors { get; } = [CurrentMajor];

    /// <summary>
    /// Determines whether this build can exchange events with a peer supporting
    /// <paramref name="peerMajor"/>.
    /// </summary>
    /// <param name="peerMajor">The protocol major advertised by the peer.</param>
    /// <returns><see langword="true"/> when the major is supported by this build.</returns>
    public static bool IsSupported(int peerMajor) => SupportedMajors.Contains(peerMajor);

    /// <summary>
    /// Negotiates the protocol major two peers will use, choosing the highest major both support.
    /// </summary>
    /// <param name="producerSupported">Majors the producing side supports.</param>
    /// <param name="subscriberSupported">Majors the subscribing side supports.</param>
    /// <param name="agreedMajor">
    /// When this method returns <see langword="true"/>, the highest common major; otherwise
    /// <c>0</c>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the peers share at least one major; <see langword="false"/>
    /// when they share none, in which case registration must be refused rather than allowing
    /// delivery to fail per event.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="producerSupported"/> or <paramref name="subscriberSupported"/> is
    /// <see langword="null"/>.
    /// </exception>
    public static bool TryNegotiate(
        IEnumerable<int> producerSupported,
        IEnumerable<int> subscriberSupported,
        out int agreedMajor
    )
    {
        ArgumentNullException.ThrowIfNull(producerSupported);
        ArgumentNullException.ThrowIfNull(subscriberSupported);

        var subscriberSet = new HashSet<int>(subscriberSupported);
        agreedMajor = 0;
        var found = false;

        foreach (var major in producerSupported)
        {
            if (subscriberSet.Contains(major) && (!found || major > agreedMajor))
            {
                agreedMajor = major;
                found = true;
            }
        }

        return found;
    }

    /// <summary>
    /// Negotiates against this build's <see cref="SupportedMajors"/> as the producing side.
    /// </summary>
    /// <param name="subscriberSupported">Majors the subscribing side supports.</param>
    /// <param name="agreedMajor">The highest common major, or <c>0</c> when there is none.</param>
    /// <returns><see langword="true"/> when the peers share at least one major.</returns>
    public static bool TryNegotiate(IEnumerable<int> subscriberSupported, out int agreedMajor) =>
        TryNegotiate(SupportedMajors, subscriberSupported, out agreedMajor);
}
