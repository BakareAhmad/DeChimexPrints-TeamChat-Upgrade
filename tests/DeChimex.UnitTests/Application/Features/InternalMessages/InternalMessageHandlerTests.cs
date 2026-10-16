using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.InternalMessages;
using DeChimex.Contracts.InternalMessages;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Repositories;
using DeChimex.Shared.Constants;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace DeChimex.UnitTests.Application.Features.InternalMessages;

public sealed class InternalMessageHandlerTests
{
    private static InternalMessageAttachmentInput Photo(string name = "brief.png")
        => new("image", name, "teamchatmedia/01.png", "/uploads/teamchatmedia/01.png", "image/png", 128, null);

    private static void SetupIdentity(IInternalMessageQueries queries, Guid actorId, string actorCategory,
        Guid otherId, string otherCategory)
    {
        queries.GetIdentityAsync(actorId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(Guid UserId, string Name, string Category)?>(
                (actorId, "Actor User", actorCategory)));
        queries.GetIdentityAsync(otherId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(Guid UserId, string Name, string Category)?>(
                (otherId, "Other User", otherCategory)));
    }

    [Fact]
    public async Task Media_only_send_persists_private_attachments_and_pushes_the_full_message()
    {
        var actorId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var conversation = InternalConversation.Start(actorId, otherId);
        var clientId = Guid.NewGuid();
        var conversations = Substitute.For<IInternalConversationRepository>();
        conversations.GetByIdAsync(conversation.Id, Arg.Any<CancellationToken>()).Returns(conversation);
        var messages = Substitute.For<IInternalMessageRepository>();
        var queries = Substitute.For<IInternalMessageQueries>();
        SetupIdentity(queries, actorId, InternalMessaging.Owner, otherId, InternalMessaging.Manager);
        InternalMessage? persisted = null;
        messages.AddAsync(Arg.Do<InternalMessage>(m => persisted = m), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var notifications = Substitute.For<INotificationService>();
        var pusher = Substitute.For<IInternalMessagePusher>();
        var sut = new SendInternalMessageHandler(conversations, messages, queries,
            Substitute.For<IUnitOfWork>(), notifications, pusher);

        var id = await sut.Handle(new SendInternalMessageCommand(actorId, conversation.Id,
            "  ", [Photo()], clientId), CancellationToken.None);

        id.Should().NotBeEmpty();
        persisted.Should().NotBeNull();
        persisted!.Body.Should().BeEmpty();
        persisted.ClientMessageId.Should().Be(clientId);
        persisted.Attachments.Should().ContainSingle(a =>
            a.Kind == "image" && a.StoredFileName.StartsWith("teamchatmedia/", StringComparison.Ordinal));
        await pusher.Received(1).PushAsync(otherId,
            Arg.Is<InternalMessageDto>(m => m.Id == id && m.ConversationId == conversation.Id &&
                m.SenderId == actorId && !m.IsMine && m.Attachments.Count == 1 && m.Body == string.Empty),
            Arg.Any<CancellationToken>());
        await notifications.Received(1).NotifyAsync(NotificationType.NewMessage,
            Arg.Any<string>(), "Photo", otherId, $"/admin/team-messages/{conversation.Id}", null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Retry_with_the_same_client_id_returns_the_original_without_a_second_notification_or_push()
    {
        var actorId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var conversation = InternalConversation.Start(actorId, otherId);
        var clientId = Guid.NewGuid();
        var original = InternalMessage.Send(conversation.Id, actorId, "Saved", clientMessageId: clientId);
        var conversations = Substitute.For<IInternalConversationRepository>();
        conversations.GetByIdAsync(conversation.Id, Arg.Any<CancellationToken>()).Returns(conversation);
        var messages = Substitute.For<IInternalMessageRepository>();
        messages.FindByClientMessageIdAsync(conversation.Id, actorId, clientId, Arg.Any<CancellationToken>())
            .Returns(original);
        var queries = Substitute.For<IInternalMessageQueries>();
        SetupIdentity(queries, actorId, InternalMessaging.Owner, otherId, InternalMessaging.Manager);
        var uow = Substitute.For<IUnitOfWork>();
        var notifications = Substitute.For<INotificationService>();
        var pusher = Substitute.For<IInternalMessagePusher>();
        var sut = new SendInternalMessageHandler(conversations, messages, queries, uow, notifications, pusher);

        var returned = await sut.Handle(new SendInternalMessageCommand(actorId, conversation.Id,
            "Saved", [Photo()], clientId), CancellationToken.None);

        returned.Should().Be(original.Id);
        await messages.DidNotReceive().AddAsync(Arg.Any<InternalMessage>(), Arg.Any<CancellationToken>());
        await uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await notifications.DidNotReceive().NotifyAsync(Arg.Any<NotificationType>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await pusher.DidNotReceive().PushAsync(Arg.Any<Guid>(), Arg.Any<InternalMessageDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Send_rechecks_the_pair_and_hides_a_disallowed_conversation()
    {
        var staffId = Guid.NewGuid();
        var developerId = Guid.NewGuid();
        var conversation = InternalConversation.Start(staffId, developerId);
        var conversations = Substitute.For<IInternalConversationRepository>();
        conversations.GetByIdAsync(conversation.Id, Arg.Any<CancellationToken>()).Returns(conversation);
        var queries = Substitute.For<IInternalMessageQueries>();
        SetupIdentity(queries, staffId, InternalMessaging.Staff, developerId, InternalMessaging.Developer);
        var messages = Substitute.For<IInternalMessageRepository>();
        var notifications = Substitute.For<INotificationService>();
        var pusher = Substitute.For<IInternalMessagePusher>();
        var sut = new SendInternalMessageHandler(conversations, messages, queries,
            Substitute.For<IUnitOfWork>(), notifications, pusher);

        var act = () => sut.Handle(new SendInternalMessageCommand(staffId, conversation.Id, "No"), CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
        await messages.DidNotReceive().AddAsync(Arg.Any<InternalMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Mark_read_rechecks_the_pair_and_pushes_receipt_to_the_sender()
    {
        var senderId = Guid.NewGuid();
        var readerId = Guid.NewGuid();
        var conversation = InternalConversation.Start(senderId, readerId);
        var incoming = InternalMessage.Send(conversation.Id, senderId, "Read this");
        var conversations = Substitute.For<IInternalConversationRepository>();
        conversations.GetByIdAsync(conversation.Id, Arg.Any<CancellationToken>()).Returns(conversation);
        var messages = Substitute.For<IInternalMessageRepository>();
        messages.GetUnreadFromAsync(conversation.Id, senderId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<InternalMessage>)[incoming]);
        var queries = Substitute.For<IInternalMessageQueries>();
        SetupIdentity(queries, readerId, InternalMessaging.Manager, senderId, InternalMessaging.Owner);
        var pusher = Substitute.For<IInternalMessagePusher>();
        var sut = new MarkInternalConversationReadHandler(conversations, messages, queries,
            Substitute.For<IUnitOfWork>(), pusher);

        await sut.Handle(new MarkInternalConversationReadCommand(readerId, conversation.Id), CancellationToken.None);

        incoming.ReadAtUtc.Should().NotBeNull();
        await pusher.Received(1).PushReadAsync(senderId, conversation.Id,
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.Count == 1 && ids[0] == incoming.Id), Arg.Any<CancellationToken>());
    }
}
