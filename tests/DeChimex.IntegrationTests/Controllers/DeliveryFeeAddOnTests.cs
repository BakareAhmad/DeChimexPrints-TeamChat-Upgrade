using System.Net;
using System.Text.RegularExpressions;
using DeChimex.Domain.Entities;
using DeChimex.Persistence.Context;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Delivery Fee is an OPTIONAL, admin-configured add-on - there is no static/hard-coded
/// delivery charge anywhere. These tests pin the contract:
///   * not selected  -> order DeliveryFee = 0 and totals exclude delivery;
///   * selected      -> the admin-configured/entered value is applied exactly once and is
///                      included in the grand total (the Paystack-payable amount);
///   * the seeded default is 0 (no hidden fallback fee).
/// The DeliveryFee stays a plain Money field on the existing Order aggregate - no new
/// entity or parallel add-on mechanism was introduced. Each test boots its own factory so
/// the in-memory database (and the delivery setting) is isolated per test.
/// </summary>
public class DeliveryFeeAddOnTests
{
    private const decimal SellingPrice = 2000m;

    [Fact]
    public async Task Seeded_delivery_default_is_zero_not_a_static_fee()
    {
        using var factory = new CustomWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var setting = await db.ApplicationSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == "delivery.default_fee");

        setting.Should().NotBeNull("the seeder registers the delivery add-on setting");
        setting!.Value.Should().Be("0",
            "delivery is an optional add-on: absent an owner-configured value there is no charge");
    }

    [Fact]
    public async Task Pickup_order_has_zero_delivery_and_total_excludes_it()
    {
        using var factory = new CustomWebApplicationFactory();
        var productId = await SeedProductAsync(factory);
        var client = await LoginAsOwnerAsync(factory);

        var orderId = await CreateOrderAsync(client, productId, delivery: false, deliveryFee: "0");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var order = await db.Orders.AsNoTracking().Include(o => o.Items).Include(o => o.Services).Include(o => o.CustomLines).SingleAsync(o => o.Id == orderId);

        order.DeliveryFee.Amount.Should().Be(0m, "pickup never carries a delivery charge");
        order.GrandTotal.Amount.Should().Be(2 * SellingPrice,
            "the total is just the line subtotal when delivery is not selected");
    }

    [Fact]
    public async Task Delivery_add_on_applies_configured_value_exactly_once_in_total()
    {
        const decimal configuredFee = 2500m;
        using var factory = new CustomWebApplicationFactory();
        await SetDeliveryDefaultAsync(factory, configuredFee);
        var productId = await SeedProductAsync(factory);
        var client = await LoginAsOwnerAsync(factory);

        var page = await (await client.GetAsync("/admin/orders/new")).Content.ReadAsStringAsync();
        var prefill = Regex.Match(page, "name=\"DeliveryFee\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        decimal.Parse(prefill).Should().Be(configuredFee,
            "the ready-made Delivery Fee add-on is prefilled with the configured value");

        var orderId = await CreateOrderAsync(client, productId, delivery: true, deliveryFee: configuredFee.ToString("0.##"));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var order = await db.Orders.AsNoTracking().Include(o => o.Items).Include(o => o.Services).Include(o => o.CustomLines).SingleAsync(o => o.Id == orderId);

        order.DeliveryFee.Amount.Should().Be(configuredFee, "the configured add-on value is stored");
        order.GrandTotal.Amount.Should().Be(2 * SellingPrice + configuredFee,
            "delivery is included exactly once in the grand total (the payable amount)");
    }

    [Fact]
    public async Task New_order_form_has_no_hardcoded_delivery_fee()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = await LoginAsOwnerAsync(factory);
        var page = await (await client.GetAsync("/admin/orders/new")).Content.ReadAsStringAsync();

        var value = Regex.Match(page, "name=\"DeliveryFee\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        decimal.Parse(value).Should().Be(0m,
            "with no configured fee the add-on defaults to 0 - never a static amount");
        page.Should().Contain("optional add-on", "the form must describe delivery as an optional add-on");
    }

    private static async Task<Guid> SeedProductAsync(CustomWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var category = Category.Create("Delivery AddOn Category " + Guid.NewGuid().ToString("N"));
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        var product = Product.Create("Delivery AddOn Mug", "DA-" + Guid.NewGuid().ToString("N")[..8],
            null, category.Id, SellingPrice);
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private static async Task SetDeliveryDefaultAsync(CustomWebApplicationFactory factory, decimal value)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var setting = await db.ApplicationSettings.FirstOrDefaultAsync(s => s.Key == "delivery.default_fee");
        if (setting is null)
            db.ApplicationSettings.Add(ApplicationSetting.Create("delivery.default_fee", value.ToString("0.##")));
        else
            setting.SetValue(value.ToString("0.##"));
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> CreateOrderAsync(HttpClient client, Guid productId, bool delivery, string deliveryFee)
    {
        var page = await (await client.GetAsync("/admin/orders/new")).Content.ReadAsStringAsync();
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

        var fields = new Dictionary<string, string>
        {
            ["CustomerMode"] = "new",
            ["NewFirstName"] = "Delivery",
            ["NewLastName"] = "Addon",
            ["Fulfilment"] = delivery ? "delivery" : "pickup",
            ["DeliveryFee"] = deliveryFee,
            ["Products[0].ProductId"] = productId.ToString(),
            ["Products[0].Quantity"] = "2",
            ["Discount"] = "0",
            ["Tax"] = "0",
            ["__RequestVerificationToken"] = token
        };
        if (delivery)
        {
            fields["Street"] = "1 Test Way";
            fields["City"] = "Lagos";
            fields["State"] = "Lagos";
            fields["Country"] = "Nigeria";
        }

        var response = await client.PostAsync("/admin/orders/new", new FormUrlEncodedContent(fields));
        response.StatusCode.Should().Be(HttpStatusCode.Redirect, "the order must be created");
        var location = response.Headers.Location!.ToString();
        var id = Regex.Match(location, "([0-9a-f-]{36})").Groups[1].Value;
        id.Should().NotBeEmpty("the redirect must carry the new order id");
        return Guid.Parse(id);
    }

    private static async Task<HttpClient> LoginAsOwnerAsync(CustomWebApplicationFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        var ok = await TryOwnerLoginAsync(client, "ChangeMe123!");
        if (!ok) ok = await TryOwnerLoginAsync(client, "Owner!Pass9");
        ok.Should().BeTrue("owner login should succeed");
        return client;
    }

    private static async Task<bool> TryOwnerLoginAsync(HttpClient client, string password)
    {
        var html = await (await client.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = "owner@dechimexprinthub.com",
            ["Password"] = password,
            ["__RequestVerificationToken"] = token
        }));
        if (response.StatusCode != HttpStatusCode.Redirect) return false;
        var location = response.Headers.Location?.ToString() ?? "";
        if (location.Contains("/admin/security/password"))
        {
            var pwHtml = await (await client.GetAsync("/admin/security/password")).Content.ReadAsStringAsync();
            var pwToken = Regex.Match(pwHtml, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
            await client.PostAsync("/admin/security/password", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["CurrentPassword"] = password,
                ["NewPassword"] = "Owner!Pass9",
                ["ConfirmPassword"] = "Owner!Pass9",
                ["__RequestVerificationToken"] = pwToken
            }));
        }
        return true;
    }
}
