using DeChimex.Domain.Common;
using DeChimex.Domain.Exceptions;

namespace DeChimex.Domain.Entities;

/// <summary>
/// Private media owned by one internal work message. This is intentionally distinct
/// from OrderMessageAttachment: internal files must never inherit customer/order access.
/// The raw storage key stays server-side; clients receive only the authorized URL.
/// </summary>
public sealed class InternalMessageAttachment : Entity
{
    private InternalMessageAttachment() { } // EF

    private InternalMessageAttachment(Guid messageId, string kind, string originalFileName,
        string storedFileName, string url, string contentType, long sizeBytes, int? durationSeconds)
    {
        MessageId = messageId;
        Kind = kind;
        OriginalFileName = originalFileName;
        StoredFileName = storedFileName;
        Url = url;
        ContentType = contentType;
        SizeBytes = sizeBytes;
        DurationSeconds = durationSeconds;
    }

    public Guid MessageId { get; private set; }
    public string Kind { get; private set; } = default!;
    public string OriginalFileName { get; private set; } = default!;
    public string StoredFileName { get; private set; } = default!;
    public string Url { get; private set; } = default!;
    public string ContentType { get; private set; } = default!;
    public long SizeBytes { get; private set; }
    public int? DurationSeconds { get; private set; }

    public static InternalMessageAttachment Create(Guid messageId, string kind, string originalFileName,
        string storedFileName, string url, string contentType, long sizeBytes, int? durationSeconds)
    {
        if (messageId == Guid.Empty)
            throw new BusinessRuleValidationException("internal.chat.attachment.message", "A message is required.");
        if (kind is not ("image" or "video" or "audio" or "voice" or "file"))
            throw new BusinessRuleValidationException("internal.chat.attachment.kind", "Unknown attachment kind.");
        if (string.IsNullOrWhiteSpace(storedFileName) ||
            !storedFileName.StartsWith("teamchatmedia/", StringComparison.OrdinalIgnoreCase) ||
            storedFileName.Contains("..", StringComparison.Ordinal) ||
            storedFileName.Contains((char)92))
            throw new BusinessRuleValidationException("internal.chat.attachment.storage", "The stored file is invalid.");
        if (!IsSafeUrl(url))
            throw new BusinessRuleValidationException("internal.chat.attachment.url", "The attachment URL is invalid.");
        if (sizeBytes <= 0)
            throw new BusinessRuleValidationException("internal.chat.attachment.size", "The attachment is empty.");
        if (durationSeconds is < 1 or > 3600)
            throw new BusinessRuleValidationException("internal.chat.attachment.duration", "Invalid attachment duration.");
        if (durationSeconds is not null && kind is not ("audio" or "voice"))
            throw new BusinessRuleValidationException("internal.chat.attachment.duration", "Only audio attachments carry a duration.");

        var rawName = (originalFileName ?? string.Empty).Replace((char)92, '/');
        var fileName = rawName[(rawName.LastIndexOf('/') + 1)..];
        if (fileName.Length > 260 || storedFileName.Length > 500 || url.Length > 1000 || (contentType?.Length ?? 0) > 120)
            throw new BusinessRuleValidationException("internal.chat.attachment.length", "Attachment metadata is too long.");

        return new InternalMessageAttachment(messageId, kind, fileName, storedFileName,
            url, contentType ?? string.Empty, sizeBytes, durationSeconds);
    }

    private static bool IsSafeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        // Root-relative upload URLs are valid and should be checked before Uri.TryCreate,
        // which can interpret a leading slash as an absolute file:// URI.
        if (value.StartsWith("/uploads/teamchatmedia/", StringComparison.OrdinalIgnoreCase))
            return !value.Contains("..", StringComparison.Ordinal) && !value.Contains((char)92);
        return Uri.TryCreate(value, UriKind.Absolute, out var absolute) &&
               (absolute.Scheme is "http" or "https") &&
               absolute.AbsolutePath.Contains("/teamchatmedia/", StringComparison.OrdinalIgnoreCase);
    }
}
