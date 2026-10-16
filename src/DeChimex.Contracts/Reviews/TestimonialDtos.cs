namespace DeChimex.Contracts.Reviews;

/// <summary>
/// Home page testimonial (the review slider). Kept in the Reviews contract folder next to the
/// product <see cref="ReviewDto"/> because both are customer feedback, but they are separate
/// aggregates: a product review belongs to a product and a signed-in customer, a testimonial is
/// staff-curated marketing copy for the storefront.
/// </summary>
public sealed record TestimonialDto(
    Guid Id, string CustomerName, string? CustomerTitle, string Body,
    string? AvatarUrl, int Rating, bool IsPublished, int DisplayOrder, DateTime CreatedDateUtc);

public sealed record CreateTestimonialRequest(
    string CustomerName, string? CustomerTitle, string Body,
    string? AvatarUrl, int Rating, bool IsPublished, int DisplayOrder);

public sealed record UpdateTestimonialRequest(
    string CustomerName, string? CustomerTitle, string Body,
    string? AvatarUrl, int Rating, bool IsPublished, int DisplayOrder);
