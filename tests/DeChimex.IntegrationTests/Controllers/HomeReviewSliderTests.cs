using System.Net;
using DeChimex.Persistence.Context;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Home page customer reviews: they are SEEDED RECORDS rendered as a slider, and staff manage
/// them from /admin/reviews (owner directive 2026-10-07). These tests pin the contract:
///   * the seeded reviews still display, in slider markup (not the old card grid);
///   * a new review appears on the home page, an edit updates it, a delete removes it;
///   * only published reviews are shown;
///   * the admin screens are permission-guarded (customers cannot reach them).
/// Each test boots its own factory so the in-memory database is isolated.
/// </summary>
public class HomeReviewSliderTests
{
    private static HttpClient Browser(CustomWebApplicationFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

    private static async Task<string> HomeHtmlAsync(CustomWebApplicationFactory factory)
    {
        using var client = Browser(factory);
        var response = await client.GetAsync("/");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>Posts the admin review form (create or edit) with an antiforgery token.</summary>
    private static async Task<HttpResponseMessage> SubmitReviewFormAsync(
        HttpClient admin, string formUrl, Dictionary<string, string> fields)
    {
        var page = await admin.GetAsync(formUrl);
        page.StatusCode.Should().Be(HttpStatusCode.OK, $"{formUrl} should render for the owner");
        fields["__RequestVerificationToken"] = TestSeed.ExtractAntiforgeryToken(await page.Content.ReadAsStringAsync());
        return await admin.PostAsync(formUrl, new FormUrlEncodedContent(fields));
    }

    private static Dictionary<string, string> ReviewFields(
        string name = "Slider Reviewer", string body = "Excellent quality and fast delivery.",
        string title = "Founder, TestCo", string rating = "5", bool published = true, string order = "0")
    {
        var fields = new Dictionary<string, string>
        {
            ["CustomerName"] = name,
            ["CustomerTitle"] = title,
            ["Body"] = body,
            ["AvatarUrl"] = "",
            ["Rating"] = rating,
            ["DisplayOrder"] = order
        };
        if (published) fields["IsPublished"] = "true";
        return fields;
    }

    [Fact]
    public async Task Home_shows_the_seeded_reviews_as_a_slider()
    {
        using var factory = new CustomWebApplicationFactory();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var seeded = await db.Testimonials.AsNoTracking().ToListAsync();
            seeded.Should().HaveCount(4, "the four reviews that used to be hard-coded are seeded as records");
            seeded.Should().OnlyContain(t => t.IsPublished);
        }

        var html = await HomeHtmlAsync(factory);
        html.Should().Contain("What customers say");
        html.Should().Contain("data-review-slider", "reviews render as a slider");
        html.Should().Contain("hm-review-track").And.Contain("hm-review-slide");
        html.Should().NotContain("class=\"hm-reviews\"", "the old card grid is gone");

        foreach (var name in new[] { "Muinat B.", "Olalekan F.", "TokenPicks", "DE-YEMKING Publishers LTD." })
            html.Should().Contain(name, "every seeded review still displays");
    }

    [Fact]
    public async Task Admin_can_add_a_review_and_it_appears_on_the_home_page()
    {
        using var factory = new CustomWebApplicationFactory();
        var admin = await TestSeed.LoginOwnerMvcAsync(factory.Services, () => Browser(factory));

        var create = await SubmitReviewFormAsync(admin, "/admin/reviews/create",
            ReviewFields(name: "Ada Obi", body: "Brand new slider review from the admin portal."));
        create.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var list = await admin.GetAsync("/admin/reviews");
        (await list.Content.ReadAsStringAsync()).Should().Contain("Ada Obi", "the new review is listed in the admin portal");

        var home = await HomeHtmlAsync(factory);
        home.Should().Contain("Ada Obi").And.Contain("Brand new slider review from the admin portal.");
    }

    [Fact]
    public async Task Admin_edit_updates_the_home_page()
    {
        using var factory = new CustomWebApplicationFactory();
        var admin = await TestSeed.LoginOwnerMvcAsync(factory.Services, () => Browser(factory));

        await SubmitReviewFormAsync(admin, "/admin/reviews/create",
            ReviewFields(name: "Edit Target", body: "Original wording before the edit."));

        Guid id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            id = (await db.Testimonials.AsNoTracking().FirstAsync(t => t.CustomerName == "Edit Target")).Id;
        }

        var edit = await SubmitReviewFormAsync(admin, $"/admin/reviews/edit/{id}",
            ReviewFields(name: "Edit Target", body: "Updated wording after the edit.", title: "CEO, TestCo", rating: "4"));
        edit.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var home = await HomeHtmlAsync(factory);
        home.Should().Contain("Updated wording after the edit.");
        home.Should().NotContain("Original wording before the edit.");
        home.Should().Contain("aria-label=\"4 out of 5 stars\"", "the edited star rating is rendered");
    }

    [Fact]
    public async Task Admin_delete_removes_the_review_from_the_home_page()
    {
        using var factory = new CustomWebApplicationFactory();
        var admin = await TestSeed.LoginOwnerMvcAsync(factory.Services, () => Browser(factory));

        await SubmitReviewFormAsync(admin, "/admin/reviews/create",
            ReviewFields(name: "Delete Target", body: "This review is about to be deleted."));
        (await HomeHtmlAsync(factory)).Should().Contain("Delete Target");

        Guid id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            id = (await db.Testimonials.AsNoTracking().FirstAsync(t => t.CustomerName == "Delete Target")).Id;
        }

        var page = await admin.GetAsync("/admin/reviews");
        var token = TestSeed.ExtractAntiforgeryToken(await page.Content.ReadAsStringAsync());
        var delete = await admin.PostAsync($"/admin/reviews/delete/{id}", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        delete.StatusCode.Should().Be(HttpStatusCode.Redirect);

        (await HomeHtmlAsync(factory)).Should().NotContain("Delete Target");
    }

    [Fact]
    public async Task Unpublished_reviews_are_hidden_from_the_home_page_but_listed_for_staff()
    {
        using var factory = new CustomWebApplicationFactory();
        var admin = await TestSeed.LoginOwnerMvcAsync(factory.Services, () => Browser(factory));

        await SubmitReviewFormAsync(admin, "/admin/reviews/create",
            ReviewFields(name: "Hidden Reviewer", body: "Draft review that is not published.", published: false));

        (await HomeHtmlAsync(factory)).Should().NotContain("Hidden Reviewer", "unpublished reviews never reach the storefront");

        var list = await admin.GetAsync("/admin/reviews");
        var html = await list.Content.ReadAsStringAsync();
        html.Should().Contain("Hidden Reviewer").And.Contain("Hidden", "staff still see and can publish it");
    }

    [Fact]
    public async Task Review_form_rejects_missing_name_or_text()
    {
        using var factory = new CustomWebApplicationFactory();
        var admin = await TestSeed.LoginOwnerMvcAsync(factory.Services, () => Browser(factory));

        var fields = ReviewFields();
        fields["CustomerName"] = "";
        fields["Body"] = "";

        var response = await SubmitReviewFormAsync(admin, "/admin/reviews/create", fields);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "validation errors re-render the form");
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("Customer name is required.").And.Contain("Review text is required.");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        (await db.Testimonials.CountAsync()).Should().Be(4, "an invalid submission stores nothing");
    }

    [Fact]
    public async Task Customers_cannot_reach_the_admin_review_screens()
    {
        using var factory = new CustomWebApplicationFactory();
        var email = $"reviewer-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(factory.Services, email);
        await TestSeed.VerifyCustomerAsync(factory.Services, email);

        var customer = Browser(factory);
        (await TestSeed.LoginMvcAsync(customer, email, TestSeed.DefaultPassword)).Should().BeTrue();

        // Authenticated but unauthorized: the app redirects staff-only screens to AccessDenied.
        var list = await customer.GetAsync("/admin/reviews");
        list.StatusCode.Should().Be(HttpStatusCode.Redirect);
        list.Headers.Location!.ToString().Should().Contain("/Account/AccessDenied");

        var create = await customer.GetAsync("/admin/reviews/create");
        create.StatusCode.Should().Be(HttpStatusCode.Redirect);
        create.Headers.Location!.ToString().Should().Contain("/Account/AccessDenied");

        // A customer must never be able to change reviews either.
        var listPage = await customer.GetAsync("/Account/Login");
        var token = TestSeed.ExtractAntiforgeryToken(await listPage.Content.ReadAsStringAsync());
        var post = await customer.PostAsync("/admin/reviews/create", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["CustomerName"] = "Sneaky", ["Body"] = "Should not be created.",
                ["Rating"] = "5", ["DisplayOrder"] = "0", ["IsPublished"] = "true",
                ["__RequestVerificationToken"] = token
            }));
        post.StatusCode.Should().Be(HttpStatusCode.Redirect);
        post.Headers.Location!.ToString().Should().Contain("/Account/AccessDenied");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        (await db.Testimonials.CountAsync(t => t.CustomerName == "Sneaky")).Should().Be(0);
    }

    [Fact]
    public async Task Anonymous_visitors_cannot_reach_the_admin_review_screens()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = Browser(factory);

        var response = await client.GetAsync("/admin/reviews");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Contain("/Account/Login");
    }
}
