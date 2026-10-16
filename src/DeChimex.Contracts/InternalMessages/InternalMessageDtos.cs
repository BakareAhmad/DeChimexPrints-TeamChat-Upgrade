namespace DeChimex.Contracts.InternalMessages;

/// <summary>A user the caller is allowed to start an internal conversation with
/// (role-pair rules already applied — disallowed users are never listed).</summary>
public sealed record TeamContactDto(Guid UserId, string Name, string Category, string RoleLabel);

/// <summary>One row of the conversation inbox.</summary>
public sealed record ConversationSummaryDto(
    Guid ConversationId,
    Guid OtherUserId,
    string OtherName,
    string OtherCategory,
    string OtherRoleLabel,
    string? LastMessageBody,
    Guid? LastSenderId,
    DateTime? LastSentUtc,
    int UnreadCount);

/// <summary>Storage metadata accepted by the internal-message send handler. The storage
/// key is server-only metadata and is never copied into a browser response.</summary>
public sealed record InternalMessageAttachmentInput(
    string Kind,
    string OriginalFileName,
    string StoredFileName,
    string Url,
    string ContentType,
    long SizeBytes,
    int? DurationSeconds);

/// <summary>Safe attachment projection for a participant in the internal conversation.</summary>
public sealed record InternalMessageAttachmentDto(
    Guid Id,
    string Kind,
    string OriginalFileName,
    string Url,
    string ContentType,
    long SizeBytes,
    int? DurationSeconds);

/// <summary>A single persisted message, rendered from the caller's side.</summary>
public sealed record InternalMessageDto(
    Guid Id,
    Guid ConversationId,
    Guid SenderId,
    string SenderName,
    string Body,
    DateTime SentAtUtc,
    bool IsMine,
    DateTime? ReadAtUtc,
    Guid? ClientMessageId,
    IReadOnlyList<InternalMessageAttachmentDto> Attachments);

/// <summary>The full conversation view for one participant.</summary>
public sealed record ConversationDetailDto(
    Guid ConversationId,
    Guid OtherUserId,
    string OtherName,
    string OtherCategory,
    string OtherRoleLabel,
    IReadOnlyList<InternalMessageDto> Messages);
