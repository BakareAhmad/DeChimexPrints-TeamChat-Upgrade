using DeChimex.Application.Features.Announcements.Commands;
using DeChimex.Application.Features.Announcements.Queries;
using DeChimex.Host.Models.Admin;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc.Admin;

/// <summary>
/// Admin management of the customer-facing announcement ticker (owner directive
/// 2026-10-07). Mirrors AdminReviewsController: list + create + edit + activate/
/// deactivate + delete over the Application commands, guarded by the
/// announcements.view / announcements.manage permission policies. Deliberately a
/// separate, small surface — it does NOT touch the per-user notification system.
/// </summary>
[Authorize(Policy = Permissions.Announcements.View)]
[Route("admin/announcements")]
public sealed class AdminAnnouncementsController : MvcControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Announcements";
        ViewData["Heading"] = "Announcements";
        ViewData["ActiveNav"] = "announcements";

        var announcements = await Sender.Send(new GetAnnouncementsQuery(VisibleOnly: false));
        ViewBag.CanManage = User.HasClaim(Permissions.ClaimType, Permissions.Announcements.Manage);
        ViewBag.NowUtc = DateTime.UtcNow;
        return View(announcements);
    }

    [HttpGet("create")]
    [Authorize(Policy = Permissions.Announcements.Manage)]
    public IActionResult Create()
    {
        ViewData["Title"] = "New announcement";
        ViewData["Heading"] = "Announcements";
        ViewData["ActiveNav"] = "announcements";
        return View(new AnnouncementFormViewModel { IsActive = true });
    }

    [HttpPost("create")]
    [Authorize(Policy = Permissions.Announcements.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(AnnouncementFormViewModel model)
    {
        ViewData["Title"] = "New announcement";
        ViewData["Heading"] = "Announcements";
        ViewData["ActiveNav"] = "announcements";

        if (!ModelState.IsValid) return View(model);

        await Sender.Send(new CreateAnnouncementCommand(new DeChimex.Contracts.Announcements.CreateAnnouncementRequest(
            model.Message.Trim(), model.StartAt, model.EndAt, model.IsActive)));

        TempData["Success"] = model.IsActive
            ? "Announcement saved and live on the customer app."
            : "Announcement saved (inactive — customers will not see it until activated).";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("edit/{id:guid}")]
    [Authorize(Policy = Permissions.Announcements.Manage)]
    public async Task<IActionResult> Edit(Guid id)
    {
        var a = await Sender.Send(new GetAnnouncementByIdQuery(id));
        if (a is null) return NotFound();

        ViewData["Title"] = "Edit announcement";
        ViewData["Heading"] = "Announcements";
        ViewData["ActiveNav"] = "announcements";
        return View(new AnnouncementFormViewModel
        {
            Id = a.Id,
            Message = a.Message,
            StartAt = a.StartAtUtc,
            EndAt = a.EndAtUtc,
            IsActive = a.IsActive
        });
    }

    [HttpPost("edit/{id:guid}")]
    [Authorize(Policy = Permissions.Announcements.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, AnnouncementFormViewModel model)
    {
        ViewData["Title"] = "Edit announcement";
        ViewData["Heading"] = "Announcements";
        ViewData["ActiveNav"] = "announcements";

        if (!ModelState.IsValid) return View(model);

        await Sender.Send(new UpdateAnnouncementCommand(new DeChimex.Contracts.Announcements.UpdateAnnouncementRequest(
            id, model.Message.Trim(), model.StartAt, model.EndAt)));
        await Sender.Send(new SetAnnouncementActiveCommand(id, model.IsActive));

        TempData["Success"] = "Announcement updated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("toggle/{id:guid}")]
    [Authorize(Policy = Permissions.Announcements.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Toggle(Guid id, bool active)
    {
        await Sender.Send(new SetAnnouncementActiveCommand(id, active));
        TempData["Success"] = active ? "Announcement activated." : "Announcement deactivated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("delete/{id:guid}")]
    [Authorize(Policy = Permissions.Announcements.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id)
    {
        await Sender.Send(new DeleteAnnouncementCommand(id));
        TempData["Success"] = "Announcement deleted.";
        return RedirectToAction(nameof(Index));
    }
}
