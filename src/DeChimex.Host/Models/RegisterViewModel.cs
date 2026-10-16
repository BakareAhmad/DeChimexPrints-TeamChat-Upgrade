using System.ComponentModel.DataAnnotations;

namespace DeChimex.Host.Models;

/// <summary>
/// Model for the MVC customer registration / sign up form.
/// Gradual signup (owner directive 2026-10-07): the form asks for ONLY what is needed to
/// authenticate — email and password. No confirm-password, name, phone or address: the rest of
/// the profile is collected later, at the point it becomes relevant (order placement, via
/// portal/profile/complete). Keep this list minimal; anything extra belongs in profile completion.
/// </summary>
public sealed class RegisterViewModel
{
    /// <summary>AUD-024 honeypot: hidden in the UI (CSS), never filled by humans. A value
    /// means a bot submitted the form — the controller pretends success and does nothing.</summary>
    public string? CompanyWebsite { get; set; }

    [Required(ErrorMessage = "Email is required.")]
    [EmailAddress(ErrorMessage = "Enter a valid email address.")]
    [MaxLength(256)]
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// Password security is unchanged by removing the confirm field: the same application rules
    /// apply server-side in RegisterValidator (min 8, upper, lower, digit) and are restated here
    /// for inline client feedback. Confirmation was client-side only, so removing it does not
    /// weaken anything.
    /// </summary>
    [Required(ErrorMessage = "Password is required.")]
    [DataType(DataType.Password)]
    [MinLength(8, ErrorMessage = "Password must be at least 8 characters long.")]
    [RegularExpression(@"^(?=.*[A-Z])(?=.*[a-z])(?=.*[0-9]).{8,128}$",
        ErrorMessage = "Password needs at least 8 characters, including an uppercase letter, a lowercase letter and a digit.")]
    public string Password { get; set; } = string.Empty;

    public string? ReturnUrl { get; set; }
}
