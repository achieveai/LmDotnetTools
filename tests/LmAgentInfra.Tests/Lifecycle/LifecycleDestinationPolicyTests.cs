using System.Net;
using AchieveAi.LmDotnetTools.LmAgentInfra.Lifecycle;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Lifecycle;

/// <summary>
/// ADR 0005 — callback egress, and specifically the half of it a name cannot express.
/// <para>
/// The allow-list authorizes a <em>name</em>, and the mapping from a name to an address belongs to
/// whoever runs that name's DNS — which, for a subscriber-supplied host, is the subscriber. So a
/// name-only check is a promise about a string: an allow-listed host repointed at
/// <c>169.254.169.254</c> or at a service on this machine's own loopback between registration and
/// delivery would still be dialled, with a signed body carrying conversation content or a tool's
/// arguments. Refusing the <i>address behind</i> the name is what makes the allow-list a statement
/// about a machine, and the address is only knowable at the moment of connection, which is why the
/// same rule is re-applied there rather than cached from registration.
/// </para>
/// <para>
/// These are the rules in isolation. That they are actually reached on a real socket — and reached
/// again on each connection rather than once — is asserted in
/// <see cref="LifecycleHostingExtensionsTests"/>, which dials a live server through the configured
/// delivery client.
/// </para>
/// </summary>
public sealed class LifecycleDestinationPolicyTests
{
    private const string AllowedHost = "callbacks.example.com";

    [Theory]
    // The host itself. A callback that reaches loopback is reaching services that were never exposed
    // — admin ports, sidecars, the delivery host's own control plane.
    [InlineData("127.0.0.1")]
    [InlineData("127.1.2.3")] // the whole /8, not just the canonical address
    [InlineData("::1")]
    // The mapped spellings. `::ffff:127.0.0.1` *is* loopback, and a check that tested the v6 form
    // without unwrapping it would answer that it is not — a single-cast bypass of every rule below.
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:10.0.0.1")]
    // Link-local, where every major cloud puts its instance metadata service. This is the single
    // most valuable address to an SSRF attacker: it hands out role credentials to anything that asks.
    [InlineData("169.254.169.254")]
    [InlineData("169.254.0.1")]
    // RFC 1918, i.e. whatever else is on this host's network.
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.1")]
    [InlineData("100.64.0.1")] // carrier-grade NAT
    [InlineData("100.127.255.255")]
    // IPv6 private space: unique-local, link-local, and site-local.
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("fe80::1")]
    [InlineData("fec0::1")]
    public void Space_a_callback_has_no_business_reaching_is_refused(string address)
    {
        LifecycleDestinationPolicy.IsAllowedAddress(IPAddress.Parse(address), Options()).Should().BeFalse();
    }

    [Theory]
    // The addresses immediately outside each refused range. Without these the ranges above could be
    // implemented as "anything vaguely similar" and still pass — an allow-list that refuses the whole
    // internet is not a security property, it is an outage.
    [InlineData("11.0.0.1")] // just past 10.0.0.0/8
    [InlineData("126.255.255.255")] // just before 127.0.0.0/8
    [InlineData("128.0.0.1")] // just past it
    [InlineData("169.253.255.255")] // just before 169.254.0.0/16
    [InlineData("169.255.0.1")] // just past it
    [InlineData("172.15.255.255")] // just before 172.16.0.0/12
    [InlineData("172.32.0.1")] // just past it
    [InlineData("192.167.255.255")] // just before 192.168.0.0/16
    [InlineData("192.169.0.1")] // just past it
    [InlineData("100.63.255.255")] // just before 100.64.0.0/10
    [InlineData("100.128.0.1")] // just past it
    [InlineData("223.255.255.255")] // just before the 224.0.0.0/4 multicast block
    [InlineData("203.0.113.5")]
    [InlineData("2606:4700::1111")]
    [InlineData("2001:db8::1")]
    public void An_ordinary_public_address_is_dialled(string address)
    {
        LifecycleDestinationPolicy.IsAllowedAddress(IPAddress.Parse(address), Options()).Should().BeTrue();
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.255")]
    [InlineData("ff02::1")]
    public void Addresses_that_are_never_a_destination_are_refused_even_where_private_space_is_open(string address)
    {
        // The escape hatch below exists for a subscriber genuinely on this machine or its network.
        // Neither of these is that: nothing answers on the unspecified address, and a multicast
        // delivery is a delivery of signed conversation content to an unknown set of listeners. So
        // these sit outside the hatch rather than inside it.
        var open = Options(allowPrivate: true);

        LifecycleDestinationPolicy.IsAllowedAddress(IPAddress.Parse(address), open).Should().BeFalse();
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("fd12:3456::1")]
    public void Opening_private_space_admits_it_wholesale(string address)
    {
        // Development and single-machine deployments are real, so the hatch exists — but it is one
        // switch that opens all of it, including the metadata endpoint. Anything narrower would read
        // as a considered policy while still handing out role credentials, and an operator setting
        // this should be told the whole truth by the code rather than by a comment.
        LifecycleDestinationPolicy
            .IsAllowedAddress(IPAddress.Parse(address), Options(allowPrivate: true))
            .Should()
            .BeTrue();
    }

    [Fact]
    public void A_literal_private_address_is_refused_at_registration_rather_than_at_connect()
    {
        // A name has to be re-checked on every connect because what it resolves to can change. A
        // literal cannot change, so it is settled once, before the subscription is ever persisted —
        // and it is settled even when an operator has allow-listed the literal, because the
        // allow-list is about which destinations are wanted, not about which are reachable.
        var options = Options(hosts: ["10.0.0.1"]);

        LifecycleDestinationPolicy
            .Evaluate(new Uri("https://10.0.0.1/hook"), options)
            .Should()
            .Be(LifecycleDestinationVerdict.AddressNotAllowed);
    }

    [Fact]
    public void An_address_verdict_is_distinct_from_a_name_verdict()
    {
        // Two different operator problems. HostNotAllowed is "you forgot to add this"; the fix is a
        // configuration line. AddressNotAllowed is "you added it and it points somewhere it may not
        // go"; adding another line will not fix it, and reporting both the same way would send an
        // operator round that loop. It is also the shape a rebinding attempt takes in the logs.
        LifecycleDestinationPolicy
            .Evaluate(new Uri("https://10.0.0.1/hook"), Options())
            .Should()
            .Be(
                LifecycleDestinationVerdict.HostNotAllowed,
                "the name is checked first, so an un-allow-listed literal never reaches the address rule"
            );

        LifecycleDestinationPolicy
            .Evaluate(new Uri("https://203.0.113.5/hook"), Options(hosts: ["203.0.113.5"]))
            .Should()
            .Be(LifecycleDestinationVerdict.Allowed, "a public literal is an ordinary destination");
    }

    [Fact]
    public void A_name_is_admitted_without_being_resolved()
    {
        // Registration deliberately does not resolve. Resolving here would decide the question at the
        // one moment its answer is worth least — the subscriber can repoint the name a second later —
        // while making registration depend on DNS being up, and letting a name that resolves fine now
        // be permanently rejected because of a transient failure.
        LifecycleDestinationPolicy
            .Evaluate(new Uri($"https://{AllowedHost}/hook"), Options())
            .Should()
            .Be(LifecycleDestinationVerdict.Allowed);
    }

    [Fact]
    public void Both_spellings_of_an_internationalized_host_are_one_destination()
    {
        var unicode = new Uri("https://ünicode.example/hook");
        var punycode = new Uri($"https://{unicode.IdnHost}/hook");

        unicode
            .Host.Should()
            .NotBe(unicode.IdnHost, "this test is vacuous unless the two spellings really are different strings");

        // Matching only the spelling in the URL would make an allow-list entry admit or refuse the
        // same machine depending on how the subscriber happened to type it.
        foreach (var entry in new[] { unicode.Host, unicode.IdnHost })
        {
            var options = Options(hosts: [entry]);

            LifecycleDestinationPolicy
                .Evaluate(unicode, options)
                .Should()
                .Be(LifecycleDestinationVerdict.Allowed, "allow-listed as {0}", entry);
            LifecycleDestinationPolicy
                .Evaluate(punycode, options)
                .Should()
                .Be(LifecycleDestinationVerdict.Allowed, "allow-listed as {0}", entry);
        }

        // And one destination to the quarantine too. If these were two keys, re-registering under the
        // other spelling would walk straight out of a quarantine the first spelling earned.
        LifecycleDestinationPolicy
            .DestinationKey(unicode)
            .Should()
            .Be(LifecycleDestinationPolicy.DestinationKey(punycode));
    }

    [Theory]
    [InlineData("")] // an empty line in the configured list
    [InlineData("a..b")] // an empty label, i.e. a typo
    [InlineData("xn--")] // the ACE prefix with nothing behind it
    [InlineData("host_with_underscores.example")] // legal in some resolvers, not a valid IDN label
    public void An_allow_list_entry_that_cannot_be_canonicalized_refuses_rather_than_throws(string malformed)
    {
        // Punycode canonicalization is the only thing standing between two spellings of one host, so
        // it runs over every entry — including the ones an operator got wrong. It has to answer for
        // those rather than throw: this is called from IsAuthorized, which is re-run before *every*
        // delivery attempt, and an exception there would escape into a delivery worker and take out a
        // subscriber's queue over a typo in someone else's configuration line.
        var refuse = () =>
            LifecycleDestinationPolicy.Evaluate(new Uri($"https://{AllowedHost}/hook"), Options(hosts: [malformed]));

        // Failing closed is the other half: a mistyped entry matches nothing, so it cannot widen the
        // allow-list by accident. Only an entry that means the host admits the host.
        refuse.Should().NotThrow().Which.Should().Be(LifecycleDestinationVerdict.HostNotAllowed);

        // And it poisons nothing around it. One bad line in a list is a bad line, not an outage for
        // every destination configured beside it.
        LifecycleDestinationPolicy
            .Evaluate(new Uri($"https://{AllowedHost}/hook"), Options(hosts: [malformed, AllowedHost]))
            .Should()
            .Be(LifecycleDestinationVerdict.Allowed);
    }

    private static LifecycleDeliveryOptions Options(bool allowPrivate = false, string[]? hosts = null) =>
        new()
        {
            Enabled = true,
            AllowedCallbackHosts = hosts ?? [AllowedHost],
            AllowPrivateCallbackAddresses = allowPrivate,
        };
}
