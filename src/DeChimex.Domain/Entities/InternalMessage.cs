using DeChimex.Domain.Common;

namespace DeChimex.Domain.Entities;

/// <summary>
/// One message inside an <see cref="InternalConversation"/>. Unread state is per
/// recipient: the message starts unread (ReadAtUtc null) and the recipient clears it
/// by opening the conversation (MarkRead). The sender never marks their own copy.
/// </summary>
public sealed class InternalMessage : AggregateRoot
{
    public const int MaxBodyLength = 2000;
    public const int MaxAttachments = 6;

    private readonly List<InternalMessageAttachment> _attachments = [];

    private InternalMessage() { } // EF

    private InternalMessage(Guid conversationId, Guid senderId, string body, Guid? clientMessageId)
    {
        ConversationId = conversationId;
        SenderId = senderId;
        Body = body;
        ClientMessageId = clientMessageId;
        SentAtUtc = DateTime.UtcNow;
    }

    public Guid ConversationId { get; private set; }
    public Guid SenderId { get; private set; }
    public string Body { get; private set; } = default!;
    public DateTime SentAtUtc { get; private set; }

    /// <summary>Per-send idempotency key; null for legacy/no-JavaScript form posts.</summary>
    public Guid? ClientMessageId { get; private set; }

    /// <summary>Private media belonging only to this internal message.</summary>
    public IReadOnlyCollection<InternalMessageAttachment> Attachments => _attachments.AsReadOnly();

    /// <summary>When the recipient opened the conversation; null = unread.</summary>
    public DateTime? ReadAtUtc { get; private set; }
    public bool IsRead => ReadAtUtc is not null;

    public static InternalMessage Send(Guid conversationId, Guid senderId, string body,
        bool hasAttachments = false, Guid? clientMessageId = null)
    {
        if (conversationId == Guid.Empty || senderId == Guid.Empty)
            throw new InvalidOperationException("A conversation and sender are required.");
        if (clientMessageId == Guid.Empty)
            throw new InvalidOperationException("The client message id is invalid.");

        var trimmed = (body ?? string.Empty).Trim();
        if (trimmed.Length == 0 && !hasAttachments)
            throw new InvalidOperationException("The message is empty.");
        if (trimmed.Length > MaxBodyLength)
            throw new InvalidOperationException($"The message is longer than {MaxBodyLength} characters.");
        return new InternalMessage(conversationId, senderId, trimmed, clientMessageId);
    }

    public void AddAttachment(InternalMessageAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        if (attachment.MessageId != Id)
            throw new InvalidOperationException("The attachment belongs to another message.");
        if (_attachments.Count >= MaxAttachments)
            throw new InvalidOperationException($"A message carries at most {MaxAttachments} attachments.");
        _attachments.Add(attachment);
    }

    /// <summary>Idempotent recipient-side read mark.</summary>
    public void MarkRead()
    {
        ReadAtUtc ??= DateTime.UtcNow;
    }
}
