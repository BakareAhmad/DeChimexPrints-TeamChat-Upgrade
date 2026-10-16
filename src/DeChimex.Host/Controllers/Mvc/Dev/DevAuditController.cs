using DeChimex.Host.Controllers.Mvc;
using DeChimex.Shared.Constants;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeChimex.Host.Controllers.Mvc.Dev;

/// <summary>
/// Forensic raw audit trail (dev.audit.view). Moved out of the business admin portal by the
/// 2026-10-08 portal cleanup: before/after entity diffs are a technical/audit surface, and the
/// business-facing "Business activity" feed remains the owner-facing event view. The capture
/// itself (AuditableSoftDeleteInterceptor -> AuditLogs) is untouched; this is only a reader.
/// </summary>
[DevPermission(Permissions.Developer.AuditView)]
public sealed class DevAuditController(Persistence.Context.DeChimexDbContext db) : DevControllerBase
{
    [HttpGet("/dev/audit")]
    public Task<IActionResult> Index(
        string? entity = null, string? entityId = null, string? actor = null, string? action = null,
        DateTime? from = null, DateTime? to = null, int page = 1, CancellationToken ct = default) =>
        UnlessClientAborted(async () =>
        {
            ViewData["Title"] = "Audit trail";
            ViewData["DevNav"] = "audit";

            page = page < 1 ? 1 : page;

            var q = db.AuditLogs.AsNoTracking().AsQueryable();

            // Only the generic capture rows — the business-activity projection has its own page.
            q = q.Where(a => a.ActivityType == null);

            if (!string.IsNullOrWhiteSpace(entity))
                q = q.Where(a => a.EntityName == entity.Trim());
            if (!string.IsNullOrWhiteSpace(entityId))
                q = q.Where(a => a.EntityId == entityId.Trim());
            if (!string.IsNullOrWhiteSpace(action))
                q = q.Where(a => EF.Functions.Like(a.Action, $"%{action.Trim()}%"));
            if (!string.IsNullOrWhiteSpace(actor))
                q = q.Where(a => EF.Functions.Like(a.PerformedBy!, $"%{actor.Trim()}%"));
            if (from.HasValue)
                q = q.Where(a => a.TimestampUtc >= from.Value.Date);
            if (to.HasValue)
            {
                var end = to.Value.Date.AddDays(1);
                q = q.Where(a => a.TimestampUtc < end);
            }

            var pageSize = 25;
            var total = await q.CountAsync(ct);
            var items = await q
                .OrderByDescending(a => a.TimestampUtc).ThenByDescending(a => a.Id)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(a => new RawAuditRow(a.Id, a.TimestampUtc, a.Action, a.EntityName, a.EntityId,
                    a.PerformedBy, a.OldValues, a.NewValues, a.IpAddress, a.CorrelationId))
                .ToListAsync(ct);

            ViewBag.Page = page;
            ViewBag.PageSize = pageSize;
            ViewBag.TotalRecords = total;
            ViewBag.TotalPages = (int)Math.Ceiling(total / (double)pageSize);
            ViewBag.Entity = entity;
            ViewBag.EntityId = entityId;
            ViewBag.Actor = actor;
            ViewBag.Action = action;
            ViewBag.From = from;
            ViewBag.To = to;
            return DevView("Audit", items);
        });

    public sealed record RawAuditRow(
        Guid Id, DateTime TimestampUtc, string Action, string EntityName, string? EntityId,
        string? PerformedBy, string? OldValues, string? NewValues, string? IpAddress, string? CorrelationId);
}
