using DeChimex.Application.Common.Interfaces;
using DeChimex.Contracts.InternalMessages;
using Microsoft.AspNetCore.SignalR;

namespace DeChimex.Host.Realtime;

/// <summary>Read-state event sent only to the other conversation participant.</summary>
public sealed record InternalMessagesReadPushDto(Guid ConversationId, IReadOnlyList<Guid> MessageIds);

/// <summary>Pushes persisted internal messages and read-state changes through the
/// authenticated notifications hub's per-user group. Team events never join or publish
/// to the customer/order workspace hub.</summary>
public sealed class InternalMessagePusher(IHubContext<NotificationHub> hub) : IInternalMessagePusher
{
    public const string EventName = "internalMessage";
    public const string ReadEventName = "internalMessagesRead";

    public Task PushAsync(Guid recipientUserId, InternalMessageDto message, CancellationToken ct = default)
    {
        // IsMine is from the sender's page perspective; recipients always see a peer bubble.
        var payload = message with { IsMine = false };
        return hub.Clients.Group(NotificationHub.UserGroup(recipientUserId)).SendAsync(EventName, payload, ct);
    }

    public Task PushReadAsync(Guid recipientUserId, Guid conversationId, IReadOnlyList<Guid> messageIds,
        CancellationToken ct = default)
    {
        var payload = new InternalMessagesReadPushDto(conversationId, messageIds);
        return hub.Clients.Group(NotificationHub.UserGroup(recipientUserId)).SendAsync(ReadEventName, payload, ct);
    }
}
