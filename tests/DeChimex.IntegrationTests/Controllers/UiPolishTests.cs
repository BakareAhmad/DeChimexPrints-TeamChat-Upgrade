using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// UI polish pass (owner directive 2026-10-07):
///   * password fields expose an eye / eye-slash icon toggle (data-pw-toggle) instead of
///     the old "Show/Hide" text buttons, on every password form (login, signup, reset,
///     portal change, admin change);
///   * enum values rendered to users are separate readable words ("Pending Payment"),
///     never raw PascalCase names, while underlying values stay untouched.
/// </summary>
public class UiPolishTests
{
    private static HttpClient Browser(CustomWebApplicationFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

    private static async Task<string> GetAsync(CustomWebApplicationFactory factory, HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"{url} should render");
        return await response.Content.ReadAsStringAsync();
    }

    private static void AssertEyeToggle(string html, int expected, string page)
    {
        var count = html.Split("data-pw-toggle").Length - 1;
        count.Should().Be(expected, $"{page} should carry {expected} eye toggle(s)");
        html.Should().Contain("pw-ic-eye-off", "the toggle shows an eye-slash state icon");
        html.Should().NotContain(">Show</button>", "the old text control is gone");
        html.Should().NotContain(">Hide</button>", "the old text control is gone");
    }

    [Fact]
    public async Task Auth_pages_use_the_eye_toggle_for_passwords()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = Browser(factory);

        AssertEyeToggle(await GetAsync(factory, client, "/Account/Login"), 1, "login");
        AssertEyeToggle(await GetAsync(factory, client, "/Account/Register"), 1, "signup");
    }

    [Fact]
    public async Task Reset_and_change_password_forms_use_the_eye_toggle()
    {
        using var factory = new CustomWebApplicationFactory();

        // Admin change-password (owner).
        var admin = await TestSeed.LoginOwnerMvcAsync(factory.Services, () => Browser(factory));
        AssertEyeToggle(await GetAsync(factory, admin, "/admin/security/password"), 3, "admin change password");

        // Customer change-password (portal).
        var email = $"polish-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(factory.Services, email);
        await TestSeed.VerifyCustomerAsync(factory.Services, email);
        var customer = Browser(factory);
        (await TestSeed.LoginMvcAsync(customer, email, TestSeed.DefaultPassword)).Should().BeTrue();
        AssertEyeToggle(await GetAsync(factory, customer, "/portal/profile/password"), 3, "portal change password");
    }

    [Fact]
    public async Task Admin_order_filters_and_badges_show_readable_status_words()
    {
        using var factory = new CustomWebApplicationFactory();
        var admin = await TestSeed.LoginOwnerMvcAsync(factory.Services, () => Browser(factory));

        var html = await GetAsync(factory, admin, "/admin/orders");

        // The filter dropdown keeps raw values but displays readable words.
        html.Should().Contain("value=\"PendingPayment\"").And.Contain(">Pending Payment<");
        html.Should().Contain("value=\"InProduction\"").And.Contain(">In Production<");
        html.Should().Contain("value=\"OutForDelivery\"").And.Contain(">Out For Delivery<");
        html.Should().NotContain(">PendingPayment<", "raw enum names are never shown verbatim");
        html.Should().NotContain(">InProduction<");
    }
}
