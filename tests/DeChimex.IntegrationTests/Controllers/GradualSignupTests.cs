using System.Net;
using DeChimex.Domain.Repositories;
using DeChimex.Persistence.Context;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Gradual signup + progressive profile completion (owner directive 2026-10-07).
///
/// Signup asks for ONLY email + password (no confirm-password, name, phone or address) and
/// password security is unchanged. The customer can then browse normally; when they reach
/// checkout with an incomplete profile they are routed to a profile-completion step that
/// collects only what the order workflow needs (name + contact phone), then returns them to
/// checkout. Customers whose profile is already complete are never prompted.
/// </summary>
public class GradualSignupTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public GradualSignupTests(CustomWebApplicationFactory factory) => _factory = factory;

    private HttpClient CreateBrowser() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    private const string StrongPassword = "Passw0rd!Strong";

    /// <summary>Reads an antiforgery token from any rendered page (all these POSTs are protected).</summary>
    private static async Task<string> TokenAsync(HttpClient browser, string url)
    {
        var page = await browser.GetAsync(url);
        page.StatusCode.Should().Be(HttpStatusCode.OK, $"{url} should render for a signed-in customer");
        return TestSeed.ExtractAntiforgeryToken(await page.Content.ReadAsStringAsync());
    }

    private static async Task AddToCartAsync(HttpClient browser, Guid productId)
    {
        var token = await TokenAsync(browser, "/cart");
        var add = await browser.PostAsync("/cart/add", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["lineType"] = "product",
            ["referenceId"] = productId.ToString(),
            ["quantity"] = "1",
            ["__RequestVerificationToken"] = token
        }));
        add.StatusCode.Should().Be(HttpStatusCode.Redirect, "adding to the cart should redirect");
    }

    /// <summary>Signs up through the MVC form with credentials only — exactly the two fields
    /// the form renders — then completes the existing email-verification gate.</summary>
    private async Task<HttpClient> SignUpCredentialsOnlyAsync(string email)
    {
        var browser = CreateBrowser();
        var page = await browser.GetAsync("/Account/Register");
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        var response = await browser.PostAsync("/Account/Register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = StrongPassword,
            ["__RequestVerificationToken"] = TestSeed.ExtractAntiforgeryToken(await page.Content.ReadAsStringAsync())
        }));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect, "signup should redirect to the existing verification step");
        response.Headers.Location!.ToString().Should().Contain("VerifyEmail");

        await TestSeed.VerifyCustomerAsync(_factory.Services, email);
        (await TestSeed.LoginMvcAsync(browser, email, StrongPassword)).Should().BeTrue("the new account should sign in normally");
        return browser;
    }

    private async Task<(DeChimex.Domain.Entities.User User, DeChimex.Domain.Entities.Customer Customer)> LoadAccountAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var customers = scope.ServiceProvider.GetRequiredService<ICustomerRepository>();
        var user = await users.GetByEmailAsync(email);
        user.Should().NotBeNull("the login account should exist");
        var customer = await customers.GetByUserIdAsync(user!.Id, CancellationToken.None);
        customer.Should().NotBeNull("a customer record should exist for the signed-up account");
        return (user!, customer!);
    }

    // ---------------------------------------------------------------- signup

    [Fact]
    public async Task Register_WithEmailAndPasswordOnly_CreatesAccountWithoutProfile()
    {
        var email = $"gradual-{Guid.NewGuid():N}@example.com";
        await SignUpCredentialsOnlyAsync(email);

        var (user, customer) = await LoadAccountAsync(email);

        // No profile was demanded at signup.
        user.FirstName.Should().BeEmpty();
        user.LastName.Should().BeEmpty();
        user.IsEmailVerified.Should().BeTrue("the existing verification gate still applies");
        customer.FirstName.Should().BeEmpty();
        customer.LastName.Should().BeEmpty();
        customer.Phone.Should().BeNull();
        customer.IsGuest.Should().BeFalse("an account with an email is not a walk-in guest");
        customer.UserId.Should().Be(user.Id);
        customer.HasCompleteProfile.Should().BeFalse("name and phone are collected later, at order time");

        // Display names stay sensible instead of rendering blank.
        user.FullName.Should().Be(email);
        customer.FullName.Should().Be(email);
    }

    [Fact]
    public async Task Register_WithoutConfirmPasswordOrProfileFields_IsAccepted()
    {
        // The form no longer has ConfirmPassword / FirstName / LastName / Phone at all.
        var email = $"noconfirm-{Guid.NewGuid():N}@example.com";
        var browser = CreateBrowser();

        var page = await browser.GetAsync("/Account/Register");
        var html = await page.Content.ReadAsStringAsync();
        html.Should().Contain("name=\"Email\"").And.Contain("name=\"Password\"");
        html.Should().NotContain("name=\"ConfirmPassword\"");
        html.Should().NotContain("name=\"FirstName\"");
        html.Should().NotContain("name=\"Phone\"");

        var response = await browser.PostAsync("/Account/Register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = StrongPassword,
            ["__RequestVerificationToken"] = TestSeed.ExtractAntiforgeryToken(html)
        }));
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task Register_WeakPassword_IsStillRejected()
    {
        // Removing confirm-password must not weaken password security: the application's
        // existing rules (min 8, upper, lower, digit) still apply server-side.
        var email = $"weakpw-{Guid.NewGuid():N}@example.com";
        var browser = CreateBrowser();
        var page = await browser.GetAsync("/Account/Register");

        var response = await browser.PostAsync("/Account/Register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = "short",
            ["__RequestVerificationToken"] = TestSeed.ExtractAntiforgeryToken(await page.Content.ReadAsStringAsync())
        }));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "an invalid password re-renders the form");
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("at least 8 characters");

        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        (await users.GetByEmailAsync(email)).Should().BeNull("no account should be created for a weak password");
    }

    // ------------------------------------------------- order-time completion

    [Fact]
    public async Task Checkout_WithIncompleteProfile_RoutesToProfileCompletion_ThenBackToCheckout()
    {
        var email = $"gate-{Guid.NewGuid():N}@example.com";
        var browser = await SignUpCredentialsOnlyAsync(email);

        // Browsing works normally right after signup — no profile wall.
        (await browser.GetAsync("/")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await browser.GetAsync("/cart")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await browser.GetAsync("/portal/profile")).StatusCode.Should().Be(HttpStatusCode.OK);

        // Give them something in the cart so checkout is reachable.
        var productId = await TestSeed.SeedProductAsync(_factory.Services, $"Gradual {Guid.NewGuid():N}");
        await AddToCartAsync(browser, productId);

        // Reaching checkout with an incomplete profile routes to completion, not an error.
        var checkout = await browser.GetAsync("/checkout");
        checkout.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var target = checkout.Headers.Location!.ToString();
        target.Should().Contain("/portal/profile/complete");
        target.Should().Contain("returnUrl=%2Fcheckout", "the customer must come back to checkout, not restart");

        // The completion page reads as a profile update: pre-filled, no email/password fields.
        var formPage = await browser.GetAsync(target);
        formPage.StatusCode.Should().Be(HttpStatusCode.OK);
        var formHtml = await formPage.Content.ReadAsStringAsync();
        formHtml.Should().Contain("Complete your profile");
        formHtml.Should().Contain("name=\"FirstName\"").And.Contain("name=\"Phone\"");
        formHtml.Should().NotContain("name=\"Password\"");
        formHtml.Should().NotContain("name=\"Email\"");
        formHtml.Should().Contain("name=\"ReturnUrl\"");

        // Saving with invalid data is rejected inline and leaves the profile incomplete.
        var token = TestSeed.ExtractAntiforgeryToken(formHtml);
        var invalid = await browser.PostAsync("/portal/profile/complete", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["FirstName"] = "Ada",
            ["LastName"] = "Obi",
            ["Phone"] = "not-a-phone",
            ["ReturnUrl"] = "/checkout",
            ["__RequestVerificationToken"] = token
        }));
        invalid.StatusCode.Should().Be(HttpStatusCode.OK, "validation errors re-render the form");
        (await invalid.Content.ReadAsStringAsync()).Should().Contain("valid phone number");
        (await LoadAccountAsync(email)).Customer.HasCompleteProfile.Should().BeFalse();

        // Saving valid details completes the profile and returns to checkout.
        var ok = await browser.PostAsync("/portal/profile/complete", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["FirstName"] = "Ada",
            ["LastName"] = "Obi",
            ["Phone"] = "08030000009",
            ["ReturnUrl"] = "/checkout",
            ["__RequestVerificationToken"] = token
        }));
        ok.StatusCode.Should().Be(HttpStatusCode.Redirect);
        ok.Headers.Location!.ToString().Should().Be("/checkout");

        var (_, customer) = await LoadAccountAsync(email);
        customer.FirstName.Should().Be("Ada");
        customer.LastName.Should().Be("Obi");
        customer.Phone!.Value.Should().Be("08030000009");
        customer.HasCompleteProfile.Should().BeTrue();
        customer.FullName.Should().Be("Ada Obi");

        // Checkout now proceeds without the prompt.
        var resumed = await browser.GetAsync("/checkout");
        resumed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resumed.Content.ReadAsStringAsync()).Should().Contain("Checkout");
    }

    [Fact]
    public async Task PlaceOrder_WithIncompleteProfile_IsBlocked_AndCreatesNoOrder()
    {
        var email = $"blocked-{Guid.NewGuid():N}@example.com";
        var browser = await SignUpCredentialsOnlyAsync(email);

        var productId = await TestSeed.SeedProductAsync(_factory.Services, $"Blocked {Guid.NewGuid():N}");
        await AddToCartAsync(browser, productId);

        // A direct POST (skipping the GET gate) must still be stopped.
        var token = await TokenAsync(browser, "/portal/profile");
        var post = await browser.PostAsync("/checkout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Fulfilment"] = "delivery",
            ["Street"] = "1 Test Street",
            ["City"] = "Lagos",
            ["State"] = "Lagos",
            ["Country"] = "Nigeria",
            ["__RequestVerificationToken"] = token
        }));

        post.StatusCode.Should().Be(HttpStatusCode.Redirect);
        post.Headers.Location!.ToString().Should().Contain("/portal/profile/complete");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var (_, customer) = await LoadAccountAsync(email);
        (await db.Orders.CountAsync(o => o.CustomerId == customer.Id)).Should().Be(0,
            "no order may be created while required profile details are missing");
    }

    [Fact]
    public async Task Checkout_WithCompleteProfile_IsNotPrompted()
    {
        // Customers created under the old (full) registration flow keep the normal path.
        var email = $"complete-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(_factory.Services, email, firstName: "Legacy", lastName: "Customer");
        await TestSeed.VerifyCustomerAsync(_factory.Services, email);

        (await LoadAccountAsync(email)).Customer.HasCompleteProfile.Should().BeTrue();

        var browser = CreateBrowser();
        (await TestSeed.LoginMvcAsync(browser, email, TestSeed.DefaultPassword)).Should().BeTrue();

        var productId = await TestSeed.SeedProductAsync(_factory.Services, $"Legacy {Guid.NewGuid():N}");
        await AddToCartAsync(browser, productId);

        var checkout = await browser.GetAsync("/checkout");
        checkout.StatusCode.Should().Be(HttpStatusCode.OK, "a complete profile is never routed to completion");
        (await checkout.Content.ReadAsStringAsync()).Should().NotContain("Complete your profile");
    }
}
