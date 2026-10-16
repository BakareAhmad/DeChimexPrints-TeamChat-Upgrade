using DeChimex.Domain.Entities;
using DeChimex.Domain.Exceptions;
using FluentAssertions;
using Xunit;

namespace DeChimex.UnitTests.Domain.Entities;

public sealed class InternalMessageMediaTests
{
    private static (string Kind, string Name, string Stored, string Url, string Type, long Size, int? Duration) Attachment(
        string kind = "image", string name = "photo.png", int? duration = null)
        => (kind, name, "teamchatmedia/01.png", "/uploads/teamchatmedia/01.png", "image/png", 12, duration);

    [Fact]
    public void Send_trims_text_and_accepts_media_only_messages_with_a_client_id()
    {
        var id = Guid.NewGuid();
        var text = InternalMessage.Send(Guid.NewGuid(), Guid.NewGuid(), "  hello  ", clientMessageId: id);
        var media = InternalMessage.Send(Guid.NewGuid(), Guid.NewGuid(), "  ", hasAttachments: true, clientMessageId: Guid.NewGuid());

        text.Body.Should().Be("hello");
        text.ClientMessageId.Should().Be(id);
        media.Body.Should().BeEmpty();
        media.ClientMessageId.Should().NotBeNull();
    }

    [Fact]
    public void Send_rejects_empty_text_without_media_and_rejects_oversized_bodies()
    {
        var empty = () => InternalMessage.Send(Guid.NewGuid(), Guid.NewGuid(), "  ");
        var longText = () => InternalMessage.Send(Guid.NewGuid(), Guid.NewGuid(), new string('x', InternalMessage.MaxBodyLength + 1));

        empty.Should().Throw<InvalidOperationException>().WithMessage("The message is empty.");
        longText.Should().Throw<InvalidOperationException>().WithMessage("*longer*");
    }

    [Fact]
    public void AddAttachment_keeps_six_file_cap_and_message_ownership()
    {
        var message = InternalMessage.Send(Guid.NewGuid(), Guid.NewGuid(), "", hasAttachments: true);
        for (var i = 0; i < InternalMessage.MaxAttachments; i++)
        {
            var attachment = InternalMessageAttachment.Create(message.Id, "image", $"{i}.png",
                $"teamchatmedia/{i}.png", $"/uploads/teamchatmedia/{i}.png", "image/png", 20, null);
            message.AddAttachment(attachment);
        }

        message.Attachments.Should().HaveCount(InternalMessage.MaxAttachments);
        var seventh = InternalMessageAttachment.Create(message.Id, "image", "7.png",
            "teamchatmedia/7.png", "/uploads/teamchatmedia/7.png", "image/png", 20, null);
        var addSeventh = () => message.AddAttachment(seventh);
        addSeventh.Should().Throw<InvalidOperationException>().WithMessage("*at most 6*");
    }

    [Fact]
    public void Attachment_factory_sanitizes_name_but_rejects_order_storage_and_active_urls()
    {
        var messageId = Guid.NewGuid();
        var good = InternalMessageAttachment.Create(messageId, "image", "C:\\fakepath\\photo.png",
            "teamchatmedia/01.png", "/uploads/teamchatmedia/01.png", "image/png", 12, null);
        good.OriginalFileName.Should().Be("photo.png");

        var wrongCategory = () => InternalMessageAttachment.Create(messageId, "image", "photo.png",
            "chatmedia/01.png", "/uploads/chatmedia/01.png", "image/png", 12, null);
        var unsafeUrl = () => InternalMessageAttachment.Create(messageId, "image", "photo.png",
            "teamchatmedia/01.png", "javascript:alert(1)", "image/png", 12, null);

        wrongCategory.Should().Throw<BusinessRuleValidationException>();
        unsafeUrl.Should().Throw<BusinessRuleValidationException>();
    }

    [Fact]
    public void Attachment_factory_restricts_kind_size_and_duration()
    {
        var messageId = Guid.NewGuid();
        var invalidKind = () => InternalMessageAttachment.Create(messageId, " hologram ", "photo.png",
            "teamchatmedia/01.png", "/uploads/teamchatmedia/01.png", "image/png", 12, null);
        var empty = () => InternalMessageAttachment.Create(messageId, "image", "photo.png",
            "teamchatmedia/01.png", "/uploads/teamchatmedia/01.png", "image/png", 0, null);
        var durationOnImage = () => InternalMessageAttachment.Create(messageId, "image", "photo.png",
            "teamchatmedia/01.png", "/uploads/teamchatmedia/01.png", "image/png", 12, 3);

        invalidKind.Should().Throw<BusinessRuleValidationException>();
        empty.Should().Throw<BusinessRuleValidationException>();
        durationOnImage.Should().Throw<BusinessRuleValidationException>();
    }
}
