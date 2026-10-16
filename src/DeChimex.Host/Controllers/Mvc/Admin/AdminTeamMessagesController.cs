using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.InternalMessages;
using DeChimex.Contracts.InternalMessages;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Exceptions;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc.Admin;

/// <summary>
/// Internal (work) messaging for the business roles: Owner, Manager and Staff.
/// The pairing rules are enforced server-side here AND in the application layer;
/// developers use their own portal side (/dev/messages) and customers never pass the
/// [Authorize] + category guard. Kept fully separate from customer/order conversations.
/// </summary>
[Authorize]
[Route("admin/team-messages")]
public sealed class AdminTeamMessagesController(IFileStorageService storage) : MvcControllerBase
{
    private string? Category => InternalMessaging.CategoryFor(CurrentUser.Roles);
    private bool IsBusinessRole => Category is InternalMessaging.Owner or InternalMessaging.Manager or InternalMessaging.Staff;

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        if (!IsBusinessRole) return Forbid();
        if (CurrentUser.UserId is not Guid userId) return Challenge();

        ViewData["Title"] = "Team messages";
        ViewData["Heading"] = "Team messages";
        ViewData["ActiveNav"] = "team-messages";
        ViewBag.DevPortal = false;
        ViewBag.MyUserId = userId;
        ViewBag.MyCategory = Category;
        ViewBag.Conversations = await Sender.Send(new GetInternalConversationsQuery(userId));
        ViewBag.Contacts = await Sender.Send(new GetTeamContactsQuery(userId));
        return View("~/Views/TeamMessages/Index.cshtml");
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Conversation(Guid id)
    {
        if (!IsBusinessRole) return Forbid();
        if (CurrentUser.UserId is not Guid userId) return Challenge();

        await Sender.Send(new MarkInternalConversationReadCommand(userId, id));
        var detail = await Sender.Send(new GetInternalConversationQuery(userId, id));

        ViewData["Title"] = $"Conversation with {detail.OtherName}";
        ViewData["Heading"] = "Team messages";
        ViewData["ActiveNav"] = "team-messages";
        ViewBag.DevPortal = false;
        ViewBag.MyUserId = userId;
        ViewBag.TeamMessagesBase = "/admin/team-messages";
        return View("~/Views/TeamMessages/Conversation.cshtml", detail);
    }

    [HttpPost("start")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Start([FromForm] Guid otherUserId)
    {
        if (!IsBusinessRole) return Forbid();
        if (CurrentUser.UserId is not Guid userId) return Challenge();

        try
        {
            var conversationId = await Sender.Send(new StartInternalConversationCommand(userId, otherUserId));
            return RedirectToAction(nameof(Conversation), new { id = conversationId });
        }
        catch (NotFoundException)
        {
            TempData["Error"] = "You are not allowed to start a conversation with that user.";
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpPost("{id:guid}/send")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(70_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 70_000_000)]
    public async Task<IActionResult> Send(Guid id,
        [FromForm] string? body,
        [FromForm] List<IFormFile>? files,
        [FromForm] List<string>? kinds,
        [FromForm] List<string>? durations,
        [FromForm] Guid? clientMessageId)
    {
        if (!IsBusinessRole) return Forbid();
        if (CurrentUser.UserId is not Guid userId) return Challenge();

        var wantsJson = ChatSendSupport.WantsJson(Request);
        IReadOnlyList<UploadedChatAttachment> uploaded = Array.Empty<UploadedChatAttachment>();
        var persisted = false;
        try
        {
            // Resolve authorization and retry state before touching storage. A replay with
            // the same client id returns the original row without uploading its files again.
            var current = await Sender.Send(new GetInternalConversationQuery(userId, id));
            if (clientMessageId is Guid key && key != Guid.Empty)
            {
                var duplicate = current.Messages.FirstOrDefault(m =>
                    m.ClientMessageId == key && m.SenderId == userId);
                if (duplicate is not null)
                    return wantsJson ? Json(new { message = duplicate, duplicate = true })
                        : RedirectToAction(nameof(Conversation), new { id });
            }

            uploaded = await ChatMediaUploadSupport.UploadAsync(storage, files, kinds, durations,
                FileCategory.TeamChatMedia, HttpContext.RequestAborted);
            var inputs = uploaded.Select(a => new InternalMessageAttachmentInput(
                a.Kind, a.OriginalFileName, a.StoredFileName, a.Url, a.ContentType,
                a.SizeBytes, a.DurationSeconds)).ToList();

            var messageId = await Sender.Send(new SendInternalMessageCommand(
                userId, id, body ?? string.Empty, inputs, clientMessageId));
            persisted = true;

            var detailAfterSend = await Sender.Send(new GetInternalConversationQuery(userId, id));
            var savedMessage = detailAfterSend.Messages.FirstOrDefault(m => m.Id == messageId);
            if (savedMessage is null)
                return StatusCode(StatusCodes.Status500InternalServerError,
                    new { error = "The message was saved but could not be loaded." });

            // In the narrow race where the same client id was committed by another
            // request after the pre-upload check, retain only files actually referenced
            // by the persisted message; the unique idempotency index prevents duplicates.
            if (uploaded.Count > 0)
            {
                var referenced = savedMessage.Attachments.Select(a => a.Url).ToHashSet(StringComparer.Ordinal);
                var unreferenced = uploaded.Where(a => !referenced.Contains(a.Url)).ToList();
                await ChatMediaUploadSupport.DeleteBestEffortAsync(storage, unreferenced, HttpContext.RequestAborted);
            }

            if (wantsJson) return Json(new { message = savedMessage });
            return RedirectToAction(nameof(Conversation), new { id });
        }
        catch (ValidationAppException ex)
        {
            if (!persisted) await ChatMediaUploadSupport.DeleteBestEffortAsync(storage, uploaded, HttpContext.RequestAborted);
            var error = ex.Errors.Values.SelectMany(v => v).FirstOrDefault() ?? "The message could not be sent.";
            if (wantsJson) return UnprocessableEntity(new { error });
            TempData["Error"] = error;
        }
        catch (ConflictException ex)
        {
            if (!persisted) await ChatMediaUploadSupport.DeleteBestEffortAsync(storage, uploaded, HttpContext.RequestAborted);
            if (wantsJson) return UnprocessableEntity(new { error = ex.Message });
            TempData["Error"] = ex.Message;
        }
        catch (BusinessRuleValidationException ex)
        {
            if (!persisted) await ChatMediaUploadSupport.DeleteBestEffortAsync(storage, uploaded, HttpContext.RequestAborted);
            if (wantsJson) return UnprocessableEntity(new { error = ex.Message });
            TempData["Error"] = ex.Message;
        }
        catch (NotFoundException)
        {
            if (!persisted) await ChatMediaUploadSupport.DeleteBestEffortAsync(storage, uploaded, HttpContext.RequestAborted);
            if (wantsJson) return NotFound(new { error = "This conversation is not available." });
            TempData["Error"] = "This conversation is not available.";
            return RedirectToAction(nameof(Index));
        }
        catch
        {
            if (!persisted) await ChatMediaUploadSupport.DeleteBestEffortAsync(storage, uploaded, HttpContext.RequestAborted);
            throw;
        }
        return RedirectToAction(nameof(Conversation), new { id });
    }

    /// <summary>Persisted-message reconciliation after SignalR reconnect or a missed push.</summary>
    [HttpGet("{id:guid}/messages")]
    public async Task<IActionResult> Messages(Guid id, [FromQuery] DateTime? afterUtc)
    {
        if (!IsBusinessRole) return Forbid();
        if (CurrentUser.UserId is not Guid userId) return Challenge();
        var detail = await Sender.Send(new GetInternalConversationQuery(userId, id));
        var floor = afterUtc?.ToUniversalTime().AddSeconds(-2); // overlap catches equal-timestamp writes; client dedupes ids
        var messages = floor is null
            ? detail.Messages
            : detail.Messages.Where(m => m.SentAtUtc >= floor.Value).ToList();
        return Json(new { messages });
    }

    /// <summary>JS companion: clears unread state when the recipient has the page open.</summary>
    [HttpPost("{id:guid}/read")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkRead(Guid id)
    {
        if (!IsBusinessRole) return Forbid();
        if (CurrentUser.UserId is not Guid userId) return Challenge();
        await Sender.Send(new MarkInternalConversationReadCommand(userId, id));
        return Json(new { ok = true });
    }

    /// <summary>Sidebar badge count for the current business user.</summary>
    [HttpGet("unread-count")]
    public async Task<IActionResult> UnreadCount()
    {
        if (!IsBusinessRole) return Json(new { count = 0 });
        var count = CurrentUser.UserId is Guid userId
            ? await Sender.Send(new GetUnreadInternalMessageCountQuery(userId))
            : 0;
        return Json(new { count });
    }
}
