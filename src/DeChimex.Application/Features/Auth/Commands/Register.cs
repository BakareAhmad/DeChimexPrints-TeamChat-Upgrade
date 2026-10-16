using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Auth;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Repositories;
using DeChimex.Shared.Constants;
using FluentValidation;

namespace DeChimex.Application.Features.Auth.Commands;

public sealed record RegisterCommand(RegisterRequest Request, string? IpAddress) : ICommand<AuthResponse>;

public sealed class RegisterValidator : AbstractValidator<RegisterCommand>
{
    public RegisterValidator()
    {
        // Gradual signup: credentials only. Name/phone are collected at order-time profile
        // completion, so they are length-checked only when a caller supplies them.
        RuleFor(x => x.Request.FirstName).MaximumLength(80);
        RuleFor(x => x.Request.LastName).MaximumLength(80);
        RuleFor(x => x.Request.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Request.Phone).MaximumLength(20);
        RuleFor(x => x.Request.Password)
            .NotEmpty().MinimumLength(8).MaximumLength(128)
            .Matches("[A-Z]").WithMessage("Password must contain an uppercase letter.")
            .Matches("[a-z]").WithMessage("Password must contain a lowercase letter.")
            .Matches("[0-9]").WithMessage("Password must contain a digit.");
    }
}

/// <summary>
/// Registers a customer account: creates the login user, a linked customer profile, assigns the
/// Customer role, and issues tokens. Email verification is triggered via a domain event.
/// Anti-enumeration (AUD-010): when the email is already registered the handler answers with
/// the SAME success-shaped response as a fresh registration — no conflict, no oracle — so the
/// form flow simply tells the visitor a verification code was sent (ForgotPassword and
/// ResendVerification already behave this way).
/// </summary>
public sealed class RegisterHandler(
    IUserRepository users,
    ICustomerRepository customers,
    IRoleRepository roles,
    IPasswordHasher hasher,
    ISecureTokenGenerator tokenGenerator,
    IPermissionRepository permissions,
    IUnitOfWork uow) : ICommandHandler<RegisterCommand, AuthResponse>
{
    public async Task<AuthResponse> Handle(RegisterCommand command, CancellationToken ct)
    {
        var r = command.Request;
        if (await users.EmailExistsAsync(r.Email, ct))
        {
            // Pretend success: identical response shape, no reveal, no email storm to the
            // existing address (an attacker could otherwise use the endpoint to spam it).
            return new AuthResponse(Guid.Empty, r.Email, DisplayName(r),
                new[] { RoleNames.Customer }, Array.Empty<string>(), IsEmailVerified: false);
        }

        var passwordHash = hasher.Hash(r.Password);
        var verificationToken = tokenGenerator.GenerateNumericToken(); // 6-digit OTP: the user types this
        var hasNames = !string.IsNullOrWhiteSpace(r.FirstName) && !string.IsNullOrWhiteSpace(r.LastName);
        var user = hasNames
            ? User.Register(r.Email, passwordHash, r.FirstName!, r.LastName!, verificationToken)
            // Credentials-only signup: create the login with no profile details collected.
            : User.RegisterWithMinimalProfile(r.Email, passwordHash, verificationToken);

        var customerRole = await roles.GetByNameAsync(RoleNames.Customer, ct)
            ?? throw new NotFoundException("Role", RoleNames.Customer);
        user.AssignRole(customerRole.Id);
        await users.AddAsync(user, ct);

        var customer = hasNames
            ? Customer.Create(r.FirstName!, r.LastName!, r.Email, r.Phone, user.Id)
            // Progressive profile completion: the customer record exists (portal, cart,
            // wishlist, orders all resolve it) but HasCompleteProfile stays false until the
            // customer supplies the details the order workflow needs.
            : Customer.CreateForSignup(r.Email, user.Id, r.Phone);
        await customers.AddAsync(customer, ct);

        var roleNames = new[] { RoleNames.Customer };
        var permissionNames = await permissions.GetPermissionNamesForUserAsync(user.Id, ct);

        await uow.SaveChangesAsync(ct); // dispatches UserRegisteredEvent -> verification email

        return new AuthResponse(user.Id, user.Email.Value, user.FullName,
            roleNames, permissionNames, user.IsEmailVerified,
            SecurityStamp: user.SecurityStamp);
    }

    /// <summary>Display name for the response shape: falls back to the email when no name was
    /// collected at signup (gradual signup). Used by the anti-enumeration branch too, so both
    /// success-shaped responses stay identical.</summary>
    private static string DisplayName(RegisterRequest r)
    {
        var full = $"{r.FirstName} {r.LastName}".Trim();
        return string.IsNullOrWhiteSpace(full) ? r.Email : full;
    }
}
