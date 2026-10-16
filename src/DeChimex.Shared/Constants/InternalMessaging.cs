namespace DeChimex.Shared.Constants;

/// <summary>
/// Role-pair rules for the internal (work) messaging feature (owner directive 2026-10-07).
/// Management (Owner/Manager) may talk to each other, to staff and to the developer;
/// staff may only talk to management; staff-to-staff and staff-to-developer are NOT
/// allowed, and the developer only talks to management. The app is not a general
/// staff chat platform.
/// Every user is reduced to a single communication category (highest authority wins):
/// Owner &gt; Manager &gt; Staff (Sales/Inventory/Designer) &gt; Developer. Customers (or
/// role-less accounts) have no category and can never use the feature.
/// The rule set is enforced server-side in the application layer AND mirrored in the
/// contact-list UI, so disallowed partners can neither be discovered nor reached.
/// </summary>
public static class InternalMessaging
{
    public const string Owner = "Owner";
    public const string Manager = "Manager";
    public const string Staff = "Staff";
    public const string Developer = "Developer";

    /// <summary>The only unordered category pairs permitted to hold a conversation.</summary>
    private static readonly HashSet<string> AllowedPairs = new(StringComparer.Ordinal)
    {
        "Owner|Manager",
        "Owner|Staff",
        "Manager|Staff",
        "Owner|Developer",
        "Manager|Developer"
    };

    /// <summary>Reduces a role set to its single communication category; null means the
    /// account may not use internal messaging at all (customers, role-less accounts).</summary>
    public static string? CategoryFor(IEnumerable<string> roles)
    {
        var set = new HashSet<string>(roles, StringComparer.OrdinalIgnoreCase);
        if (set.Contains(RoleNames.Owner)) return Owner;
        if (set.Contains(RoleNames.Manager)) return Manager;
        if (set.Contains(RoleNames.SalesStaff) || set.Contains(RoleNames.InventoryStaff) || set.Contains(RoleNames.Designer)) return Staff;
        if (set.Contains(RoleNames.Developer)) return Developer;
        return null;
    }

    /// <summary>True when the two categories form an allowed conversation pair.
    /// Null/unknown categories and unlisted pairs (Staff|Staff, Staff|Developer,
    /// Developer|Developer, Owner|Owner, Manager|Manager) are denied.</summary>
    public static bool CanCommunicate(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        return AllowedPairs.Contains($"{a}|{b}") || AllowedPairs.Contains($"{b}|{a}");
    }

    /// <summary>Categories a user in <paramref name="category"/> may start conversations with
    /// (drives the contact list so disallowed users are never offered).</summary>
    public static IReadOnlyList<string> AllowedCounterparts(string? category)
    {
        return category switch
        {
            Owner => new[] { Manager, Staff, Developer },
            Manager => new[] { Owner, Staff, Developer },
            Staff => new[] { Owner, Manager },
            Developer => new[] { Owner, Manager },
            _ => Array.Empty<string>()
        };
    }
}
