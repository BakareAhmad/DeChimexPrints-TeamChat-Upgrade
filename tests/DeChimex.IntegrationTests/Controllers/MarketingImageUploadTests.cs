using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeChimex.Infrastructure.Options;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>Campaign image upload endpoint: storage integration, validation, cleanup,
/// and the generated email referencing the public HTTPS URL (never embedded data).</summary>
public class MarketingImageUploadTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    public MarketingImageUploadTests(CustomWebApplicationFactory factory) => _factory = factory;

    private HttpClient CreateBrowser() => _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    private async Task<HttpClient> LoginAsOwnerAsync()
    {
        var client = CreateBrowser();
        if (!await TryOwnerLoginAsync(client, "ChangeMe123!"))
            (await TryOwnerLoginAsync(client, "Owner!Pass9")).Should().BeTrue("owner login should succeed");
        return client;
    }

    private static async Task<bool> TryOwnerLoginAsync(HttpClient client, string password)
    {
        var html = await (await client.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = "owner@dechimexprinthub.com",
            ["Password"] = password,
            ["__RequestVerificationToken"] = ExtractToken(html)
        }));
        if (response.StatusCode != HttpStatusCode.Redirect) return false;
        if ((response.Headers.Location?.ToString() ?? "").Contains("/admin/security/password"))
        {
            var page = await client.GetAsync("/admin/security/password");
            var changed = await client.PostAsync("/admin/security/password", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["CurrentPassword"] = password,
                ["NewPassword"] = "Owner!Pass9",
                ["ConfirmPassword"] = "Owner!Pass9",
                ["__RequestVerificationToken"] = ExtractToken(await page.Content.ReadAsStringAsync())
            }));
            changed.StatusCode.Should().Be(HttpStatusCode.Redirect);
        }
        return true;
    }

    private static string ExtractToken(string html)
    {
        var tag = Regex.Match(html, "<input[^>]*__RequestVerificationToken[^>]*>").Value;
        return Regex.Match(tag, "value=\"([^\"]+)\"").Groups[1].Value;
    }

    private static byte[] PngBytes() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static async Task<(HttpStatusCode Status, JsonDocument? Json, string Body)> UploadAsync(
        HttpClient client, string token, byte[] content, string fileName, string contentType, string? previousUrl = null)
    {
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(content);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(fileContent, "file", fileName);
        form.Add(new StringContent(token), "__RequestVerificationToken");
        if (previousUrl is not null) form.Add(new StringContent(previousUrl), "previousUrl");
        var response = await client.PostAsync("/admin/marketing/upload-image", form);
        var body = await response.Content.ReadAsStringAsync();
        JsonDocument? json = null;
        if (response.StatusCode == HttpStatusCode.OK) json = JsonDocument.Parse(body);
        return (response.StatusCode, json, body);
    }

    private async Task<string> UploadValidPngAsync(HttpClient client)
    {
        var token = ExtractToken(await (await client.GetAsync("/admin/marketing/compose")).Content.ReadAsStringAsync());
        var (status, json, body) = await UploadAsync(client, token, PngBytes(), "banner.png", "image/png");
        status.Should().Be(HttpStatusCode.OK, body);
        var url = json!.RootElement.GetProperty("url").GetString()!;
        url.Should().StartWith("https://").And.Contain("/uploads/marketingmedia/");
        json.RootElement.GetProperty("previewUrl").GetString()!.Should().StartWith("/uploads/marketingmedia/");
        return url;
    }

    private string StoredPathFor(string url)
    {
        var options = _factory.Services.GetRequiredService<IOptions<StorageOptions>>().Value;
        var fileName = url.Split('/').Last();
        return Path.Combine(options.LocalRootPath, "marketingmedia", fileName);
    }

    [Fact]
    public async Task Upload_ValidPng_StoresFileAndReturnsHttpsUrl()
    {
        var client = await LoginAsOwnerAsync();
        var url = await UploadValidPngAsync(client);
        File.Exists(StoredPathFor(url)).Should().BeTrue("the image should be stored under uploads/marketingmedia");
    }

    [Fact]
    public async Task Upload_NonImageContentType_Rejected()
    {
        var client = await LoginAsOwnerAsync();
        var token = ExtractToken(await (await client.GetAsync("/admin/marketing/compose")).Content.ReadAsStringAsync());
        var (status, _, body) = await UploadAsync(client, token, Encoding.UTF8.GetBytes("MZ not an image"), "payload.exe", "application/octet-stream");
        status.Should().Be(HttpStatusCode.BadRequest);
        body.Should().Contain("JPG, PNG, WEBP, or GIF");
    }

    [Fact]
    public async Task Upload_SvgContent_Rejected()
    {
        var client = await LoginAsOwnerAsync();
        var token = ExtractToken(await (await client.GetAsync("/admin/marketing/compose")).Content.ReadAsStringAsync());
        var (status, _, body) = await UploadAsync(client, token, Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'></svg>"), "art.svg", "image/svg+xml");
        status.Should().Be(HttpStatusCode.BadRequest);
        body.Should().Contain("JPG, PNG, WEBP, or GIF");
    }

    [Fact]
    public async Task Upload_ImageLabeledButDisallowedExtension_RejectedByStorage()
    {
        var client = await LoginAsOwnerAsync();
        var token = ExtractToken(await (await client.GetAsync("/admin/marketing/compose")).Content.ReadAsStringAsync());
        var (status, _, _) = await UploadAsync(client, token, PngBytes(), "photo.png.exe", "image/png");
        status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Upload_OversizedImage_Rejected()
    {
        var client = await LoginAsOwnerAsync();
        var token = ExtractToken(await (await client.GetAsync("/admin/marketing/compose")).Content.ReadAsStringAsync());
        var big = new byte[10 * 1024 * 1024 + 1];
        PngBytes().CopyTo(big, 0);
        var (status, _, body) = await UploadAsync(client, token, big, "huge.png", "image/png");
        status.Should().Be(HttpStatusCode.BadRequest);
        body.Should().Contain("10 MB");
    }

    [Fact]
    public async Task Upload_AnonymousAndMissingFile_Rejected()
    {
        var anonymous = CreateBrowser();
        var response = await anonymous.PostAsync("/admin/marketing/upload-image", new MultipartFormDataContent());
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Contain("/Account/Login");

        var client = await LoginAsOwnerAsync();
        var token = ExtractToken(await (await client.GetAsync("/admin/marketing/compose")).Content.ReadAsStringAsync());
        var (status, _, body) = await UploadAsync(client, token, Array.Empty<byte>(), "empty.png", "image/png");
        status.Should().Be(HttpStatusCode.BadRequest);
        body.Should().Contain("Choose an image");
    }

    [Fact]
    public async Task Upload_WithPreviousUrl_ReplacesStoredFile()
    {
        var client = await LoginAsOwnerAsync();
        var first = await UploadValidPngAsync(client);
        var token = ExtractToken(await (await client.GetAsync("/admin/marketing/compose")).Content.ReadAsStringAsync());
        var (status, json, body) = await UploadAsync(client, token, PngBytes(), "banner2.png", "image/png", first);
        status.Should().Be(HttpStatusCode.OK, body);
        var second = json!.RootElement.GetProperty("url").GetString()!;
        second.Should().NotBe(first);
        File.Exists(StoredPathFor(first)).Should().BeFalse("replacing an image should delete the previously stored file");
        File.Exists(StoredPathFor(second)).Should().BeTrue();
    }

    [Fact]
    public async Task RemoveImage_DeletesOnlyMarketingUploads()
    {
        var client = await LoginAsOwnerAsync();
        var url = await UploadValidPngAsync(client);
        var token = ExtractToken(await (await client.GetAsync("/admin/marketing/compose")).Content.ReadAsStringAsync());
        var remove = await client.PostAsync("/admin/marketing/remove-image", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["url"] = url,
            ["__RequestVerificationToken"] = token
        }));
        remove.StatusCode.Should().Be(HttpStatusCode.OK);
        File.Exists(StoredPathFor(url)).Should().BeFalse();

        // A non-marketing URL must never be touched by this endpoint.
        var galleryProbe = "/uploads/gallerymedia/should-not-exist.png";
        var ignored = await client.PostAsync("/admin/marketing/remove-image", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["url"] = galleryProbe,
            ["__RequestVerificationToken"] = token
        }));
        ignored.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UploadedUrl_FlowsIntoGeneratedEmailHtml_AsHttpsReference()
    {
        var client = await LoginAsOwnerAsync();
        var url = await UploadValidPngAsync(client);
        var blocksJson = JsonSerializer.Serialize(new
        {
            blocks = new object[]
            {
                new { type = "heading", text = "Look at this" },
                new { type = "image", url, alt = "Uploaded banner" }
            }
        });

        var token = ExtractToken(await (await client.GetAsync("/admin/marketing/compose")).Content.ReadAsStringAsync());
        var preview = await client.PostAsync("/admin/marketing/preview", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["subject"] = "Uploaded image campaign",
            ["preheader"] = "",
            ["blocksJson"] = blocksJson,
            ["__RequestVerificationToken"] = token
        }));
        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        using var previewDocument = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        var html = previewDocument.RootElement.GetProperty("htmlBody").GetString()!;
        html.Should().Contain($"src='{url}'");
        html.Should().NotContain("data:image");
        html.Should().NotContain("base64");
    }
}
