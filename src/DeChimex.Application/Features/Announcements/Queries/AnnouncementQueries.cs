using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Announcements;

namespace DeChimex.Application.Features.Announcements.Queries;

/// <summary>
/// Announcement reads. <paramref name="VisibleOnly"/> is true for the customer shell
/// (active + inside the visibility window) and false for the admin list, which must
/// also show inactive and scheduled announcements so staff can manage them.
/// </summary>
public sealed record GetAnnouncementsQuery(bool VisibleOnly = false) : IQuery<IReadOnlyList<AnnouncementDto>>;

public sealed class GetAnnouncementsHandler(IAnnouncementQueries queries)
    : IQueryHandler<GetAnnouncementsQuery, IReadOnlyList<AnnouncementDto>>
{
    public Task<IReadOnlyList<AnnouncementDto>> Handle(GetAnnouncementsQuery query, CancellationToken ct)
        => queries.GetAllAsync(query.VisibleOnly, ct);
}

public sealed record GetAnnouncementByIdQuery(Guid Id) : IQuery<AnnouncementDto?>;

public sealed class GetAnnouncementByIdHandler(IAnnouncementQueries queries)
    : IQueryHandler<GetAnnouncementByIdQuery, AnnouncementDto?>
{
    public Task<AnnouncementDto?> Handle(GetAnnouncementByIdQuery query, CancellationToken ct)
        => queries.GetByIdAsync(query.Id, ct);
}
