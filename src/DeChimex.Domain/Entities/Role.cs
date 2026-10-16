using DeChimex.Domain.Common;
using DeChimex.Domain.Exceptions;

namespace DeChimex.Domain.Entities;

/// <summary>A security role grouping a set of permissions.</summary>
public sealed class Role : AggregateRoot
{
    private readonly List<RolePermission> _permissions = new();

    private Role() { } // EF

    private Role(string name, string? description, bool isSystemRole)
    {
        Name = name;
        NormalizedName = name.ToUpperInvariant();
        Description = description;
        IsSystemRole = isSystemRole;
    }

    public string Name { get; private set; } = default!;
    public string NormalizedName { get; private set; } = default!;
    public string? Description { get; private set; }
    public bool IsSystemRole { get; private set; }

    /// <summary>
    /// Business permission names already considered for the Owner default. This is seed metadata,
    /// not an authorization source: role access remains exclusively in <see cref="Permissions"/>.
    /// Keeping the names lets future releases add newly registered business permissions to Owner
    /// without restoring older permissions an administrator deliberately unchecked through this sync.
    /// </summary>
    public string? OwnerBusinessPermissionDefaultsSnapshot { get; private set; }

    public IReadOnlyCollection<RolePermission> Permissions => _permissions.AsReadOnly();

    public IReadOnlySet<string> GetOwnerBusinessPermissionDefaultsSnapshot()
        => string.IsNullOrEmpty(OwnerBusinessPermissionDefaultsSnapshot)
            ? new HashSet<string>(StringComparer.Ordinal)
            : OwnerBusinessPermissionDefaultsSnapshot
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.Ordinal);

    public void RememberOwnerBusinessPermissionDefaults(IEnumerable<string> permissionNames)
    {
        var snapshot = string.Join('\n', permissionNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal));
        if (!string.Equals(OwnerBusinessPermissionDefaultsSnapshot, snapshot, StringComparison.Ordinal))
            OwnerBusinessPermissionDefaultsSnapshot = snapshot;
    }

    public static Role Create(string name, string? description = null, bool isSystemRole = false)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new BusinessRuleValidationException("role.name", "Role name is required.");
        return new Role(name.Trim(), description?.Trim(), isSystemRole);
    }

    public void Update(string name, string? description)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new BusinessRuleValidationException("role.name", "Role name is required.");
        Name = name.Trim();
        NormalizedName = name.Trim().ToUpperInvariant();
        Description = description?.Trim();
    }


    /// <summary>
    /// Reconciles the role's permissions to exactly the supplied set using a diff
    /// (add missing, remove extra) rather than clear-and-recreate. This keeps EF change
    /// tracking consistent and makes re-seeding / re-saving idempotent, avoiding phantom
    /// DELETEs that surface as DbUpdateConcurrencyException.
    /// </summary>
    public void SetPermissions(IEnumerable<Guid> permissionIds)
    {
        var desired = permissionIds.Distinct().ToHashSet();

        // Remove permissions no longer desired.
        _permissions.RemoveAll(rp => !desired.Contains(rp.PermissionId));

        // Add permissions that are missing.
        var existing = _permissions.Select(rp => rp.PermissionId).ToHashSet();
        foreach (var id in desired.Where(id => !existing.Contains(id)))
            _permissions.Add(RolePermission.Create(Id, id));
    }
}
