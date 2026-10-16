using System.ComponentModel.DataAnnotations;

namespace DeChimex.Host.Models.Admin;

public sealed class CustomerFormViewModel
{
    public Guid? CustomerId { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Notes { get; set; }
}

/// <summary>Line in the quote invoice form: exactly one of ProductId/ServiceId.</summary>
public sealed class QuoteLineViewModel
{
    public string? ProductId { get; set; }
    public string? ServiceId { get; set; }
    public int Quantity { get; set; } = 1;
    public string? Notes { get; set; }
}

/// <summary>Staff quote invoice form: pick customer, add product/service lines, discount/tax, notes.</summary>
public sealed class QuoteInvoiceFormViewModel
{
    /// <summary>"existing" or "new".</summary>
    public string CustomerMode { get; set; } = "existing";
    public Guid? CustomerId { get; set; }
    public string? NewFirstName { get; set; }
    public string? NewLastName { get; set; }
    public string? NewEmail { get; set; }
    public string? NewPhone { get; set; }
    public decimal Discount { get; set; }
    public decimal Tax { get; set; }
    public string? Notes { get; set; }
    public List<QuoteLineViewModel> Lines { get; set; } = new();
}

public sealed class OrderBuilderViewModel
{
    /// <summary>Quote invoice this order is placed from (null for regular POS orders).</summary>
    public Guid? InvoiceId { get; set; }

    /// <summary>"existing" or "new".</summary>
    public string CustomerMode { get; set; } = "existing";

    public Guid? CustomerId { get; set; }

    public string? NewFirstName { get; set; }
    public string? NewLastName { get; set; }
    public string? NewEmail { get; set; }
    public string? NewPhone { get; set; }

    /// <summary>"delivery" or "pickup".</summary>
    public string Fulfilment { get; set; } = "delivery";

    /// <summary>
    /// Optional payment taken at the counter while creating the order:
    /// "Cash", "BankTransfer" or "Paystack" (reference). Null/blank leaves
    /// the order unpaid (Draft) for later "Mark as paid".
    /// </summary>
    public string? PaymentMethod { get; set; }

    [StringLength(500, ErrorMessage = "Payment note is too long.")]
    public string? PaymentNote { get; set; }

    public string? Street { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Country { get; set; } = "Nigeria";
    public string? PostalCode { get; set; }

    [Range(0, double.MaxValue, ErrorMessage = "Discount cannot be negative.")]
    public decimal Discount { get; set; }

    [Range(0, double.MaxValue, ErrorMessage = "Tax cannot be negative.")]
    public decimal Tax { get; set; }

    [StringLength(1000)]
    public string? Notes { get; set; }

    [Range(0, double.MaxValue, ErrorMessage = "Delivery fee cannot be negative.")]
    public decimal DeliveryFee { get; set; }

    public List<OrderLineForm> Products { get; set; } = new() { new() };
    public List<OrderServiceForm> Services { get; set; } = new() { new() };
}

public sealed class OrderLineForm
{
    public Guid? ProductId { get; set; }
    public int Quantity { get; set; } = 1;
}

public sealed class OrderServiceForm
{
    public Guid? ServiceId { get; set; }
    public int Quantity { get; set; } = 1;
    [StringLength(500)]
    public string? Notes { get; set; }
}

public sealed class MarkPaidFormViewModel
{
    public const string Cash = "Cash";
    public const string BankTransfer = "BankTransfer";
    public const string Paystack = "Paystack";

    [Required]
    public string Method { get; set; } = Cash;
    public string? Note { get; set; }
}

public sealed class OrderCancelFormViewModel
{
    [StringLength(500, ErrorMessage = "Reason is too long.")]
    public string? Reason { get; set; }

    /// <summary>AUD-018: what happens to a HELD payment when the order is cancelled.
    /// RefundLater (default) | StoreCredit | NotRequired.</summary>
    public string? RefundOutcome { get; set; }
}

public sealed class AdminCustomerAddressFormViewModel
{
    public string Label { get; set; } = "";
    public string Street { get; set; } = "";
    public string City { get; set; } = "";
    public string State { get; set; } = "";
    public string Country { get; set; } = "Nigeria";
    public string? PostalCode { get; set; }
    public bool IsDefault { get; set; }
}

/// <summary>Form models for admin MVC pages. Separate from the Contracts DTOs because
/// model binding needs settable properties (Contracts use positional records).</summary>

public sealed class ProductFormViewModel
{
    public Guid Id { get; set; }

    [Required(ErrorMessage = "Name is required.")]
    [StringLength(200, ErrorMessage = "Name is too long.")]
    public string Name { get; set; } = string.Empty;

    // SKU is auto-suggested from the product name and may be left blank (the create
    // handler generates one). When supplied it must match the exact 9-char format,
    // which is enforced server-side by the create validator.
    [StringLength(50, ErrorMessage = "SKU is too long.")]
    public string Sku { get; set; } = string.Empty;

    [StringLength(4000, ErrorMessage = "Description is too long.")]
    public string? Description { get; set; }

    [Required(ErrorMessage = "Category is required.")]
    public Guid CategoryId { get; set; }

    [Range(0.01, double.MaxValue, ErrorMessage = "Selling price must be greater than zero.")]
    public decimal SellingPrice { get; set; }

    public bool IsCustomizable { get; set; }
    public bool IsFeatured { get; set; }

    [Range(0, int.MaxValue, ErrorMessage = "Quantity cannot be negative.")]
    public int InitialQuantity { get; set; }

    [Range(0, int.MaxValue, ErrorMessage = "Threshold cannot be negative.")]
    public int LowStockThreshold { get; set; }

    /// <summary>New images to upload (JPEG, PNG, WebP, GIF). First uploaded image becomes primary.
    /// At least one image is required when creating a product (enforced in the controller,
    /// since IFormFile arrays cannot carry DataAnnotations) and when editing a product that
    /// currently has no images.</summary>
    public Microsoft.AspNetCore.Http.IFormFile[]? Images { get; set; }
}

public sealed class CategoryFormViewModel
{
    public Guid? MajorCategoryId { get; set; }
    public Guid Id { get; set; }

    [Required(ErrorMessage = "Name is required.")]
    [StringLength(120, ErrorMessage = "Name is too long.")]
    public string Name { get; set; } = string.Empty;

    [StringLength(1000, ErrorMessage = "Description is too long.")]
    public string? Description { get; set; }

    public bool IsActive { get; set; } = true;
}


/// <summary>
/// Admin form for a Home page review (testimonial shown in the slider). Only the fields the
/// storefront actually renders, plus publish state and slider order — nothing else from the
/// entity is exposed (owner directive 2026-10-07).
/// </summary>
public sealed class ReviewFormViewModel
{
    public Guid Id { get; set; }

    [Required(ErrorMessage = "Customer name is required.")]
    [StringLength(160, ErrorMessage = "Customer name is too long.")]
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>Role/company line under the name, e.g. "Founder, EAYoruba".</summary>
    [StringLength(200, ErrorMessage = "Title is too long.")]
    public string? CustomerTitle { get; set; }

    [Required(ErrorMessage = "Review text is required.")]
    [StringLength(2000, ErrorMessage = "Review text is too long.")]
    public string Body { get; set; } = string.Empty;

    /// <summary>Optional avatar/logo. Leave empty to show the customer's initial instead.</summary>
    [StringLength(500, ErrorMessage = "Image URL is too long.")]
    public string? AvatarUrl { get; set; }

    /// <summary>Optional upload; when supplied it replaces AvatarUrl.</summary>
    public Microsoft.AspNetCore.Http.IFormFile? AvatarFile { get; set; }

    /// <summary>The slider renders stars, so the rating is part of the existing design.</summary>
    [Range(1, 5, ErrorMessage = "Rating must be between 1 and 5.")]
    public int Rating { get; set; } = 5;

    /// <summary>Defaults to false so an omitted form value can never publish by accident;
    /// the Create action pre-checks it for a new review.</summary>
    public bool IsPublished { get; set; }

    /// <summary>Position in the slider (lower first). 0 = append to the end.</summary>
    [Range(0, 9999, ErrorMessage = "Display order must be 0 or more.")]
    public int DisplayOrder { get; set; }
}

public sealed class ServiceFormViewModel
{
    public Guid Id { get; set; }

    [Required(ErrorMessage = "Name is required.")]
    [StringLength(200, ErrorMessage = "Name is too long.")]
    public string Name { get; set; } = string.Empty;

    [StringLength(4000, ErrorMessage = "Description is too long.")]
    public string? Description { get; set; }

    [Range(0, double.MaxValue, ErrorMessage = "Price cannot be negative.")]
    public decimal BasePrice { get; set; }

    [StringLength(20, ErrorMessage = "Color is too long.")]
    public string? IconColor { get; set; }

    public bool IsFeatured { get; set; }

    public bool IsActive { get; set; } = true;
}

public sealed class ChangePasswordFormViewModel
{
    [Required(ErrorMessage = "Current password is required.")]
    [DataType(DataType.Password)]
    public string CurrentPassword { get; set; } = "";

    [Required(ErrorMessage = "New password is required.")]
    [DataType(DataType.Password)]
    [MinLength(8, ErrorMessage = "New password must be at least 8 characters.")]
    public string NewPassword { get; set; } = "";

    [Required(ErrorMessage = "Confirm the new password.")]
    [DataType(DataType.Password)]
    [Compare(nameof(NewPassword), ErrorMessage = "The passwords do not match.")]
    public string ConfirmPassword { get; set; } = "";
}

/// <summary>
/// Admin form for the customer-facing announcement ticker. IsActive defaults to FALSE
/// (fail-closed, same rule as ReviewFormViewModel); the Create GET pre-checks the box so
/// the normal flow still activates the announcement.
/// </summary>
public sealed class AnnouncementFormViewModel : IValidatableObject
{
    public Guid Id { get; set; }

    [Required(ErrorMessage = "Announcement message is required.")]
    [StringLength(200, ErrorMessage = "Announcement message must be at most 200 characters.")]
    public string Message { get; set; } = string.Empty;

    /// <summary>Optional visibility window start. Null = visible immediately when active.</summary>
    [Display(Name = "Visible from (optional)")]
    public DateTime? StartAt { get; set; }

    /// <summary>Optional visibility window end. Null = never expires on its own.</summary>
    [Display(Name = "Visible until (optional)")]
    public DateTime? EndAt { get; set; }

    /// <summary>Manual active switch; only active announcements inside their window show.</summary>
    [Display(Name = "Active (show to customers)")]
    public bool IsActive { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (StartAt is not null && EndAt is not null && EndAt < StartAt)
            yield return new ValidationResult("End date must be after the start date.", new[] { nameof(EndAt) });
    }
}
