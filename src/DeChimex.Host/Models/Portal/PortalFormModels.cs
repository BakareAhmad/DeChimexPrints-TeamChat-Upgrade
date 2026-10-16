using System.ComponentModel.DataAnnotations;

namespace DeChimex.Host.Models.Portal;

/// <summary>Form models for the customer portal MVC pages.</summary>

/// <summary>
/// Order-time profile completion (owner directive 2026-10-07). Gradual signup means an account
/// can exist with only credentials, so this step collects ONLY the fields the order workflow
/// genuinely needs — the name printed on orders/quotes/invoices and the contact phone printed on
/// the invoice and used for fulfilment. The delivery address is collected on the checkout form
/// itself, and no other customer field is asked for. It reads as a profile update (no email, no
/// password, values pre-filled), never as a second registration.
/// </summary>
public sealed class ProfileCompletionViewModel
{
    [Required(ErrorMessage = "First name is required.")]
    [MaxLength(80)]
    public string FirstName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Last name is required.")]
    [MaxLength(80)]
    public string LastName { get; set; } = string.Empty;

    /// <summary>Required: printed on the invoice and used as the delivery/fulfilment contact.</summary>
    [Required(ErrorMessage = "A contact phone number is required for your order.")]
    [RegularExpression(@"^\+?[0-9\s\-()]{7,20}$", ErrorMessage = "Enter a valid phone number.")]
    public string Phone { get; set; } = string.Empty;

    /// <summary>Where to return after saving (e.g. /checkout). Local URLs only.</summary>
    public string? ReturnUrl { get; set; }
}

public sealed class ProfileFormViewModel
{
    [Required(ErrorMessage = "First name is required.")]
    public string FirstName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Last name is required.")]
    public string LastName { get; set; } = string.Empty;

    public string? Phone { get; set; }
    public string? Notes { get; set; }
}

public sealed class AddressFormViewModel
{
    [Required(ErrorMessage = "Label is required.")]
    public string Label { get; set; } = string.Empty;

    [Required(ErrorMessage = "Street is required.")]
    public string Street { get; set; } = string.Empty;

    [Required(ErrorMessage = "City is required.")]
    public string City { get; set; } = string.Empty;

    [Required(ErrorMessage = "State is required.")]
    public string State { get; set; } = string.Empty;

    [Required(ErrorMessage = "Country is required.")]
    public string Country { get; set; } = "Nigeria";

    public string? PostalCode { get; set; }
    public bool IsDefault { get; set; }
}

public sealed class ChangePasswordFormViewModel
{
    [Required(ErrorMessage = "Current password is required.")]
    [DataType(DataType.Password)]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "New password is required.")]
    [MinLength(8, ErrorMessage = "Password must be at least 8 characters.")]
    [RegularExpression("^(?=.*[A-Z])(?=.*[a-z])(?=.*[0-9]).*$",
        ErrorMessage = "Password must contain upper and lower case letters and a number.")]
    [DataType(DataType.Password)]
    public string NewPassword { get; set; } = string.Empty;

    [Compare(nameof(NewPassword), ErrorMessage = "Passwords do not match.")]
    [DataType(DataType.Password)]
    public string ConfirmPassword { get; set; } = string.Empty;
}
