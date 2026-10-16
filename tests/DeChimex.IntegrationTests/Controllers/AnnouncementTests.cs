using System.Net;
using DeChimex.Persistence.Context;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Service-wide announcements (owner directive 2026-10-07): staff manage them at
/// /admin/announcements and customers see ONLY active announcements inside their
/// visibility window as a compact scrolling pill on every customer-facing page.
/// These tests pin the contract:
///   * no active announcement -> nothing renders on customer pages;
///   * create/edit/activate/deactivate/delete flow through to the customer shell;
///   * the optional start/end window gates visibility (future start hidden,
///     past end hidden, inside the window shown);
///   * invalid forms re-render with errors and store nothing;
///   * the screens are permission-guarded (customers denied, anonymous to login);
///   * the admin list still shows inactive/scheduled announcements for staff.
/// Each test boots its own factory so the in-memory database is isolated.
/// </summary>
public class AnnouncementTests
{
    private static HttpClient Browser(CustomWebApplicationFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

    private static async Task<string> PageHtmlAsync(CustomWebApplicationFactory factory, string url)
    {
        using var client = Browser(factory);
        var response = await client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>Posts an admin announcement form (create or edit) with an antiforgery token.</summary>
    private static async Task<HttpResponseMessage> SubmitAnnouncementFormAsync(
        HttpClient admin, string formUrl, Dictionary<string, string> fields)
    {
        var page = await admin.GetAsync(formUrl);
        page.StatusCode.Should().Be(HttpStatusCode.OK, $"{formUrl} should render for the owner");
        fields["__RequestVerificationToken"] = TestSeed.ExtractAntiforgeryToken(await page.Content.ReadAsStringAsync());
        return await admin.PostAsync(formUrl, new FormUrlEncodedContent(fields));
    }

    private static Dictionary<string, string> AnnouncementFields(
        string message = "Heads up: we close early at 4pm today.",
        string? start = null, string? end = null, bool active = true)
    {
        var fields = new Dictionary<string, string>
        {
            ["Message"] = message,
            ["StartAt"] = start ?? "",
            ["EndAt"] = end ?? ""
        };
        if (active) fields["IsActive"] = "true";
        return fields;
    }

    private static string Fmt(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm");

    [Fact]
    public async Task No_announcement_renders_nothing_on_customer_pages()
    {
        using var factory = new CustomWebApplicationFactory();

        foreach (var url in new[] { "/", "/about" })
        {
            var html = await PageHtmlAsync(factory, url);
            html.Should().NotContain("data-announce-ticker", "no announcement -> no pill");
            html.Should().NotContain("cs-announce");
        }
    }

    [Fact]
    public async Task Owner_creates_an_active_announcement_shown_across_customer_pages()
    {
        using var factory = new CustomWebApplicationFactory();
        var admin = await TestSeed.LoginOwnerMvcAsync(factory.Services, () => Browser(factory));

        var create = await SubmitAnnouncementFormAsync(admin, "/admin/announcements/create",
            AnnouncementFields(message: "Public holiday: orders placed after 4pm ship next day."));
        create.StatusCode.Should().Be(HttpStatusCode.Redirect);

        foreach (var url in new[] { "/", "/about", "/contact" })
        {
            var html = await PageHtmlAsync(factory, url);
            html.Should().Contain("data-announce-ticker", "the pill renders on every customer page");
            html.Should().Contain("Public holiday: orders placed after 4pm ship next day.");
        }

        var list = await admin.GetAsync("/admin/announcements");
        var listHtml = await list.Content.ReadAsStringAsync();
        listHtml.Should().Contain("Public holiday: orders placed after 4pm ship next day.")
            .And.Contain("Live", "an active in-window announcement shows as live");
    }

    [Fact]
    public async Task Announcement_edit_updates_the_ticker()
    {
        using var factory = new CustomWebApplicationFactory();
        var admin = await TestSeed.LoginOwnerMvcAsync(factory.Services, () => Browser(factory));

        await SubmitAnnouncementFormAsync(admin, "/admin/announcements/create",
            AnnouncementFields(message: "Original announcement wording."));

        Guid id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            id = (await db.Announcements.AsNoTracking().FirstAsync(a => a.Message == "Original announcement wording.")).Id;
        }

        var edit = await SubmitAnnouncementFormAsync(admin, $"/admin/announcements/edit/{id}",
            AnnouncementFields(message: "Updated announcement wording."));
        edit.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var home = await PageHtmlAsync(factory, "/");
        home.Should().Contain("Updated announcement wording.")
            .And.NotContain("Original announcement wording.");
    }

    [Fact]
    public async Task Deactivate_hides_and_activate_shows_again()
    {
        using var factory = new CustomWebApplicationFactory();
        var admin = await TestSeed.LoginOwnerMvcAsync(factory.Services, () => Browser(factory));

        await SubmitAnnouncementFormAsync(admin, "/admin/announcements/create",
            AnnouncementFields(message: "Toggle me."));

        Guid id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            id = (await db.Announcements.AsNoTracking().FirstAsync(a => a.Message == "Toggle me.")).Id;
        }

        async Task<HttpResponseMessage> ToggleAsync(bool active)
        {
            var page = await admin.GetAsync("/admin/announcements");
            var token = TestSeed.ExtractAntiforgeryToken(await page.Content.ReadAsStringAsync());
            return await admin.PostAsync($"/admin/announcements/toggle/{id}?active={(active ? "true" : "false")}",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = token
                }));
        }

        (await ToggleAsync(false)).StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await PageHtmlAsync(factory, "/")).Should().NotContain("Toggle me.", "deactivated announcements hide");

        (await ToggleAsync(true)).StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await PageHtmlAsync(factory, "/")).Should().Contain("Toggle me.", "reactivating shows it again");
    }

    [Fact]
    public async Task Delete_removes_the_ticker()
    {
        using var factory = new CustomWebApplicationFactory();
        var admin = await TestSeed.LoginOwnerMvcAsync(factory.Services, () => Browser(factory));

        await SubmitAnnouncementFormAsync(admin, "/admin/announcements/create",
            AnnouncementFields(message: "Short-lived announcement."));

        Guid id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            id = (await db.Announcements.AsNoTracking().FirstAsync(a => a.Message == "Short-lived announcement.")).Id;
        }

        var page = await admin.GetAsync("/admin/announcements");
        var token = TestSeed.ExtractAntiforgeryToken(await page.Content.ReadAsStringAsync());
        var delete = await admin.PostAsync($"/admin/announcements/delete/{id}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token
            }));
        delete.StatusCode.Should().Be(HttpStatusCode.Redirect);

        (await PageHtmlAsync(factory, "/")).Should().NotContain("Short-lived announcement.");
        (await admin.GetAsync("/admin/announcements")).Content.ReadAsStringAsync().Result
            .Should().NotContain("Short-lived announcement.");
    }

    [Fact]
    public async Task Only_announcements_inside_their_visibility_window_show()
    {
        using var factory = new CustomWebApplicationFactory();
        var admin = await TestSeed.LoginOwnerMvcAsync(factory.Services, () => Browser(factory));

        // Future start -> not yet visible.
        (await SubmitAnnouncementFormAsync(admin, "/admin/announcements/create",
            AnnouncementFields(message: "Starts tomorrow.", start: Fmt(DateTime.UtcNow.AddDays(1)))))
            .StatusCode.Should().Be(HttpStatusCode.Redirect);

        // Past end -> expired.
        (await SubmitAnnouncementFormAsync(admin, "/admin/announcements/create",
            AnnouncementFields(message: "Ended yesterday.", end: Fmt(DateTime.UtcNow.AddHours(-1)))))
            .StatusCode.Should().Be(HttpStatusCode.Redirect);

        // Inside the window -> visible.
        (await SubmitAnnouncementFormAsync(admin, "/admin/announcements/create",
            AnnouncementFields(message: "Inside the window.",
                start: Fmt(DateTime.UtcNow.AddHours(-1)), end: Fmt(DateTime.UtcNow.AddHours(1)))))
            .StatusCode.Should().Be(HttpStatusCode.Redirect);

        var home = await PageHtmlAsync(factory, "/");
        home.Should().Contain("Inside the window.")
            .And.NotContain("Starts tomorrow.", "a future start date hides the announcement")
            .And.NotContain("Ended yesterday.", "an expired announcement hides");

        // Staff still see all three (with their states) in the admin list.
        var listHtml = await (await admin.GetAsync("/admin/announcements")).Content.ReadAsStringAsync();
        listHtml.Should().Contain("Starts tomorrow.").And.Contain("Ended yesterday.").And.Contain("Inside the window.");
    }

    [Fact]
    public async Task Inactive_announcement_never_shows_even_inside_its_window()
    {
        using var factory = new CustomWebApplicationFactory();
        var admin = await TestSeed.LoginOwnerMvcAsync(factory.Services, () => Browser(factory));

        (await SubmitAnnouncementFormAsync(admin, "/admin/announcements/create",
            AnnouncementFields(message: "Saved but inactive.", active: false)))
            .StatusCode.Should().Be(HttpStatusCode.Redirect);

        (await PageHtmlAsync(factory, "/")).Should().NotContain("Saved but inactive.");

        var listHtml = await (await admin.GetAsync("/admin/announcements")).Content.ReadAsStringAsync();
        listHtml.Should().Contain("Saved but inactive.").And.Contain("Inactive");
    }

    [Fact]
    public async Task Invalid_forms_re_render_with_errors_and_store_nothing()
    {
        using var factory = new CustomWebApplicationFactory();
        var admin = await TestSeed.LoginOwnerMvcAsync(factory.Services, () => Browser(factory));

        // Empty message.
        var empty = await SubmitAnnouncementFormAsync(admin, "/admin/announcements/create",
            AnnouncementFields(message: ""));
        empty.StatusCode.Should().Be(HttpStatusCode.OK, "invalid model re-renders the form");
        (await empty.Content.ReadAsStringAsync()).Should().Contain("Announcement message is required.");

        // End before start.
        var backwards = await SubmitAnnouncementFormAsync(admin, "/admin/announcements/create",
            AnnouncementFields(message: "Backwards window.",
                start: Fmt(DateTime.UtcNow.AddDays(2)), end: Fmt(DateTime.UtcNow.AddDays(1))));
        backwards.StatusCode.Should().Be(HttpStatusCode.OK);
        (await backwards.Content.ReadAsStringAsync()).Should().Contain("End date must be after the start date.");

        // Overlong message (201 chars).
        var tooLong = await SubmitAnnouncementFormAsync(admin, "/admin/announcements/create",
            AnnouncementFields(message: new string('x', 201)));
        tooLong.StatusCode.Should().Be(HttpStatusCode.OK);
        (await tooLong.Content.ReadAsStringAsync()).Should().Contain("at most 200 characters");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        (await db.Announcements.CountAsync()).Should().Be(0, "invalid submissions store nothing");
    }

    [Fact]
    public async Task Customers_cannot_reach_the_admin_announcement_screens()
    {
        using var factory = new CustomWebApplicationFactory();
        var email = $"reader-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(factory.Services, email);
        await TestSeed.VerifyCustomerAsync(factory.Services, email);

        var customer = Browser(factory);
        (await TestSeed.LoginMvcAsync(customer, email, TestSeed.DefaultPassword)).Should().BeTrue();

        var list = await customer.GetAsync("/admin/announcements");
        list.StatusCode.Should().Be(HttpStatusCode.Redirect);
        list.Headers.Location!.ToString().Should().Contain("/Account/AccessDenied");

        // A customer must never be able to create announcements either.
        var loginPage = await customer.GetAsync("/Account/Login");
        var token = TestSeed.ExtractAntiforgeryToken(await loginPage.Content.ReadAsStringAsync());
        var post = await customer.PostAsync("/admin/announcements/create", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Message"] = "Sneaky announcement.",
                ["IsActive"] = "true",
                ["__RequestVerificationToken"] = token
            }));
        post.StatusCode.Should().Be(HttpStatusCode.Redirect);
        post.Headers.Location!.ToString().Should().Contain("/Account/AccessDenied");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        (await db.Announcements.CountAsync(a => a.Message == "Sneaky announcement.")).Should().Be(0);

        // The customer-facing pages stay clean.
        (await PageHtmlAsync(factory, "/")).Should().NotContain("Sneaky announcement.");
    }

    [Fact]
    public async Task Anonymous_visitors_cannot_reach_the_admin_announcement_screens()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = Browser(factory);

        var list = await client.GetAsync("/admin/announcements");
        list.StatusCode.Should().Be(HttpStatusCode.Redirect);
        list.Headers.Location!.ToString().Should().Contain("/Account/Login");
    }
}
