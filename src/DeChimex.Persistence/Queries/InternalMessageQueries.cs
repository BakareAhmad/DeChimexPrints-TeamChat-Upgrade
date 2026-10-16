using DeChimex.Application.Common.Interfaces;
using DeChimex.Contracts.InternalMessages;
using DeChimex.Domain.Entities;
using DeChimex.Persistence.Context;
using DeChimex.Shared;
using DeChimex.Shared.Constants;
using Microsoft.EntityFrameworkCore;

namespace DeChimex.Persistence.Queries;

/// <summary>
/// Read side for internal (work) messaging. Only accounts holding a messaging role
/// (business staff roles or Developer) are ever considered, so customers stay out of
/// the feature entirely; role-pair rules from InternalMessaging are applied here as
/// well as in the command handlers (defense in depth).
/// </summary>
public sealed class InternalMessageQueries(DeChimexDbContext db) : IInternalMessageQueries
{
    private static readonly string[] MessagingRoles =
    {
        RoleNames.Owner, RoleNames.Manager, RoleNames.SalesStaff,
        RoleNames.InventoryStaff, RoleNames.Designer, RoleNames.Developer
    };

    private sealed record IdentityRow(Guid Id, string FirstName, string LastName, List<string> RoleNames)
    {
        public string Name => $"{FirstName} {LastName}".Trim();
        public string RoleLabel => string.Join(", ", RoleNames
            .Where(r => MessagingRoles.Contains(r, StringComparer.OrdinalIgnoreCase))
            .Select(r => EnumDisplay.Words(r)));
    }

    /// <summary>Active accounts with their role names; the mapping to IdentityRow happens
    /// client-side (EF cannot materialize the private record in a server projection).</summary>
    private async Task<List<IdentityRow>> LoadIdentitiesAsync(
        CancellationToken ct, List<Guid>? onlyIds = null, Guid? excludeId = null)
    {
        var users = db.Users.AsNoTracking().Where(u => u.IsActive);
        if (onlyIds is not null) users = users.Where(u => onlyIds.Contains(u.Id));
        if (excludeId is not null) users = users.Where(u => u.Id != excludeId.Value);

        var rows = await users
            .Select(u => new { u.Id, u.FirstName, u.LastName, RoleNames = u.Roles.Select(ur => ur.Role!.Name).ToList() })
            .ToListAsync(ct);
        return rows.Select(r => new IdentityRow(r.Id, r.FirstName, r.LastName, r.RoleNames)).ToList();
    }

    public async Task<(Guid UserId, string Name, string Category)?> GetIdentityAsync(Guid userId, CancellationToken ct)
    {
        var row = (await LoadIdentitiesAsync(ct, onlyIds: new List<Guid> { userId })).FirstOrDefault();
        if (row is null) return null;
        var category = InternalMessaging.CategoryFor(row.RoleNames);
        return category is null ? null : (row.Id, row.Name, category);
    }

    public async Task<IReadOnlyList<TeamContactDto>> GetContactsAsync(Guid userId, string myCategory, CancellationToken ct)
    {
        var allowed = InternalMessaging.AllowedCounterparts(myCategory);
        if (allowed.Count == 0) return Array.Empty<TeamContactDto>();

        var rows = await LoadIdentitiesAsync(ct, excludeId: userId);
        return rows
            .Select(r => (Row: r, Category: InternalMessaging.CategoryFor(r.RoleNames)))
            .Where(x => x.Category is not null && allowed.Contains(x.Category))
            .OrderBy(x => x.Row.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => new TeamContactDto(x.Row.Id, x.Row.Name, x.Category!, x.Row.RoleLabel))
            .ToList();
    }

    public async Task<IReadOnlyList<ConversationSummaryDto>> GetConversationsAsync(Guid userId, CancellationToken ct)
    {
        var conversations = await db.InternalConversations.AsNoTracking()
            .Where(c => c.ParticipantOneId == userId || c.ParticipantTwoId == userId)
            .ToListAsync(ct);
        if (conversations.Count == 0) return Array.Empty<ConversationSummaryDto>();

        var ids = conversations.Select(c => c.Id).ToList();
        var messages = await db.InternalMessages.AsNoTracking()
            .Include(m => m.Attachments)
            .Where(m => ids.Contains(m.ConversationId))
            .OrderBy(m => m.SentAtUtc)
            .ThenBy(m => m.Id)
            .ToListAsync(ct);

        var otherIds = conversations.Select(c => c.OtherThan(userId)).Distinct().ToList();
        var identities = (await LoadIdentitiesAsync(ct, onlyIds: otherIds))
            .ToDictionary(r => r.Id);

        return conversations
            .Select(c =>
            {
                var otherId = c.OtherThan(userId);
                identities.TryGetValue(otherId, out var other);
                var mine = messages.Where(m => m.ConversationId == c.Id).ToList();
                var last = mine.LastOrDefault();
                return new ConversationSummaryDto(
                    c.Id,
                    otherId,
                    other?.Name ?? "Former user",
                    other is null ? "" : InternalMessaging.CategoryFor(other.RoleNames) ?? "",
                    other?.RoleLabel ?? "",
                    Preview(last),
                    last?.SenderId,
                    last?.SentAtUtc,
                    mine.Count(m => m.SenderId == otherId && m.ReadAtUtc == null));
            })
            .OrderByDescending(s => s.LastSentUtc ?? DateTime.MinValue)
            .ToList();
    }

    public async Task<ConversationDetailDto?> GetConversationAsync(Guid userId, Guid conversationId, CancellationToken ct)
    {
        var conversation = await db.InternalConversations.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == conversationId, ct);
        if (conversation is null || !conversation.Includes(userId)) return null;

        var otherId = conversation.OtherThan(userId);
        var messages = await db.InternalMessages.AsNoTracking()
            .Include(m => m.Attachments)
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.SentAtUtc)
            .ThenBy(m => m.Id)
            .ToListAsync(ct);

        var bothIds = new[] { userId, otherId };
        var identities = (await LoadIdentitiesAsync(ct, onlyIds: bothIds.ToList()))
            .ToDictionary(r => r.Id);
        if (!identities.TryGetValue(otherId, out var other)) return null;

        var otherCategory = InternalMessaging.CategoryFor(other.RoleNames);
        // A conversation whose pair is no longer permitted (e.g. roles changed) stops rendering.
        identities.TryGetValue(userId, out var me);
        if (me is null || otherCategory is null ||
            !InternalMessaging.CanCommunicate(InternalMessaging.CategoryFor(me.RoleNames), otherCategory))
            return null;

        string NameOf(Guid id) => identities.TryGetValue(id, out var row) ? row.Name : "Former user";

        return new ConversationDetailDto(
            conversation.Id,
            otherId,
            other.Name,
            otherCategory,
            other.RoleLabel,
            messages.Select(m => new InternalMessageDto(
                m.Id, m.ConversationId, m.SenderId, NameOf(m.SenderId), m.Body, m.SentAtUtc,
                m.SenderId == userId, m.ReadAtUtc, m.ClientMessageId,
                m.Attachments.Select(a => new InternalMessageAttachmentDto(
                    a.Id, a.Kind, a.OriginalFileName, a.Url, a.ContentType, a.SizeBytes, a.DurationSeconds)).ToList())).ToList());
    }

    private static string? Preview(InternalMessage? message)
    {
        if (message is null) return null;
        if (!string.IsNullOrWhiteSpace(message.Body)) return message.Body;
        var attachment = message.Attachments.FirstOrDefault();
        if (attachment is null) return null;
        var label = attachment.Kind switch
        {
            "image" => "Photo",
            "video" => "Video",
            "voice" => "Voice note",
            "audio" => "Audio",
            _ => "Attachment"
        };
        return message.Attachments.Count <= 1 ? label : $"{label} + {message.Attachments.Count - 1} more";
    }

    public Task<int> CountUnreadAsync(Guid userId, CancellationToken ct) =>
        (from c in db.InternalConversations.AsNoTracking()
         where c.ParticipantOneId == userId || c.ParticipantTwoId == userId
         join m in db.InternalMessages.AsNoTracking() on c.Id equals m.ConversationId
         where m.SenderId != userId && m.ReadAtUtc == null
         select m).CountAsync(ct);
}
