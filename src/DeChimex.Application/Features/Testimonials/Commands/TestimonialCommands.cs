using DeChimex.Application.Common;
using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Reviews;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Repositories;
using FluentValidation;
using MediatR;

namespace DeChimex.Application.Features.Testimonials.Commands;

/// <summary>
/// Staff CRUD for the Home page review slider (owner directive 2026-10-07). Mirrors the
/// Services command pattern: one file, Create/Update/Delete, FluentValidation per command,
/// aggregate invariants enforced in the domain, repository + unit of work for persistence.
/// </summary>

// ---- Create ----
public sealed record CreateTestimonialCommand(CreateTestimonialRequest Request) : ICommand<TestimonialDto>;

public sealed class CreateTestimonialValidator : AbstractValidator<CreateTestimonialCommand>
{
    public CreateTestimonialValidator()
    {
        RuleFor(x => x.Request.CustomerName).NotEmpty().MaximumLength(160);
        RuleFor(x => x.Request.CustomerTitle).MaximumLength(200);
        RuleFor(x => x.Request.Body).NotEmpty().MaximumLength(2000);
        RuleFor(x => x.Request.AvatarUrl).MaximumLength(500);
        RuleFor(x => x.Request.Rating).InclusiveBetween(1, 5);
    }
}

public sealed class CreateTestimonialHandler(ITestimonialRepository testimonials, IUnitOfWork uow, ICacheService cache)
    : ICommandHandler<CreateTestimonialCommand, TestimonialDto>
{
    public async Task<TestimonialDto> Handle(CreateTestimonialCommand command, CancellationToken ct)
    {
        var r = command.Request;

        // Default the slider position to the end of the current list so a new review
        // appears last instead of jumping to the front.
        var displayOrder = r.DisplayOrder;
        if (displayOrder == 0)
        {
            var existing = await testimonials.ListAsync(ct);
            displayOrder = existing.Count == 0 ? 1 : existing.Max(t => t.DisplayOrder) + 1;
        }

        var testimonial = Testimonial.Create(r.CustomerName, r.CustomerTitle, r.Body,
            r.AvatarUrl, r.Rating, r.IsPublished, displayOrder);
        await testimonials.AddAsync(testimonial, ct);
        await uow.SaveChangesAsync(ct);
        foreach (var key in CacheKeys.TestimonialLists) await cache.RemoveAsync(key, ct); // MED-004
        return Map(testimonial);
    }

    internal static TestimonialDto Map(Testimonial t) => new(
        t.Id, t.CustomerName, t.CustomerTitle, t.Body, t.AvatarUrl,
        t.Rating, t.IsPublished, t.DisplayOrder, t.CreatedDateUtc);
}

// ---- Update ----
public sealed record UpdateTestimonialCommand(Guid Id, UpdateTestimonialRequest Request) : ICommand<Unit>;

public sealed class UpdateTestimonialValidator : AbstractValidator<UpdateTestimonialCommand>
{
    public UpdateTestimonialValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Request.CustomerName).NotEmpty().MaximumLength(160);
        RuleFor(x => x.Request.CustomerTitle).MaximumLength(200);
        RuleFor(x => x.Request.Body).NotEmpty().MaximumLength(2000);
        RuleFor(x => x.Request.AvatarUrl).MaximumLength(500);
        RuleFor(x => x.Request.Rating).InclusiveBetween(1, 5);
    }
}

public sealed class UpdateTestimonialHandler(ITestimonialRepository testimonials, IUnitOfWork uow, ICacheService cache)
    : ICommandHandler<UpdateTestimonialCommand, Unit>
{
    public async Task<Unit> Handle(UpdateTestimonialCommand command, CancellationToken ct)
    {
        var testimonial = await testimonials.GetByIdAsync(command.Id, ct)
            ?? throw new NotFoundException("Testimonial", command.Id);
        var r = command.Request;

        testimonial.Update(r.CustomerName, r.CustomerTitle, r.Body, r.AvatarUrl, r.Rating, r.IsPublished, r.DisplayOrder);
        testimonials.Update(testimonial);
        await uow.SaveChangesAsync(ct);
        foreach (var key in CacheKeys.TestimonialLists) await cache.RemoveAsync(key, ct);
        return Unit.Value;
    }
}

// ---- Delete ----
public sealed record DeleteTestimonialCommand(Guid Id) : ICommand<Unit>;

public sealed class DeleteTestimonialValidator : AbstractValidator<DeleteTestimonialCommand>
{
    public DeleteTestimonialValidator() => RuleFor(x => x.Id).NotEmpty();
}

public sealed class DeleteTestimonialHandler(ITestimonialRepository testimonials, IUnitOfWork uow, ICacheService cache)
    : ICommandHandler<DeleteTestimonialCommand, Unit>
{
    public async Task<Unit> Handle(DeleteTestimonialCommand command, CancellationToken ct)
    {
        var testimonial = await testimonials.GetByIdAsync(command.Id, ct)
            ?? throw new NotFoundException("Testimonial", command.Id);

        testimonials.Remove(testimonial); // soft delete via the persistence interceptor
        await uow.SaveChangesAsync(ct);
        foreach (var key in CacheKeys.TestimonialLists) await cache.RemoveAsync(key, ct);
        return Unit.Value;
    }
}
