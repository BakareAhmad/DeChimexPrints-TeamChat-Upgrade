using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using DeChimex.Domain.Enums;
using DeChimex.Persistence.Context;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Mobile gallery surface (2026-10-06 fix): the public gallery must be part of the mobile
/// experience (bottom nav entry - the desktop-only .cs-links hid it on phones), and the admin
/// upload form's file picker must offer videos on mobile. The accept attribute is only a picker
/// hint - mobile OS pickers honour it and previously filtered existing videos out of the list,
/// so a single .mp4/.webm pick (already supported end-to-end by onPick + the controller) was
/// impossible from a phone. The server stays authoritative on type/size: unsupported extensions,
/// oversized files and mixed selections are rejected exactly as before, and image carousels are
/// unchanged.
/// </summary>
public class GalleryMobileSurfaceTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly byte[] Png1X1 = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    // Not a playable clip - storage validates by extension/size and re-encoding is best-effort,
    // which is exactly what an upload test needs.
    private static readonly byte[] FakeMp4 = "ftypisom-not-a-real-video".Select(c => (byte)c).ToArray();

    private readonly CustomWebApplicationFactory _factory;

    public GalleryMobileSurfaceTests(CustomWebApplicationFactory factory) => _factory = factory;

    private HttpClient CreateBrowser()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        // The full suite runs dozens of hosts in parallel on small CI boxes; give the
        // multi-step owner flows headroom so slowness is not mistaken for a failure.
        client.Timeout = TimeSpan.FromMinutes(5);
        return client;
    }

    private async Task<HttpClient> LoginAsOwnerAsync()
    {
        var client = CreateBrowser();
        var ok = await TryOwnerLoginAsync(client, "ChangeMe123!");
        if (!ok)
            ok = await TryOwnerLoginAsync(client, "Owner!Pass9");
        ok.Should().BeTrue("owner login should succeed");
        return client;
    }

    private static async Task<bool> TryOwnerLoginAsync(HttpClient client, string password)
    {
        var loginPage = await client.GetAsync("/Account/Login");
        var html = await loginPage.Content.ReadAsStringAsync();
        var token = Regex.Match(html, "value=\"([^\"]+)\"").Groups[1].Value;

        var loginResponse = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Email"] = "owner@dechimexprinthub.com",
                ["Password"] = password,
                ["__RequestVerificationToken"] = token
            }));
        if (loginResponse.StatusCode != HttpStatusCode.Redirect)
            return false;

        var location = loginResponse.Headers.Location?.ToString() ?? "";
        if (location.Contains("/admin/security/password"))
            await CompleteForcedPasswordChangeAsync(client, password);
        return true;
    }

    private static async Task CompleteForcedPasswordChangeAsync(HttpClient client, string currentPassword)
    {
        var html = await (await client.GetAsync("/admin/security/password")).Content.ReadAsStringAsync();
        var token = Regex.Match(html, "value=\"([^\"]+)\"").Groups[1].Value;
        var response = await client.PostAsync("/admin/security/password", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["CurrentPassword"] = currentPassword,
                ["NewPassword"] = "Owner!Pass9",
                ["ConfirmPassword"] = "Owner!Pass9",
                ["__RequestVerificationToken"] = token
            }));
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    /// <summary>Posts the create form (multipart) as the owner and returns the response.</summary>
    private static async Task<HttpResponseMessage> PostCreateAsync(
        HttpClient client, string title, params (byte[] Bytes, string ContentType, string FileName)[] files)
    {
        var createPage = await client.GetAsync("/admin/gallery/create");
        createPage.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = TestSeed.ExtractAntiforgeryToken(await createPage.Content.ReadAsStringAsync());
        token.Should().NotBeNullOrWhiteSpace();

        var form = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new StringContent(title), "Title" },
            { new StringContent("0"), "DisplayOrder" }
        };
        foreach (var (bytes, contentType, fileName) in files)
        {
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(file, "MediaFiles", fileName);
        }
        return await client.PostAsync("/admin/gallery/create", form);
    }

    private static string BottomNav(string html)
    {
        var start = html.IndexOf("<nav class=\"cs-bottom\"", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, "the customer shell renders a mobile bottom nav");
        var end = html.IndexOf("</nav>", start, StringComparison.Ordinal);
        return html[start..(end + 6)];
    }

    private static string DesktopNav(string html)
    {
        var start = html.IndexOf("<nav class=\"cs-links\"", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, "the customer shell renders the desktop link nav");
        var end = html.IndexOf("</nav>", start, StringComparison.Ordinal);
        return html[start..(end + 6)];
    }

    // ------------------------------------------------------------------
    // Issue 1: gallery presence in the mobile public experience
    // ------------------------------------------------------------------

    [Fact]
    public async Task Mobile_bottom_nav_links_the_gallery_and_desktop_nav_keeps_our_work()
    {
        var html = await _factory.CreateClient().GetStringAsync("/");

        var bottom = BottomNav(html);
        bottom.Should().Contain("href=\"/gallery\"", "gallery must be reachable from the phone nav, not only the footer");
        bottom.Should().Contain("<span>Gallery</span>");
        bottom.Should().NotContain("capture");

        // Desktop regression: the inline nav still offers the same destination.
        DesktopNav(html).Should().Contain("href=\"/gallery\"").And.Contain("Our work");
    }

    [Fact]
    public async Task Bottom_nav_has_room_for_the_gallery_entry_on_small_phones()
    {
        var css = await _factory.CreateClient().GetStringAsync("/css/customer.css");
        css.Should().Contain("grid-template-columns:repeat(6,minmax(0,1fr))",
            "the fixed bottom bar grid must match the six nav entries");
        css.Should().Contain("@media (max-width:359px){.cs-bottom a{font-size:10px",
            "tiny phones shrink the labels so nothing clips or wraps");
    }

    [Fact]
    public async Task Gallery_viewer_modal_helper_ships_with_the_customer_shell()
    {
        // The viewer calls openModal(...) and drives #dcxModal; the helper was lost with the
        // old storefront shell, silently breaking every tile click. Keep the pair together.
        var client = _factory.CreateClient();
        var js = await client.GetStringAsync("/js/customer-shell.js");
        js.Should().Contain("window.openModal").And.Contain("dcxModal");

        var gallery = await client.GetStringAsync("/gallery");
        gallery.Should().Contain("openGallery(this)", "grid tiles open the shared viewer");
    }

    [Fact]
    public async Task Gallery_page_marks_the_mobile_nav_entry_active()
    {
        var html = await _factory.CreateClient().GetStringAsync("/gallery");
        var galleryLink = Regex.Match(BottomNav(html), "<a href=\"/gallery\"[^>]*>");
        galleryLink.Success.Should().BeTrue();
        galleryLink.Value.Should().Contain("is-active").And.Contain("aria-current=\"page\"");
    }

    // ------------------------------------------------------------------
    // Issue 2: mobile file pickers must offer existing videos
    // ------------------------------------------------------------------

    [Fact]
    public async Task Create_form_media_input_offers_videos_to_mobile_pickers_without_forcing_the_camera()
    {
        var client = await LoginAsOwnerAsync();
        var html = await (await client.GetAsync("/admin/gallery/create")).Content.ReadAsStringAsync();

        var input = Regex.Match(html, "<input[^>]*id=\"MediaFiles\"[^>]*>");
        input.Success.Should().BeTrue();
        var accept = Regex.Match(input.Value, "accept=\"([^\"]+)\"").Groups[1].Value;

        // Image types stay (unchanged behaviour) ...
        accept.Should().Contain(".jpg").And.Contain(".jpeg").And.Contain(".png")
            .And.Contain(".webp").And.Contain(".gif");
        // ... and videos join as BOTH extensions and MIME types: iOS/Android pickers match on either.
        accept.Should().Contain(".mp4").And.Contain(".webm")
            .And.Contain("video/mp4").And.Contain("video/webm");

        // Never add capture: it would force the camera and hide the existing-video library.
        input.Value.Should().NotContain("capture");
        html.Should().NotContain("capture=", "no gallery input may force the camera");
    }

    // ------------------------------------------------------------------
    // Server stays authoritative + existing behaviour is preserved
    // ------------------------------------------------------------------

    [Fact]
    public async Task Single_video_pick_persists_as_a_Video_post_and_renders_on_the_public_gallery()
    {
        var client = await LoginAsOwnerAsync();
        var title = "E2E mobile clip " + Guid.NewGuid().ToString("n")[..8];

        var response = await PostCreateAsync(client, title, (FakeMp4, "video/mp4", "clip.mp4"));
        response.StatusCode.Should().Be(HttpStatusCode.Redirect, "a single .mp4 pick creates a video post");
        response.Headers.Location!.ToString().Should().Be("/admin/gallery");

        string url;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var post = await db.GalleryMedia.SingleOrDefaultAsync(m => m.Title == title && m.ParentId == null);
            post.Should().NotBeNull();
            post!.Type.Should().Be(GalleryMediaType.Video, "media type is detected from the file, server-side");
            post.Url.Should().EndWith(".mp4");
            url = post.Url;
        }

        var publicHtml = await _factory.CreateClient().GetStringAsync("/gallery");
        publicHtml.Should().Contain($"data-type=\"Video\"");
        publicHtml.Should().Contain(url, "the uploaded video is publicly retrievable");
    }

    [Fact]
    public async Task Multi_image_pick_still_creates_one_ordered_carousel_post()
    {
        var client = await LoginAsOwnerAsync();
        var title = "E2E carousel " + Guid.NewGuid().ToString("n")[..8];

        var response = await PostCreateAsync(client, title,
            (Png1X1, "image/png", "shot-a.png"),
            (Png1X1, "image/png", "shot-b.png"));
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var post = await db.GalleryMedia.SingleOrDefaultAsync(m => m.Title == title && m.ParentId == null);
        post.Should().NotBeNull();
        post!.Type.Should().Be(GalleryMediaType.Image);
        var children = await db.GalleryMedia.CountAsync(m => m.ParentId == post.Id);
        children.Should().Be(1, "two images = one post row + one ordered child row");
    }

    [Fact]
    public async Task Mixed_image_and_video_selection_is_rejected_server_side()
    {
        var client = await LoginAsOwnerAsync();
        var title = "E2E mixed " + Guid.NewGuid().ToString("n")[..8];

        var response = await PostCreateAsync(client, title,
            (Png1X1, "image/png", "shot.png"),
            (FakeMp4, "video/mp4", "clip.mp4"));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the form re-renders with the validation error");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("not mixed");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        (await db.GalleryMedia.AnyAsync(m => m.Title == title)).Should().BeFalse();
    }

    [Fact]
    public async Task Multiple_videos_in_one_pick_are_rejected_server_side()
    {
        var client = await LoginAsOwnerAsync();
        var title = "E2E two videos " + Guid.NewGuid().ToString("n")[..8];

        var response = await PostCreateAsync(client, title,
            (FakeMp4, "video/mp4", "clip-a.mp4"),
            (FakeMp4, "video/mp4", "clip-b.mp4"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("single-media");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        (await db.GalleryMedia.AnyAsync(m => m.Title == title)).Should().BeFalse();
    }

    [Fact]
    public async Task Unsupported_extension_is_rejected_even_though_accept_is_only_a_hint()
    {
        var client = await LoginAsOwnerAsync();
        var title = "E2E avi " + Guid.NewGuid().ToString("n")[..8];

        var response = await PostCreateAsync(client, title, (FakeMp4, "video/x-msvideo", "movie.avi"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("not allowed");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        (await db.GalleryMedia.AnyAsync(m => m.Title == title)).Should().BeFalse();
    }

    [Fact]
    public async Task Oversized_video_is_rejected_server_side()
    {
        var client = await LoginAsOwnerAsync();
        var title = "E2E huge " + Guid.NewGuid().ToString("n")[..8];
        var oversized = new byte[10 * 1024 * 1024 + 1024]; // just over the 10 MB storage cap
        oversized.AsSpan().Fill((byte)'x');

        var response = await PostCreateAsync(client, title, (oversized, "video/mp4", "huge.mp4"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("File size must be between");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        (await db.GalleryMedia.AnyAsync(m => m.Title == title)).Should().BeFalse();
    }
}
