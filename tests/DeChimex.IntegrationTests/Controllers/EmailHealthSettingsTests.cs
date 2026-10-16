using System.Net;
using DeChimex.Application.Common.Interfaces;
using DeChimex.IntegrationTests.Fakes;
using DeChimex.Shared.Constants;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Email delivery status after the 2026-10-08 portal cleanup: business Settings shows a
/// plain-language status only ("Operational" / "issue — technical contact notified"), while
/// vendor diagnostics and the provider test send live exclusively in the Developer Portal
/// (/dev/email). Business users must never see vendor troubleshooting, server IPs or the
/// old admin test-send endpoint.
/// </summary>
public class EmailHealthSettingsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public EmailHealthSettingsTests(CustomWebApplicationFactory factory) => _factory = factory;

    private HttpClient CreateBrowser() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

    private async Task<HttpClient> OwnerBrowserAsync() =>
        await TestSeed.LoginOwnerMvcAsync(_factory.Services, CreateBrowser);

    [Fact]
    public async Task SettingsPage_ShowsPlainLanguageStatus_WithoutVendorTooling()
    {
        var owner = await OwnerBrowserAsync();
        var page = await owner.GetAsync("/admin/settings");
        page.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await page.Content.ReadAsStringAsync();
        body.Should().Contain("Email delivery");
        body.Should().Contain("Operational", "the fake health check reports healthy");
        body.Should().Contain("being delivered normally");
        body.Should().NotContain("Send test email", "the provider test send moved to the Developer Portal");
        body.Should().NotContain("Authorised IPs", "vendor troubleshooting must not face business users");
    }

    [Fact]
    public async Task SettingsPage_WhenProviderBlocked_ShowsPlainLanguageIssue_NotTechnicalDetail()
    {
        var health = (FakeEmailHealthCheck)_factory.Services.GetRequiredService<IEmailHealthCheck>();
        health.Result = new EmailHealthResult(false, "Brevo is blocking this server's IP address",
            "We have detected you are using an unrecognised IP address 1.2.3.4.",
            "https://app.brevo.com/security/authorised_ips");
        try
        {
            var owner = await OwnerBrowserAsync();
            var body = await (await owner.GetAsync("/admin/settings")).Content.ReadAsStringAsync();
            body.Should().Contain("Email delivery is currently experiencing an issue");
            body.Should().Contain("technical contact has been notified");
            body.Should().NotContain("1.2.3.4", "provider detail stays in the Developer Portal");
            body.Should().NotContain("app.brevo.com", "vendor fix links stay in the Developer Portal");
        }
        finally
        {
            health.Result = new EmailHealthResult(true, "Connected to Brevo (test fake)", "Fake provider status for tests.");
        }
    }

    [Fact]
    public async Task EmailTest_BusinessEndpointRemoved_DevPortalEndpointSendsToSignedInDeveloper()
    {
        var owner = await OwnerBrowserAsync();
        var gone = await owner.GetAsync("/admin/settings/email-test");
        gone.StatusCode.Should().Be(HttpStatusCode.NotFound, "the business-facing test send no longer exists");

        // A developer-role account keeps the capability through the Dev Portal.
        var devEmail = $"dev.{Guid.NewGuid():N}@dechimex.test";
        await TestSeed.CreateStaffUserAsync(_factory.Services, devEmail, TestSeed.DefaultPassword, RoleNames.Developer);
        var dev = CreateBrowser();
        (await TestSeed.LoginMvcAsync(dev, devEmail, TestSeed.DefaultPassword)).Should().BeTrue();

        var post = await DeChimex.IntegrationTests.DevPortal.DevPortalFactory.PostFormAsync(dev, "/dev/email", "/dev/email/test", new());
        post.StatusCode.Should().Be(HttpStatusCode.Redirect);
        post.Headers.Location!.ToString().Should().Contain("/dev/email");

        var health = (FakeEmailHealthCheck)_factory.Services.GetRequiredService<IEmailHealthCheck>();
        health.LastTestRecipient.Should().Be(devEmail, "the dev test send goes to the signed-in developer");
    }

    [Fact]
    public async Task RawAuditTrail_BusinessRouteGone_OwnerDenied_DevCanRead()
    {
        var owner = await OwnerBrowserAsync();
        (await owner.GetAsync("/admin/audit/raw")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var ownerDev = await owner.GetAsync("/dev/audit");
        // Business roles never hold dev.audit.view, so any of the denial outcomes is acceptable.
        ownerDev.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Redirect, HttpStatusCode.NotFound,
            HttpStatusCode.Unauthorized);

        var devEmail = $"audit.{Guid.NewGuid():N}@dechimex.test";
        await TestSeed.CreateStaffUserAsync(_factory.Services, devEmail, TestSeed.DefaultPassword, RoleNames.Developer);
        var dev = CreateBrowser();
        (await TestSeed.LoginMvcAsync(dev, devEmail, TestSeed.DefaultPassword)).Should().BeTrue();
        var page = await dev.GetAsync("/dev/audit");
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        (await page.Content.ReadAsStringAsync()).Should().Contain("Audit trail");
    }
}
