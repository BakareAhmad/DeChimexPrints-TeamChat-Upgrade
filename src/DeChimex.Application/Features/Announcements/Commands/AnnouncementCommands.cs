using DeChimex.Application.Common;
using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Announcements;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Repositories;
using FluentValidation;
using MediatR;

namespace DeChimex.Application.Features.Announcements.Commands;

/// <summary>
/// Staff CRUD + activate/deactivate for the customer-facing announcement ticker
/// (owner directive 2026-10-07). Mirrors the Testimonials command pattern: one file,
/// FluentValidation per command, aggregate invariants in the domain, repository +
/// unit of work for persistence, cache invalidation so the storefront updates now.
/// </summary>

// ---- Create ----
public sealed record CreateAnnouncementCommand(CreateAnnouncementRequest Request) : ICommand<AnnouncementDto>;

public sealed class CreateAnnouncementValidator : AbstractValidator<CreateAnnouncementCommand>
{
    public CreateAnnouncementValidator()
    {
        RuleFor(x => x.Request.Message).NotEmpty().MaximumLength(Announcement.MaxMessageLength);
        RuleFor(x => x.Request)
            .Must(r => r.EndAtUtc is null || r.StartAtUtc is null || r.EndAtUtc >= r.StartAtUtc)
            .WithMessage("End date must be after the start date.");
    }
}

public sealed class CreateAnnouncementHandler(IAnnouncementRepository announcements, IUnitOfWork uow, ICacheService cache)
    : ICommandHandler<CreateAnnouncementCommand, AnnouncementDto>
{
    public async Task<AnnouncementDto> Handle(CreateAnnouncementCommand command, CancellationToken ct)
    {
        var r = command.Request;
        var a = Announcement.Create(r.Message, r.StartAtUtc, r.EndAtUtc, r.IsActive);
        await announcements.AddAsync(a, ct);
        await uow.SaveChangesAsync(ct);

        foreach (var key in CacheKeys.AnnouncementLists) await cache.RemoveAsync(key, ct); // MED-004

        return Map(a);
    }

    internal static AnnouncementDto Map(Announcement a) =>
        new(a.Id, a.Message, a.StartAtUtc, a.EndAtUtc, a.IsActive, a.CreatedDateUtc);
}

// ---- Update ----
public sealed record UpdateAnnouncementCommand(UpdateAnnouncementRequest Request) : ICommand<AnnouncementDto>;

public sealed class UpdateAnnouncementValidator : AbstractValidator<UpdateAnnouncementCommand>
{
    public UpdateAnnouncementValidator()
    {
        RuleFor(x => x.Request.Id).NotEmpty();
        RuleFor(x => x.Request.Message).NotEmpty().MaximumLength(Announcement.MaxMessageLength);
        RuleFor(x => x.Request)
            .Must(r => r.EndAtUtc is null || r.StartAtUtc is null || r.EndAtUtc >= r.StartAtUtc)
            .WithMessage("End date must be after the start date.");
    }
}

public sealed class UpdateAnnouncementHandler(IAnnouncementRepository announcements, IUnitOfWork uow, ICacheService cache)
    : ICommandHandler<UpdateAnnouncementCommand, AnnouncementDto>
{
    public async Task<AnnouncementDto> Handle(UpdateAnnouncementCommand command, CancellationToken ct)
    {
        var r = command.Request;
        var a = await announcements.GetByIdAsync(r.Id, ct)
            ?? throw new NotFoundException("Announcement", r.Id);

        a.Update(r.Message, r.StartAtUtc, r.EndAtUtc);
        await uow.SaveChangesAsync(ct);

        foreach (var key in CacheKeys.AnnouncementLists) await cache.RemoveAsync(key, ct); // MED-004

        return CreateAnnouncementHandler.Map(a);
    }
}

// ---- Activate / deactivate ----
public sealed record SetAnnouncementActiveCommand(Guid Id, bool Active) : ICommand<AnnouncementDto>;

public sealed class SetAnnouncementActiveHandler(IAnnouncementRepository announcements, IUnitOfWork uow, ICacheService cache)
    : ICommandHandler<SetAnnouncementActiveCommand, AnnouncementDto>
{
    public async Task<AnnouncementDto> Handle(SetAnnouncementActiveCommand command, CancellationToken ct)
    {
        var a = await announcements.GetByIdAsync(command.Id, ct)
            ?? throw new NotFoundException("Announcement", command.Id);

        if (command.Active) a.Activate(); else a.Deactivate();
        await uow.SaveChangesAsync(ct);

        foreach (var key in CacheKeys.AnnouncementLists) await cache.RemoveAsync(key, ct); // MED-004

        return CreateAnnouncementHandler.Map(a);
    }
}

// ---- Delete (soft) ----
public sealed record DeleteAnnouncementCommand(Guid Id) : ICommand<Unit>;

public sealed class DeleteAnnouncementValidator : AbstractValidator<DeleteAnnouncementCommand>
{
    public DeleteAnnouncementValidator() => RuleFor(x => x.Id).NotEmpty();
}

public sealed class DeleteAnnouncementHandler(IAnnouncementRepository announcements, IUnitOfWork uow, ICacheService cache)
    : ICommandHandler<DeleteAnnouncementCommand, Unit>
{
    public async Task<Unit> Handle(DeleteAnnouncementCommand command, CancellationToken ct)
    {
        var a = await announcements.GetByIdAsync(command.Id, ct)
            ?? throw new NotFoundException("Announcement", command.Id);

        announcements.Remove(a); // soft delete via the persistence interceptor
        await uow.SaveChangesAsync(ct);

        foreach (var key in CacheKeys.AnnouncementLists) await cache.RemoveAsync(key, ct); // MED-004

        return Unit.Value;
    }
}
