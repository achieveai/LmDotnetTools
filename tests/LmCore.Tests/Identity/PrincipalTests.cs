using AchieveAi.LmDotnetTools.LmCore.Identity;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Identity;

/// <summary>
/// Pins the normative behaviour of <see cref="Principal"/> from the P1 spec (section 3.1): the
/// attribution rule that decides which party a request's activity is written against.
/// </summary>
public class PrincipalTests
{
    private static Principal Build(PrincipalRef actor, PrincipalRef? onBehalfOf = null) =>
        new()
        {
            TenantId = "tnt_acme",
            Actor = actor,
            OnBehalfOf = onBehalfOf,
            Source = PrincipalSource.Interactive,
        };

    [Fact]
    public void EffectiveUserId_IsTheActor_WhenTheActorIsAnEndUserActingForItself()
    {
        var principal = Build(new PrincipalRef(PrincipalKind.EndUser, "tid-1:oid-1"));

        principal.EffectiveUserId.Should().Be("tid-1:oid-1");
    }

    [Fact]
    public void EffectiveUserId_PrefersOnBehalfOf_OverTheActor()
    {
        // An app acting for a human attributes to the human, never to the app.
        var principal = Build(
            new PrincipalRef(PrincipalKind.App, "app-7"),
            new PrincipalRef(PrincipalKind.EndUser, "tid-1:oid-1")
        );

        principal.EffectiveUserId.Should().Be("tid-1:oid-1");
    }

    [Fact]
    public void EffectiveUserId_PrefersAnEndUserOnBehalfOf_EvenWhenTheActorIsAlsoAnEndUser()
    {
        var principal = Build(
            new PrincipalRef(PrincipalKind.EndUser, "tid-1:actor"),
            new PrincipalRef(PrincipalKind.EndUser, "tid-1:subject")
        );

        principal.EffectiveUserId.Should().Be("tid-1:subject");
    }

    [Fact]
    public void EffectiveUserId_IsNull_ForAnAppOnlyPrincipal()
    {
        // 7.4 step 3 leans on this: an app-only principal must never match a null owner.
        var principal = Build(new PrincipalRef(PrincipalKind.App, "app-7"));

        principal.EffectiveUserId.Should().BeNull();
    }

    [Fact]
    public void EffectiveUserId_IsNull_WhenNeitherPartyIsAnEndUser()
    {
        var principal = Build(
            new PrincipalRef(PrincipalKind.Agent, "agent-1"),
            new PrincipalRef(PrincipalKind.App, "app-7")
        );

        principal.EffectiveUserId.Should().BeNull();
    }

    [Fact]
    public void Defaults_AreEmptyCollections_SoNoConsumerNeedsANullCheck()
    {
        var principal = Build(new PrincipalRef(PrincipalKind.EndUser, "tid-1:oid-1"));

        principal.DelegationChain.Should().BeEmpty();
        principal.Scopes.Should().BeEmpty();
        principal.Roles.Should().BeEmpty();
        principal.AppId.Should().BeNull();
        principal.OnBehalfOf.Should().BeNull();
    }
}
