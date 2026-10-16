using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Features.Categories;
using DeChimex.Application.Features.Categories.Queries;
using DeChimex.Host.Models.Admin;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace DeChimex.Host.Controllers.Mvc.Admin;

[Authorize(Policy = Permissions.Categories.View)]
[Route("admin/major-categories")]
public sealed class AdminMajorCategoriesController : MvcControllerBase
{
    private void Page(string title)
    {
        ViewData["Title"] = title; ViewData["Heading"] = "Categories"; ViewData["ActiveNav"] = "categories";
        ViewBag.CanManage = User.HasClaim(Permissions.ClaimType, Permissions.Categories.Manage);
    }
    // The standalone major-categories list was merged into the single Categories tree page
    // (2026-10-08 portal cleanup). These routes now redirect so old links keep working.
    [HttpGet]
    public IActionResult Index() => RedirectToAction("Index", "AdminCategories");
    [HttpGet("{id:guid}")]
    public IActionResult Details(Guid id) => RedirectToAction("Index", "AdminCategories", new { majorCategoryId = id });
    [HttpGet("create")]
    [Authorize(Policy = Permissions.Categories.Manage)]
    public IActionResult Create() { Page("New top-level category"); return View("Form", new MajorCategoryFormViewModel()); }
    [HttpGet("edit/{id:guid}")]
    [Authorize(Policy = Permissions.Categories.Manage)]
    public async Task<IActionResult> Edit(Guid id)
    {
        var major = (await Sender.Send(new GetMajorCategoriesQuery())).FirstOrDefault(m => m.Id == id);
        if (major is null) return NotFound();
        Page("Edit top-level category");
        return View("Form", new MajorCategoryFormViewModel { Id = id, Name = major.Name, Description = major.Description });
    }
    [HttpPost("create")]
    [Authorize(Policy = Permissions.Categories.Manage)]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Create(MajorCategoryFormViewModel model) { model.Id = null; return Save(model); }
    [HttpPost("edit/{id:guid}")]
    [Authorize(Policy = Permissions.Categories.Manage)]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Edit(Guid id, MajorCategoryFormViewModel model) { model.Id = id; return Save(model); }
    private async Task<IActionResult> Save(MajorCategoryFormViewModel model)
    {
        Page(model.Id is null ? "New top-level category" : "Edit top-level category");
        if (!ModelState.IsValid) return View("Form", model);
        try
        {
            var id = await Sender.Send(new SaveMajorCategoryCommand(model.Id, model.Name, model.Description));
            TempData["Success"] = "Category saved.";
            return RedirectToAction("Index", "AdminCategories", new { majorCategoryId = id });
        }
        catch (ConflictException ex) { ModelState.AddModelError("", ex.Message); return View("Form", model); }
        catch (NotFoundException) { return NotFound(); }
    }
    [HttpPost("delete/{id:guid}")]
    [Authorize(Policy = Permissions.Categories.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id)
    {
        try { await Sender.Send(new DeleteMajorCategoryCommand(id)); TempData["Success"] = "Top-level category deleted."; }
        catch (ConflictException ex) { TempData["Error"] = ex.Message; }
        catch (NotFoundException) { return NotFound(); }
        return RedirectToAction("Index", "AdminCategories");
    }
}
