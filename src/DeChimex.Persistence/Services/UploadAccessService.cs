using DeChimex.Application.Common.Interfaces;
using DeChimex.Persistence.Context;
using DeChimex.Shared.Constants;
using Microsoft.EntityFrameworkCore;

namespace DeChimex.Persistence.Services;

/// <summary>
/// Read-side authorization for confidential uploads. Order attachments, chat media,
/// design proofs and design-review files retain their existing order-owner / orders.view
/// behavior. Team Chat media is deliberately resolved and authorized first against the
/// current internal conversation pair; broad order staff permission never grants access.
/// Unknown private URLs are denied for customers and internal media.
/// </summary>
public sealed class UploadAccessService(
    DeChimexDbContext db,
    ICurrentUser currentUser,
    IInternalMessageQueries internalMessages) : IUploadAccessService
{
    public async Task<bool> CanViewAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url) || !currentUser.IsAuthenticated)
            return false;

        var normalized = url.Trim();
        if (IsTeamChatMediaUrl(normalized))
        {
            var record = await ResolveInternalChatAsync(normalized, ct);
            if (record is null || currentUser.UserId is not Guid userId) return false;
            var pair = record.Value;
            if (userId != pair.ParticipantOneId && userId != pair.ParticipantTwoId) return false;

            // Re-check both active identities and their current communication category:
            // a revoked role or inactive account closes attachment access immediately.
            var me = await internalMessages.GetIdentityAsync(userId, ct);
            var otherId = pair.ParticipantOneId == userId ? pair.ParticipantTwoId : pair.ParticipantOneId;
            var other = await internalMessages.GetIdentityAsync(otherId, ct);
            return me is not null && other is not null &&
                   InternalMessaging.CanCommunicate(me.Value.Category, other.Value.Category);
        }

        // Existing order-bound staff permissions remain unchanged.
        if (currentUser.HasPermission(Permissions.Orders.View))
            return true;

        var orderId = await ResolveOrderIdAsync(normalized, ct);
        if (orderId is null)
            return false;

        // The order's own customer.
        return await db.Orders.AsNoTracking()
            .Where(o => o.Id == orderId && o.Customer != null && o.Customer.UserId == currentUser.UserId)
            .AnyAsync(ct);
    }

    private static bool IsTeamChatMediaUrl(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var absolute) ? absolute.AbsolutePath : url;
        return path.Contains("/teamchatmedia/", StringComparison.OrdinalIgnoreCase) ||
               path.TrimStart('/').StartsWith("teamchatmedia/", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(Guid ParticipantOneId, Guid ParticipantTwoId)?> ResolveInternalChatAsync(
        string url, CancellationToken ct)
    {
        var row = await (from attachment in db.InternalMessageAttachments.AsNoTracking()
                         join message in db.InternalMessages.AsNoTracking() on attachment.MessageId equals message.Id
                         join conversation in db.InternalConversations.AsNoTracking() on message.ConversationId equals conversation.Id
                         where attachment.Url == url
                         select new { conversation.ParticipantOneId, conversation.ParticipantTwoId })
            .FirstOrDefaultAsync(ct);
        return row is null ? null : (row.ParticipantOneId, row.ParticipantTwoId);
    }

    /// <summary>Finds the order a private upload belongs to (any order-bound file table).</summary>
    private async Task<Guid?> ResolveOrderIdAsync(string url, CancellationToken ct)
    {
        var fromFile = await db.OrderFiles.AsNoTracking()
            .Where(f => f.Url == url).Select(f => (Guid?)f.OrderId).FirstOrDefaultAsync(ct);
        if (fromFile is not null) return fromFile;

        // Order chat media hangs off the message; the message carries the order id.
        var fromAttachment = await db.OrderMessageAttachments.AsNoTracking()
            .Join(db.OrderMessages.AsNoTracking(), a => a.MessageId, m => m.Id,
                (a, m) => new { a.Url, m.OrderId })
            .Where(x => x.Url == url)
            .Select(x => (Guid?)x.OrderId)
            .FirstOrDefaultAsync(ct);
        if (fromAttachment is not null) return fromAttachment;

        var fromProof = await db.OrderDesignProofs.AsNoTracking()
            .Where(p => p.Url == url).Select(p => (Guid?)p.OrderId).FirstOrDefaultAsync(ct);
        if (fromProof is not null) return fromProof;

        var fromReview = await db.OrderDesignReviews.AsNoTracking()
            .Where(r => r.Url == url || r.PreviewUrl == url).Select(r => (Guid?)r.OrderId).FirstOrDefaultAsync(ct);
        if (fromReview is not null) return fromReview;

        return null;
    }
}
