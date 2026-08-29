using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmLifecycle.Tests;

/// <summary>
/// Covers major-version negotiation.
/// </summary>
/// <remarks>
/// Incompatibility is settled once, at registration, rather than rediscovered per event. A peer this
/// build cannot talk to must be refused up front — a subscriber that registers successfully and then
/// silently discards every event is worse than one that never registered.
/// </remarks>
public class LifecycleProtocolTests
{
    [Fact]
    public void This_build_supports_the_major_it_produces()
    {
        LifecycleProtocol.SupportedMajors.Should().Contain(LifecycleProtocol.CurrentMajor);
        LifecycleProtocol.IsSupported(LifecycleProtocol.CurrentMajor).Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void A_major_this_build_does_not_implement_is_not_supported(int peerMajor)
    {
        LifecycleProtocol.IsSupported(peerMajor).Should().BeFalse();
    }

    [Fact]
    public void Peers_sharing_a_major_agree_on_it()
    {
        LifecycleProtocol.TryNegotiate([LifecycleProtocol.CurrentMajor], out var agreed).Should().BeTrue();
        agreed.Should().Be(LifecycleProtocol.CurrentMajor);
    }

    [Fact]
    public void Peers_sharing_several_majors_agree_on_the_highest()
    {
        LifecycleProtocol.TryNegotiate([1, 2, 3], [2, 3, 4], out var agreed).Should().BeTrue();

        agreed.Should().Be(3);
    }

    [Fact]
    public void Negotiation_is_order_independent()
    {
        LifecycleProtocol.TryNegotiate([3, 1, 2], [4, 2, 3], out var forward).Should().BeTrue();
        LifecycleProtocol.TryNegotiate([2, 3, 1], [3, 2, 4], out var reverse).Should().BeTrue();

        forward.Should().Be(reverse).And.Be(3);
    }

    [Fact]
    public void A_peer_one_version_behind_still_agrees_on_the_older_major()
    {
        LifecycleProtocol.TryNegotiate([1, 2], [1], out var agreed).Should().BeTrue();

        agreed.Should().Be(1);
    }

    [Fact]
    public void Peers_sharing_no_major_refuse_to_agree()
    {
        LifecycleProtocol.TryNegotiate([1], [2, 3], out var agreed).Should().BeFalse();

        agreed.Should().Be(0);
    }

    [Fact]
    public void A_subscriber_on_an_incompatible_major_is_refused_by_this_build()
    {
        LifecycleProtocol.TryNegotiate([LifecycleProtocol.CurrentMajor + 1], out var agreed).Should().BeFalse();
        agreed.Should().Be(0);
    }

    [Fact]
    public void An_empty_advertisement_agrees_on_nothing()
    {
        LifecycleProtocol.TryNegotiate([], out var agreed).Should().BeFalse();

        agreed.Should().Be(0);
    }

    [Fact]
    public void Negotiating_against_nothing_is_a_programming_error_not_a_refusal()
    {
        var negotiate = () => LifecycleProtocol.TryNegotiate(null!, out _);

        negotiate.Should().Throw<ArgumentNullException>();
    }
}
