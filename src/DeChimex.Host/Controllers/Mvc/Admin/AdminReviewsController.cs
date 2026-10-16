using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Testimonials.Commands;
using DeChimex.Application.Features.Testimonials.Queries;
using DeChimex.Contracts.Reviews;
using DeChimex.Host.Models.Admin;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc.Admin;

/// <summary>
/// Admin management of the Home page customer reviews (the review slider). Mirrors
/// AdminServicesController: list + create + edit + delete over the Application commands,
/// guarded by the reviews.view / reviews.manage permission policies.
/// </summary>
[Authorize(Policy = Permissions.Reviews.View)]
[Route("admin/reviews")]
public sealed class AdminReviewsController(IFileStorageService storage) : MvcControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Customer reviews";
        ViewData["Heading"] = "Customer reviews";
        ViewData["ActiveNav"] = "reviews";

        var reviews = await Sender.Send(new GetTestimonialsQuery(PublishedOnly: false));
        ViewBag.CanManage = User.HasClaim(Permissions.ClaimType, Permissions.Reviews.Manage);
        return View(reviews);
    }

    [HttpGet("create")]
    [Authorize(Policy = Permissions.Reviews.Manage)]
    public IActionResult Create()
    {
        ViewData["Title"] = "New review";
        ViewData["Heading"] = "Customer reviews";
        ViewData["ActiveNav"] = "reviews";
        return View(new ReviewFormViewModel { Rating = 5, IsPublished = true });
    }

    [HttpPost("create")]
    [Authorize(Policy = Permissions.Reviews.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ReviewFormViewModel model)
    {
        ViewData["Title"] = "New review";
        ViewData["Heading"] = "Customer reviews";
        ViewData["ActiveNav"] = "reviews";

        if (!ModelState.IsValid) return View(model);

        var (avatar, error) = await ResolveAvatarAsync(model);
        if (error is not null)
        {
            ModelState.AddModelError(nameof(model.AvatarFile), error);
            return View(model);
        }

        await Sender.Send(new CreateTestimonialCommand(new CreateTestimonialRequest(
            model.CustomerName.Trim(), model.CustomerTitle, model.Body.Trim(),
            avatar, model.Rating, model.IsPublished, model.DisplayOrder)));

        TempData["Success"] = "Review added to the home page slider.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("edit/{id:guid}")]
    [Authorize(Policy = Permissions.Reviews.Manage)]
    public async Task<IActionResult> Edit(Guid id)
    {
        var review = await Sender.Send(new GetTestimonialByIdQuery(id));
        if (review is null) return NotFound();

        ViewData["Title"] = "Edit review";
        ViewData["Heading"] = "Customer reviews";
        ViewData["ActiveNav"] = "reviews";
        return View(new ReviewFormViewModel
        {
            Id = review.Id,
            CustomerName = review.CustomerName,
            CustomerTitle = review.CustomerTitle,
            Body = review.Body,
            AvatarUrl = review.AvatarUrl,
            Rating = review.Rating,
            IsPublished = review.IsPublished,
            DisplayOrder = review.DisplayOrder
        });
    }

    [HttpPost("edit/{id:guid}")]
    [Authorize(Policy = Permissions.Reviews.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, ReviewFormViewModel model)
    {
        ViewData["Title"] = "Edit review";
        ViewData["Heading"] = "Customer reviews";
        ViewData["ActiveNav"] = "reviews";

        if (!ModelState.IsValid) return View(model);

        var (avatar, error) = await ResolveAvatarAsync(model);
        if (error is not null)
        {
            ModelState.AddModelError(nameof(model.AvatarFile), error);
            return View(model);
        }

        try
        {
            await Sender.Send(new UpdateTestimonialCommand(id, new UpdateTestimonialRequest(
                model.CustomerName.Trim(), model.CustomerTitle, model.Body.Trim(),
                avatar, model.Rating, model.IsPublished, model.DisplayOrder)));
        }
        catch (NotFoundException)
        {
            return NotFound();
        }

        TempData["Success"] = "Review updated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("delete/{id:guid}")]
    [Authorize(Policy = Permissions.Reviews.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id)
    {
        try
        {
            await Sender.Send(new DeleteTestimonialCommand(id));
            TempData["Success"] = "Review deleted.";
        }
        catch (NotFoundException)
        {
            TempData["Error"] = "Review not found.";
        }
        catch (DeChimex.Domain.Exceptions.BusinessRuleValidationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Optional avatar: an uploaded file wins over a typed URL, and clearing both leaves the
    /// review without an image (the slider then shows the customer's initial). Public storefront
    /// media, so it uses the same storage category as gallery/product imagery.
    /// </summary>
    private async Task<(string? Url, string? Error)> ResolveAvatarAsync(ReviewFormViewModel model)
    {
        if (model.AvatarFile is not { Length: > 0 })
            return (string.IsNullOrWhiteSpace(model.AvatarUrl) ? null : model.AvatarUrl.Trim(), null);

        try
        {
            using var stream = model.AvatarFile.OpenReadStream();
            var uploaded = await storage.UploadAsync(new FileUploadRequest(
                stream, model.AvatarFile.FileName, model.AvatarFile.ContentType,
                model.AvatarFile.Length, FileCategory.GalleryMedia));
            return (uploaded.Url, null);
        }
        catch (ValidationAppException ex)
        {
            return (null, ex.Errors.Values.SelectMany(v => v).FirstOrDefault() ?? "That image was rejected.");
        }
    }
}
