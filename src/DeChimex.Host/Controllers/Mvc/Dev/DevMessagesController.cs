using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.InternalMessages;
using DeChimex.Contracts.InternalMessages;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Exceptions;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Mvc;

namespace DeChimex.Host.Controllers.Mvc.Dev;

/// <summary>
/// The developer side of internal (work) messaging: the developer may hold one-to-one
/// conversations with the Owner and Managers only (see InternalMessaging). Lives in the
/// Developer Portal with its own layout and live permission re-checks; business roles
/// use /admin/team-messages instead. Customers can never reach either.
/// </summary>
[Route("dev/messages")]
public sealed class DevMessagesController(IFileStorageService storage) : DevControllerBase
{
    private bool IsDeveloper => InternalMessaging.CategoryFor(CurrentUser.Roles) == InternalMessaging.Developer;

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        if (!IsDeveloper) return Forbid();
        if (CurrentUser.UserId is not Guid userId) return Challenge();

        ViewData["Title"] = "Messages";
        ViewData["DevNav"] = "messages";
        ViewBag.DevPortal = true;
        ViewBag.MyUserId = userId;
        ViewBag.MyCategory = InternalMessaging.Developer;
        ViewBag.Conversations = await Sender.Send(new GetInternalConversationsQuery(userId));
        ViewBag.Contacts = await Sender.Send(new GetTeamContactsQuery(userId));
        return View("~/Views/TeamMessages/Index.cshtml");
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Conversation(Guid id)
    {
        if (!IsDeveloper) return Forbid();
        if (CurrentUser.UserId is not Guid userId) return Challenge();

        await Sender.Send(new MarkInternalConversationReadCommand(userId, id));
        var detail = await Sender.Send(new GetInternalConversationQuery(userId, id));

        ViewData["Title"] = $"Conversation with {detail.OtherName}";
        ViewData["DevNav"] = "messages";
        ViewBag.DevPortal = true;
        ViewBag.MyUserId = userId;
        ViewBag.TeamMessagesBase = "/dev/messages";
        return View("~/Views/TeamMessages/Conversation.cshtml", detail);
    }

    [HttpPost("start")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Start([FromForm] Guid otherUserId)
    {
        if (!IsDeveloper) return Forbid();
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
        if (!IsDeveloper) return Forbid();
        if (CurrentUser.UserId is not Guid userId) return Challenge();

        var wantsJson = ChatSendSupport.WantsJson(Request);
        IReadOnlyList<UploadedChatAttachment> uploaded = Array.Empty<UploadedChatAttachment>();
        var persisted = false;
        try
        {
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

    [HttpGet("{id:guid}/messages")]
    public async Task<IActionResult> Messages(Guid id, [FromQuery] DateTime? afterUtc)
    {
        if (!IsDeveloper) return Forbid();
        if (CurrentUser.UserId is not Guid userId) return Challenge();
        var detail = await Sender.Send(new GetInternalConversationQuery(userId, id));
        var floor = afterUtc?.ToUniversalTime().AddSeconds(-2);
        var messages = floor is null
            ? detail.Messages
            : detail.Messages.Where(m => m.SentAtUtc >= floor.Value).ToList();
        return Json(new { messages });
    }

    [HttpPost("{id:guid}/read")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkRead(Guid id)
    {
        if (!IsDeveloper) return Forbid();
        if (CurrentUser.UserId is not Guid userId) return Challenge();
        await Sender.Send(new MarkInternalConversationReadCommand(userId, id));
        return Json(new { ok = true });
    }

    [HttpGet("unread-count")]
    public async Task<IActionResult> UnreadCount()
    {
        if (!IsDeveloper) return Json(new { count = 0 });
        var count = CurrentUser.UserId is Guid userId
            ? await Sender.Send(new GetUnreadInternalMessageCountQuery(userId))
            : 0;
        return Json(new { count });
    }
}
