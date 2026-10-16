using DeChimex.Domain.Enums;
using DeChimex.Shared;
using FluentAssertions;
using Xunit;

namespace DeChimex.UnitTests.Shared;

/// <summary>
/// Enum values shown to users render as separate readable words (owner directive
/// 2026-10-07): InProgress-style PascalCase becomes "In Progress"-style wording,
/// for enums and for strings carrying enum names alike.
/// </summary>
public class EnumDisplayTests
{
    [Theory]
    [InlineData(OrderStatus.PendingPayment, "Pending Payment")]
    [InlineData(OrderStatus.InProduction, "In Production")]
    [InlineData(OrderStatus.AwaitingProcurement, "Awaiting Procurement")]
    [InlineData(OrderStatus.OutForDelivery, "Out For Delivery")]
    [InlineData(OrderStatus.RequirementsConfirmed, "Requirements Confirmed")]
    [InlineData(OrderStatus.Paid, "Paid")]
    [InlineData(PaymentStatus.Succeeded, "Succeeded")]
    [InlineData(InventoryTransactionType.ReleaseReservation, "Release Reservation")]
    public void Enums_render_as_separate_words(object value, string expected)
        => EnumDisplay.Words(value).Should().Be(expected);

    [Theory]
    [InlineData("InProgress", "In Progress")]
    [InlineData("PendingPayment", "Pending Payment")]
    [InlineData("AmountMismatch", "Amount Mismatch")]
    [InlineData("Paid", "Paid")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Strings_carrying_enum_names_render_as_separate_words(string? value, string expected)
        => EnumDisplay.Words(value).Should().Be(expected);
}
