using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Reviews;

namespace DeChimex.Application.Features.Testimonials.Queries;

/// <summary>
/// Home page review slider reads. <paramref name="PublishedOnly"/> is true for the storefront
/// and false for the admin list, which must also show unpublished reviews.
/// </summary>
public sealed record GetTestimonialsQuery(bool PublishedOnly = false) : IQuery<IReadOnlyList<TestimonialDto>>;

public sealed class GetTestimonialsHandler(ITestimonialQueries queries)
    : IQueryHandler<GetTestimonialsQuery, IReadOnlyList<TestimonialDto>>
{
    public Task<IReadOnlyList<TestimonialDto>> Handle(GetTestimonialsQuery query, CancellationToken ct)
        => queries.GetAllAsync(query.PublishedOnly, ct);
}

public sealed record GetTestimonialByIdQuery(Guid Id) : IQuery<TestimonialDto?>;

public sealed class GetTestimonialByIdHandler(ITestimonialQueries queries)
    : IQueryHandler<GetTestimonialByIdQuery, TestimonialDto?>
{
    public Task<TestimonialDto?> Handle(GetTestimonialByIdQuery query, CancellationToken ct)
        => queries.GetByIdAsync(query.Id, ct);
}
