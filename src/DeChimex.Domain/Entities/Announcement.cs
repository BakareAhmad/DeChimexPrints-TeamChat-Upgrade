using DeChimex.Domain.Common;
using DeChimex.Domain.Exceptions;

namespace DeChimex.Domain.Entities;

/// <summary>
/// A short service-wide announcement shown as a compact scrolling pill/ticker across the
/// customer-facing app (owner directive 2026-10-07), similar to banking-app service banners.
///
/// Deliberately NOT part of the in-app <see cref="Notification"/> system: notifications are
/// per-user records with read state and delivery, while an announcement is one piece of
/// broadcast content staff curate for every visitor. It follows the same
/// aggregate/repository/command conventions as the rest of the catalogue.
/// </summary>
public sealed class Announcement : AggregateRoot
{
    private Announcement() { } // EF

    private Announcement(string message, DateTime? startAtUtc, DateTime? endAtUtc, bool isActive)
    {
        Message = message;
        StartAtUtc = startAtUtc;
        EndAtUtc = endAtUtc;
        IsActive = isActive;
    }

    /// <summary>The short ticker text, e.g. "Holiday closure: we reopen Mon 8am".</summary>
    public string Message { get; private set; } = default!;

    /// <summary>Optional visibility window start (UTC). Null = visible immediately.</summary>
    public DateTime? StartAtUtc { get; private set; }

    /// <summary>Optional visibility window end (UTC). Null = never expires on its own.</summary>
    public DateTime? EndAtUtc { get; private set; }

    /// <summary>Manual active switch; only active announcements are ever shown.</summary>
    public bool IsActive { get; private set; }

    public const int MaxMessageLength = 200;

    /// <summary>Customers only ever see active announcements inside their visibility window.</summary>
    public bool IsVisibleAt(DateTime utcNow) =>
        IsActive
        && (StartAtUtc is null || StartAtUtc.Value <= utcNow)
        && (EndAtUtc is null || EndAtUtc.Value >= utcNow);

    public static Announcement Create(string message, DateTime? startAtUtc = null,
        DateTime? endAtUtc = null, bool isActive = true)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new BusinessRuleValidationException("announcement.message", "Announcement message is required.");
        if (message.Trim().Length > MaxMessageLength)
            throw new BusinessRuleValidationException("announcement.message", $"Announcement message must be at most {MaxMessageLength} characters.");
        if (startAtUtc is not null && endAtUtc is not null && endAtUtc.Value < startAtUtc.Value)
            throw new BusinessRuleValidationException("announcement.window", "End date must be after the start date.");

        return new Announcement(message.Trim(), startAtUtc, endAtUtc, isActive);
    }

    public void Update(string message, DateTime? startAtUtc, DateTime? endAtUtc)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new BusinessRuleValidationException("announcement.message", "Announcement message is required.");
        if (message.Trim().Length > MaxMessageLength)
            throw new BusinessRuleValidationException("announcement.message", $"Announcement message must be at most {MaxMessageLength} characters.");
        if (startAtUtc is not null && endAtUtc is not null && endAtUtc.Value < startAtUtc.Value)
            throw new BusinessRuleValidationException("announcement.window", "End date must be after the start date.");

        Message = message.Trim();
        StartAtUtc = startAtUtc;
        EndAtUtc = endAtUtc;
    }

    public void Activate() => IsActive = true;
    public void Deactivate() => IsActive = false;
}
