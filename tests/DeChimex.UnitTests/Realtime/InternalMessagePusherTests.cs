using DeChimex.Contracts.InternalMessages;
using DeChimex.Host.Realtime;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;
using Xunit;

namespace DeChimex.UnitTests.Realtime;

public sealed class InternalMessagePusherTests
{
    [Fact]
    public async Task Push_targets_only_the_recipient_user_group_and_marks_message_as_peer_authored()
    {
        var recipientId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var proxy = Substitute.For<IClientProxy>();
        var clients = Substitute.For<IHubClients>();
        var hub = Substitute.For<IHubContext<NotificationHub>>();
        clients.Group(NotificationHub.UserGroup(recipientId)).Returns(proxy);
        hub.Clients.Returns(clients);
        var pusher = new InternalMessagePusher(hub);
        var message = new InternalMessageDto(messageId, conversationId, Guid.NewGuid(), "Sender",
            "Hello", DateTime.UtcNow, true, null, Guid.NewGuid(), Array.Empty<InternalMessageAttachmentDto>());

        await pusher.PushAsync(recipientId, message);

        message.IsMine.Should().BeTrue("the sender's projection is not mutated");
        await proxy.Received(1).SendCoreAsync(InternalMessagePusher.EventName,
            Arg.Is<object?[]>(args => args.Length == 1 &&
                ((InternalMessageDto)args[0]!).Id == messageId &&
                ((InternalMessageDto)args[0]!).ConversationId == conversationId &&
                !((InternalMessageDto)args[0]!).IsMine),
            Arg.Any<CancellationToken>());
        clients.Received(1).Group(NotificationHub.UserGroup(recipientId));
        clients.DidNotReceive().Group(Arg.Is<string>(group => group != NotificationHub.UserGroup(recipientId)));
    }

    [Fact]
    public async Task Push_read_sends_receipt_only_to_the_other_participants_user_group()
    {
        var recipientId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var messageIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var proxy = Substitute.For<IClientProxy>();
        var clients = Substitute.For<IHubClients>();
        var hub = Substitute.For<IHubContext<NotificationHub>>();
        clients.Group(NotificationHub.UserGroup(recipientId)).Returns(proxy);
        hub.Clients.Returns(clients);
        var pusher = new InternalMessagePusher(hub);

        await pusher.PushReadAsync(recipientId, conversationId, messageIds);

        await proxy.Received(1).SendCoreAsync(InternalMessagePusher.ReadEventName,
            Arg.Is<object?[]>(args => args.Length == 1 &&
                ((InternalMessagesReadPushDto)args[0]!).ConversationId == conversationId &&
                ((InternalMessagesReadPushDto)args[0]!).MessageIds.SequenceEqual(messageIds)),
            Arg.Any<CancellationToken>());
        clients.Received(1).Group(NotificationHub.UserGroup(recipientId));
    }
}
