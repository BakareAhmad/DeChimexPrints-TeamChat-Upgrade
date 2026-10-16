using DeChimex.Application.Common.Interfaces;
using DeChimex.Host.DevPortal;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc.Dev;

/// <summary>Integration, payment and email diagnostics (dev.payments.view).</summary>
[DevPermission(Permissions.Developer.PaymentsView)]
public sealed class DevIntegrationsController(DevPortalQueries queries, DevHealthService health, IEmailHealthCheck emailHealth) : DevControllerBase
{
    /// <summary>
    /// Provider test send for the technical owner of the integration. The business Settings page
    /// deliberately no longer offers this (2026-10-08 portal cleanup): vendor verdicts belong here.
    /// </summary>
    [HttpPost("/dev/email/test")]
    public Task<IActionResult> EmailTest(CancellationToken ct) => UnlessClientAborted(async () =>
    {
        var me = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
        if (string.IsNullOrWhiteSpace(me))
        {
            TempData["Error"] = "Could not determine your email address for the test.";
            return Redirect("/dev/email");
        }

        var result = await emailHealth.CheckAsync(me, ct);
        TempData[result.Healthy ? "Success" : "Error"] =
            result.Detail is null ? result.Status : $"{result.Status} - {result.Detail}";
        return Redirect("/dev/email");
    });

    [HttpGet("/dev/integrations")]
    public Task<IActionResult> Index(string? range, string? from, string? to, CancellationToken ct) => UnlessClientAborted(async () =>
    {
        ViewData["Title"] = "Integrations";
        ViewData["DevNav"] = "integrations";
        var r = DevRange.Parse(range, from, to);
        var snapshot = await health.GetAsync(false, ct);
        ViewBag.Range = r;
        var list = new[]
        {
            await queries.IntegrationAsync(IntegrationNames.Paystack, r, snapshot, ct),
            await queries.IntegrationAsync(IntegrationNames.Brevo, r, snapshot, ct)
        };
        return DevView("Integrations", list);
    });

    [HttpGet("/dev/payments")]
    public Task<IActionResult> Payments(string? range, string? from, string? to, CancellationToken ct) => UnlessClientAborted(async () =>
    {
        ViewData["Title"] = "Payment diagnostics";
        ViewData["DevNav"] = "payments";
        return DevView("Payments", await queries.PaymentsAsync(DevRange.Parse(range, from, to, "7d"), ct));
    });

    [HttpGet("/dev/email")]
    public Task<IActionResult> Email(string? range, string? from, string? to, CancellationToken ct) => UnlessClientAborted(async () =>
    {
        ViewData["Title"] = "Email diagnostics";
        ViewData["DevNav"] = "email";
        return DevView("Email", await queries.EmailAsync(DevRange.Parse(range, from, to, "7d"), ct));
    });
}
