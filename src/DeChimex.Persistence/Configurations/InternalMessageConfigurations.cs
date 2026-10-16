using DeChimex.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DeChimex.Persistence.Configurations;

public sealed class InternalConversationConfiguration : IEntityTypeConfiguration<InternalConversation>
{
    public void Configure(EntityTypeBuilder<InternalConversation> b)
    {
        b.ToTable("InternalConversations");
        b.HasKey(x => x.Id);
        // One conversation per unordered pair (stored in creation order, queried both ways).
        b.HasIndex(x => new { x.ParticipantOneId, x.ParticipantTwoId }).IsUnique();
        b.HasIndex(x => x.ParticipantTwoId);
        b.HasMany(x => x.Messages)
            .WithOne()
            .HasForeignKey(x => x.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasQueryFilter(x => !x.IsDeleted);
    }
}

public sealed class InternalMessageConfiguration : IEntityTypeConfiguration<InternalMessage>
{
    public void Configure(EntityTypeBuilder<InternalMessage> b)
    {
        b.ToTable("InternalMessages");
        b.HasKey(x => x.Id);
        b.Property(x => x.Body).HasMaxLength(InternalMessage.MaxBodyLength).IsRequired();
        b.Property(x => x.ClientMessageId);
        b.HasIndex(x => x.ConversationId);
        b.HasIndex(x => x.SenderId);
        b.HasIndex(x => new { x.ConversationId, x.SenderId, x.ClientMessageId })
            .IsUnique()
            .HasFilter("[ClientMessageId] IS NOT NULL");
        b.HasMany(x => x.Attachments)
            .WithOne()
            .HasForeignKey(x => x.MessageId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasQueryFilter(x => !x.IsDeleted);
    }
}

public sealed class InternalMessageAttachmentConfiguration : IEntityTypeConfiguration<InternalMessageAttachment>
{
    public void Configure(EntityTypeBuilder<InternalMessageAttachment> b)
    {
        b.ToTable("InternalMessageAttachments");
        b.HasKey(x => x.Id);
        b.Property(x => x.Kind).HasMaxLength(10).IsRequired();
        b.Property(x => x.OriginalFileName).HasMaxLength(260).IsRequired();
        b.Property(x => x.StoredFileName).HasMaxLength(500).IsRequired();
        b.Property(x => x.Url).HasMaxLength(1000).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(120).IsRequired();
        b.HasIndex(x => x.MessageId);
    }
}
