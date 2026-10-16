using DeChimex.Shared.Constants;
using FluentAssertions;
using Xunit;

namespace DeChimex.UnitTests.Shared;

/// <summary>Role-pair rules for internal (work) messaging: management talks to everyone
/// they need, staff only to management, developer only to management, and nobody else.</summary>
public class InternalMessagingTests
{
    [Theory]
    [InlineData(new[] { RoleNames.Owner }, InternalMessaging.Owner)]
    [InlineData(new[] { RoleNames.Owner, RoleNames.Manager }, InternalMessaging.Owner)]
    [InlineData(new[] { RoleNames.Manager }, InternalMessaging.Manager)]
    [InlineData(new[] { RoleNames.Manager, RoleNames.SalesStaff }, InternalMessaging.Manager)]
    [InlineData(new[] { RoleNames.SalesStaff }, InternalMessaging.Staff)]
    [InlineData(new[] { RoleNames.InventoryStaff }, InternalMessaging.Staff)]
    [InlineData(new[] { RoleNames.Designer }, InternalMessaging.Staff)]
    [InlineData(new[] { RoleNames.Developer }, InternalMessaging.Developer)]
    [InlineData(new[] { RoleNames.Customer }, null)]
    [InlineData(new string[] { }, null)]
    public void CategoryFor_reduces_roles_to_the_highest_communication_category(string[] roles, string? expected)
        => InternalMessaging.CategoryFor(roles).Should().Be(expected);

    [Fact]
    public void CategoryFor_is_case_insensitive()
        => InternalMessaging.CategoryFor(new[] { "owner" }).Should().Be(InternalMessaging.Owner);

    [Theory]
    [InlineData(InternalMessaging.Owner, InternalMessaging.Manager, true)]
    [InlineData(InternalMessaging.Owner, InternalMessaging.Staff, true)]
    [InlineData(InternalMessaging.Manager, InternalMessaging.Staff, true)]
    [InlineData(InternalMessaging.Owner, InternalMessaging.Developer, true)]
    [InlineData(InternalMessaging.Manager, InternalMessaging.Developer, true)]
    [InlineData(InternalMessaging.Staff, InternalMessaging.Staff, false)]
    [InlineData(InternalMessaging.Staff, InternalMessaging.Developer, false)]
    [InlineData(InternalMessaging.Developer, InternalMessaging.Developer, false)]
    [InlineData(InternalMessaging.Owner, InternalMessaging.Owner, false)]
    [InlineData(InternalMessaging.Manager, InternalMessaging.Manager, false)]
    public void CanCommunicate_matches_the_owner_directive(string a, string b, bool expected)
        => InternalMessaging.CanCommunicate(a, b).Should().Be(expected);

    [Fact]
    public void CanCommunicate_denies_null_and_unknown_categories()
    {
        InternalMessaging.CanCommunicate(null, InternalMessaging.Owner).Should().BeFalse();
        InternalMessaging.CanCommunicate(InternalMessaging.Owner, null).Should().BeFalse();
        InternalMessaging.CanCommunicate("Customer", InternalMessaging.Owner).Should().BeFalse();
    }

    [Fact]
    public void AllowedCounterparts_offer_only_permitted_categories()
    {
        InternalMessaging.AllowedCounterparts(InternalMessaging.Owner)
            .Should().BeEquivalentTo(InternalMessaging.Manager, InternalMessaging.Staff, InternalMessaging.Developer);
        InternalMessaging.AllowedCounterparts(InternalMessaging.Manager)
            .Should().BeEquivalentTo(InternalMessaging.Owner, InternalMessaging.Staff, InternalMessaging.Developer);
        InternalMessaging.AllowedCounterparts(InternalMessaging.Staff)
            .Should().BeEquivalentTo(InternalMessaging.Owner, InternalMessaging.Manager);
        InternalMessaging.AllowedCounterparts(InternalMessaging.Developer)
            .Should().BeEquivalentTo(InternalMessaging.Owner, InternalMessaging.Manager);
        InternalMessaging.AllowedCounterparts(null).Should().BeEmpty();
        InternalMessaging.AllowedCounterparts("Customer").Should().BeEmpty();
    }
}
