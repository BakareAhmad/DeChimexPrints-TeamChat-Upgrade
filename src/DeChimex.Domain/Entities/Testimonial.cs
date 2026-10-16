using DeChimex.Domain.Common;
using DeChimex.Domain.Exceptions;

namespace DeChimex.Domain.Entities;

/// <summary>
/// A customer testimonial shown in the Home page review slider (owner directive 2026-10-07).
///
/// Deliberately NOT the product <see cref="Review"/> aggregate: that one is scoped to a
/// product and a signed-in customer and carries an approve/reject moderation workflow, while a
/// home-page testimonial has no product, no customer account and is curated by staff. It follows
/// the same aggregate/repository/command conventions as the rest of the catalogue.
/// </summary>
public sealed class Testimonial : AggregateRoot
{
    private Testimonial() { } // EF

    private Testimonial(string customerName, string? customerTitle, string body,
        string? avatarUrl, int rating, bool isPublished, int displayOrder)
    {
        CustomerName = customerName;
        CustomerTitle = customerTitle;
        Body = body;
        AvatarUrl = avatarUrl;
        Rating = rating;
        IsPublished = isPublished;
        DisplayOrder = displayOrder;
    }

    public string CustomerName { get; private set; } = default!;

    /// <summary>Role/company line rendered under the name, e.g. "Founder, EAYoruba".</summary>
    public string? CustomerTitle { get; private set; }

    public string Body { get; private set; } = default!;

    /// <summary>Optional avatar/logo. The slider falls back to the customer's initial.</summary>
    public string? AvatarUrl { get; private set; }

    /// <summary>1-5 stars. The previous hard-coded markup always showed five.</summary>
    public int Rating { get; private set; }

    /// <summary>Only published testimonials reach the Home page.</summary>
    public bool IsPublished { get; private set; }

    /// <summary>Stable slider order (ascending), so the carousel does not reshuffle.</summary>
    public int DisplayOrder { get; private set; }

    public const int DefaultRating = 5;

    public static Testimonial Create(string customerName, string? customerTitle, string body,
        string? avatarUrl = null, int rating = DefaultRating, bool isPublished = true, int displayOrder = 0)
    {
        if (string.IsNullOrWhiteSpace(customerName))
            throw new BusinessRuleValidationException("testimonial.customerName", "Customer name is required.");
        if (string.IsNullOrWhiteSpace(body))
            throw new BusinessRuleValidationException("testimonial.body", "Review text is required.");
        if (rating is < 1 or > 5)
            throw new BusinessRuleValidationException("testimonial.rating", "Rating must be between 1 and 5.");

        return new Testimonial(customerName.Trim(), customerTitle?.Trim(), body.Trim(),
            string.IsNullOrWhiteSpace(avatarUrl) ? null : avatarUrl.Trim(), rating, isPublished, displayOrder);
    }

    public void Update(string customerName, string? customerTitle, string body,
        string? avatarUrl, int rating, bool isPublished, int displayOrder)
    {
        if (string.IsNullOrWhiteSpace(customerName))
            throw new BusinessRuleValidationException("testimonial.customerName", "Customer name is required.");
        if (string.IsNullOrWhiteSpace(body))
            throw new BusinessRuleValidationException("testimonial.body", "Review text is required.");
        if (rating is < 1 or > 5)
            throw new BusinessRuleValidationException("testimonial.rating", "Rating must be between 1 and 5.");

        CustomerName = customerName.Trim();
        CustomerTitle = string.IsNullOrWhiteSpace(customerTitle) ? null : customerTitle.Trim();
        Body = body.Trim();
        AvatarUrl = string.IsNullOrWhiteSpace(avatarUrl) ? null : avatarUrl.Trim();
        Rating = rating;
        IsPublished = isPublished;
        DisplayOrder = displayOrder;
    }

    public void Publish() => IsPublished = true;
    public void Unpublish() => IsPublished = false;
}
