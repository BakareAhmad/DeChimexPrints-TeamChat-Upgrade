using DeChimex.Application.Common.Interfaces;
using DeChimex.Contracts.Orders;
using Microsoft.AspNetCore.Http;

namespace DeChimex.Host.Controllers.Mvc;

/// <summary>Read-beacon body: ids the client actually rendered while visible.</summary>
public sealed record ChatMarkReadRequest(IReadOnlyList<Guid> Ids);

/// <summary>
/// Order-workspace adapter for the shared chat-media upload helper. The upload layer
/// validates configured type/size limits; the order feature still stores its own
/// OrderMessageAttachment rows and order-specific authorization.
/// </summary>
internal static class ChatSendSupport
{
    /// <summary>Fetch submits ask for JSON; plain form posts keep the PRG redirect,
    /// so the conversation still works with JavaScript disabled.</summary>
    public static bool WantsJson(HttpRequest request)
        => request.Headers.Accept.Any(h =>
            h?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true);

    public static async Task<IReadOnlyList<ChatMessageAttachmentInput>> UploadAttachmentsAsync(
        IFileStorageService storage,
        IList<IFormFile>? files,
        IList<string>? kinds,
        IList<string>? durations,
        CancellationToken ct)
    {
        var uploaded = await ChatMediaUploadSupport.UploadAsync(
            storage, files, kinds, durations, FileCategory.ChatMedia, ct);
        return uploaded.Select(a => new ChatMessageAttachmentInput(
            a.Kind, a.OriginalFileName, a.StoredFileName, a.Url, a.ContentType,
            a.SizeBytes, a.DurationSeconds)).ToList();
    }
}
