namespace DeChimex.Contracts.Announcements;

/// <summary>Read model for the service-wide announcement ticker.</summary>
public sealed record AnnouncementDto(
    Guid Id, string Message, DateTime? StartAtUtc, DateTime? EndAtUtc,
    bool IsActive, DateTime CreatedDateUtc);

public sealed record CreateAnnouncementRequest(
    string Message, DateTime? StartAtUtc, DateTime? EndAtUtc, bool IsActive);

public sealed record UpdateAnnouncementRequest(
    Guid Id, string Message, DateTime? StartAtUtc, DateTime? EndAtUtc);
