using System.Net;
using System.Text.RegularExpressions;
using DeChimex.Infrastructure.Persistence;
using DeChimex.Shared.Constants;
using DeChimex.Persistence.Context;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Verifies the admin role-permission checklist end to end (role-permission checklist
/// requirement, 2026-08-28): an admin can edit the checklist of any role, including the
/// seeded system roles, the change applies immediately, and it survives another seeder
/// run (simulated application restart). A deleted role is re-created with its canonical map.
/// </summary>
public class RolePermissionChecklistTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public RolePermissionChecklistTests(CustomWebApplicationFactory factory) => _factory = factory;

    private sealed record Perm(string Name, string Id, bool Checked);

    [Fact]
    public async Task System_role_checklist_edits_apply_immediately_and_survive_reseed()
    {
        var client = await LoginAsOwnerAsync();

        // Counts are derived from the registry itself so adding a permission (e.g. reviews.view/
        // reviews.manage, 2026-10-07) does not silently invalidate this checklist test.
        var registry = Permissions.All();
        const int managerExcluded = 6; // users.view/create/update/disable/assignroles + activity.view
        var managerCanonical = registry.Count - managerExcluded;

        // 1. Manager starts with the canonical map: everything except users.* and the
        //    owner-default activity.view permission.
        var permLink = await PermissionsLinkForRoleAsync(client, "Manager");
        var page = await (await client.GetAsync(permLink)).Content.ReadAsStringAsync();
        var perms = ParsePermissions(page);
        perms.Count.Should().Be(registry.Count, "the full permission registry is offered on every checklist");
        perms.Count(p => p.Checked).Should().Be(managerCanonical);
        perms.Where(p => !p.Checked).Select(p => p.Name)
            .Should().BeEquivalentTo("users.view", "users.create", "users.update", "users.disable", "users.assignroles", "activity.view");

        // 2. The admin unchecks settings.manage and saves through the real UI.
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var keep = perms.Where(p => p.Checked && p.Name != "settings.manage").Select(p => p.Id).ToList();
        var form = new FormUrlEncodedContent(keep
            .Select(id => new KeyValuePair<string, string>("PermissionIds", id))
            .Append(new KeyValuePair<string, string>("__RequestVerificationToken", token)));
        var save = await client.PostAsync(permLink, form);
        save.StatusCode.Should().Be(HttpStatusCode.Redirect);

        // 3. The edit applied immediately: one fewer granted, settings.manage gone.
        page = await (await client.GetAsync(permLink)).Content.ReadAsStringAsync();
        perms = ParsePermissions(page);
        perms.Count(p => p.Checked).Should().Be(managerCanonical - 1);
        perms.Should().NotContain(p => p.Checked && p.Name == "settings.manage");

        // 4. Simulate an application restart: run the seeder again against the same database.
        using (var scope = _factory.Services.CreateScope())
        {
            var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
            await seeder.SeedAsync();
        }

        // 5. The admin edit survived the restart.
        page = await (await client.GetAsync(permLink)).Content.ReadAsStringAsync();
        perms = ParsePermissions(page);
        perms.Count(p => p.Checked).Should().Be(managerCanonical - 1, "the seeder must not re-sync permissions of existing roles");
        perms.Should().NotContain(p => p.Checked && p.Name == "settings.manage");

        // 6. Untouched system roles still hold their canonical maps.
        (await CheckedCountForRoleAsync(client, "Owner")).Should().Be(registry.Count);
        (await CheckedCountForRoleAsync(client, "SalesStaff")).Should().Be(9);
        (await CheckedCountForRoleAsync(client, "InventoryStaff")).Should().Be(8, "canonical map now includes messages.view (owner directive 2026-09-08)");
        (await CheckedCountForRoleAsync(client, "Designer")).Should().Be(5, "canonical map now includes messages.view (owner directive 2026-09-08)");
        (await CheckedCountForRoleAsync(client, "Customer")).Should().Be(1);
    }

    [Fact]
    public async Task Owner_receives_new_registry_permissions_without_reverting_manual_edits_or_escalating_manager()
    {
        // Isolate this authority-changing test from the shared class fixture's other role edits.
        using var isolatedFactory = new CustomWebApplicationFactory();
        using var ownerClient = await LoginAsOwnerAsync(isolatedFactory);
        var registry = Permissions.All();
        var ownerLink = await PermissionsLinkForRoleAsync(ownerClient, "Owner");
        var initialOwner = ParsePermissions(await (await ownerClient.GetAsync(ownerLink)).Content.ReadAsStringAsync());
        initialOwner.Select(permission => permission.Name).Should().BeEquivalentTo(registry);
        initialOwner.Should().OnlyContain(permission => permission.Checked,
            "a newly seeded Owner starts with every business permission selected");

        var newlyIntroduced = Permissions.Users.AssignRoles;
        var intentionallyDeselected = Permissions.Settings.Manage;
        var managerEmail = $"permission-manager-{Guid.NewGuid():N}@example.com";
        List<string> managerPermissionsBefore;
        string ownerStampBeforeReseed;

        // Model an existing Owner database that has not yet recorded its permission snapshot.
        // The initial baseline fills the current registry; future edits are then protected by the snapshot.
        using (var scope = isolatedFactory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var ownerRole = await db.Roles.Include(role => role.Permissions)
                .SingleAsync(role => role.NormalizedName == RoleNames.Owner.ToUpperInvariant());
            var targetIds = await db.Permissions
                .Where(permission => permission.Name == newlyIntroduced || permission.Name == intentionallyDeselected)
                .ToDictionaryAsync(permission => permission.Name, permission => permission.Id);
            targetIds.Keys.Should().BeEquivalentTo(newlyIntroduced, intentionallyDeselected);
            ownerRole.SetPermissions(ownerRole.Permissions.Select(link => link.PermissionId)
                .Where(id => id != targetIds[newlyIntroduced]));
            db.Entry(ownerRole).Property(role => role.OwnerBusinessPermissionDefaultsSnapshot).CurrentValue = null;

            var ownerUser = await db.Users.FirstAsync(user => user.Email.Value == TestSeed.OwnerEmail);
            ownerUser.BumpSecurityStamp();
            var managerRole = await db.Roles.Include(role => role.Permissions).ThenInclude(link => link.Permission)
                .SingleAsync(role => role.NormalizedName == RoleNames.Manager.ToUpperInvariant());
            managerPermissionsBefore = managerRole.Permissions.Select(link => link.Permission!.Name)
                .OrderBy(name => name, StringComparer.Ordinal).ToList();
            await db.SaveChangesAsync();
        }

        using (var scope = isolatedFactory.Services.CreateScope())
        {
            var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
            await seeder.SeedAsync();
        }
        var afterLegacyAdoptionResponse = await ownerClient.GetAsync(ownerLink);
        afterLegacyAdoptionResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "the one-time Owner baseline should be reflected in existing sessions");
        var afterLegacyAdoption = ParsePermissions(await afterLegacyAdoptionResponse.Content.ReadAsStringAsync());
        afterLegacyAdoption.Should().OnlyContain(permission => permission.Checked);

        // Model a later registry release plus a separate manual deselection. The snapshot says
        // settings.manage was already known, but users.assignroles is newly introduced.
        using (var scope = isolatedFactory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var ownerRole = await db.Roles.Include(role => role.Permissions)
                .SingleAsync(role => role.NormalizedName == RoleNames.Owner.ToUpperInvariant());
            var targetIds = await db.Permissions
                .Where(permission => permission.Name == newlyIntroduced || permission.Name == intentionallyDeselected)
                .ToDictionaryAsync(permission => permission.Name, permission => permission.Id);
            ownerRole.SetPermissions(ownerRole.Permissions.Select(link => link.PermissionId)
                .Where(id => id != targetIds[newlyIntroduced] && id != targetIds[intentionallyDeselected]));
            ownerRole.RememberOwnerBusinessPermissionDefaults(
                registry.Where(name => name != newlyIntroduced));

            var ownerUser = await db.Users.FirstAsync(user => user.Email.Value == TestSeed.OwnerEmail);
            ownerUser.BumpSecurityStamp();
            ownerStampBeforeReseed = ownerUser.SecurityStamp;
            await db.SaveChangesAsync();
        }

        // The stale Owner cookie is refreshed from the edited role and loses assign-roles access.
        var deniedBeforeBackfill = await ownerClient.GetAsync(ownerLink);
        deniedBeforeBackfill.StatusCode.Should().Be(HttpStatusCode.Redirect);
        deniedBeforeBackfill.Headers.Location?.ToString().Should().Contain("/Account/AccessDenied");

        // Reseeding adds only the unrecorded permission and bumps the Owner security stamp, so
        // the browser receives the new claim without restoring the prior manual deselection.
        using (var scope = isolatedFactory.Services.CreateScope())
        {
            var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
            await seeder.SeedAsync();
        }

        var ownerResponse = await ownerClient.GetAsync(ownerLink);
        ownerResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "Owner's newly restored assign-roles permission must be honored server-side");
        var ownerAfter = ParsePermissions(await ownerResponse.Content.ReadAsStringAsync());
        ownerAfter.Select(permission => permission.Name).Should().BeEquivalentTo(registry);
        ownerAfter.Count(permission => permission.Checked).Should().Be(registry.Count - 1);
        ownerAfter.Should().Contain(permission => permission.Name == newlyIntroduced && permission.Checked);
        ownerAfter.Should().Contain(permission => permission.Name == intentionallyDeselected && !permission.Checked,
            "a deliberate edit to a previously known permission must survive reseeding");

        using (var scope = isolatedFactory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var ownerRole = await db.Roles.Include(role => role.Permissions).ThenInclude(link => link.Permission)
                .SingleAsync(role => role.NormalizedName == RoleNames.Owner.ToUpperInvariant());
            var managerRole = await db.Roles.Include(role => role.Permissions).ThenInclude(link => link.Permission)
                .SingleAsync(role => role.NormalizedName == RoleNames.Manager.ToUpperInvariant());
            var ownerUser = await db.Users.FirstAsync(user => user.Email.Value == TestSeed.OwnerEmail);
            var managerPermissionsAfter = managerRole.Permissions.Select(link => link.Permission!.Name)
                .OrderBy(name => name, StringComparer.Ordinal).ToList();

            ownerRole.Permissions.Select(link => link.Permission!.Name)
                .Should().NotContain(Permissions.Developer.All(), "Developer Portal grants remain isolated");
            ownerRole.GetOwnerBusinessPermissionDefaultsSnapshot().Should().BeEquivalentTo(registry);
            managerPermissionsAfter.Should().Equal(managerPermissionsBefore,
                "Owner defaults must not modify the Manager role's checklist");
            managerPermissionsAfter.Should().NotContain(Permissions.Users.AssignRoles);
            ownerUser.SecurityStamp.Should().NotBe(ownerStampBeforeReseed,
                "new Owner authority must invalidate stale permission claims");
        }

        // A real Manager session remains outside the staff/role-management authorization policy.
        await TestSeed.CreateStaffUserAsync(isolatedFactory.Services, managerEmail, TestSeed.DefaultPassword, RoleNames.Manager);
        using var managerClient = isolatedFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        (await TestSeed.LoginMvcAsync(managerClient, managerEmail, TestSeed.DefaultPassword)).Should().BeTrue();
        var managerAttempt = await managerClient.GetAsync("/admin/users/roles");
        managerAttempt.StatusCode.Should().Be(HttpStatusCode.Redirect);
        managerAttempt.Headers.Location?.ToString().Should().Contain("/Account/AccessDenied");
    }

    [Fact]
    public async Task A_role_deleted_out_of_band_is_recreated_with_its_canonical_map()
    {
        var client = await LoginAsOwnerAsync();
        var permLink = await PermissionsLinkForRoleAsync(client, "Designer");
        var checkedBefore = ParsePermissions(await (await client.GetAsync(permLink)).Content.ReadAsStringAsync());
        checkedBefore.Count(p => p.Checked).Should().Be(5);

        // Delete Designer directly through the database (bypassing the role-delete guard,
        // which correctly blocks deleting system roles from the UI).
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            var role = await db.Roles.FirstOrDefaultAsync(r => r.Name == "Designer");
            role.Should().NotBeNull();
            db.Roles.Remove(role!);
            await db.SaveChangesAsync();
        }

        // The next seeder run (application restart) must re-create the role.
        using (var scope = _factory.Services.CreateScope())
        {
            var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
            await seeder.SeedAsync();
        }

        var rolesPage = await (await client.GetAsync("/admin/users/roles")).Content.ReadAsStringAsync();
        var permLinkAfter = await PermissionsLinkForRoleAsync(client, "Designer");
        var checkedAfter = ParsePermissions(await (await client.GetAsync(permLinkAfter)).Content.ReadAsStringAsync());
        checkedAfter.Select(p => p.Name).Should().BeEquivalentTo(checkedBefore.Select(p => p.Name));
        checkedAfter.Count(p => p.Checked).Should().Be(5, "re-created roles get the canonical permission map (Designer now includes messages.view)");
    }

    private async Task<HttpClient> LoginAsOwnerAsync(CustomWebApplicationFactory? factory = null)
    {
        var client = (factory ?? _factory).CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        // The seeded owner is forced to change the bootstrap password on first login.
        // An earlier test in this class may already have done that, so fall back to the
        // known changed password when the bootstrap one no longer works.
        var ok = await TryOwnerLoginAsync(client, "ChangeMe123!");
        if (!ok)
            ok = await TryOwnerLoginAsync(client, "Owner!Pass9");
        ok.Should().BeTrue("owner login should succeed with the bootstrap or the changed password");
        return client;
    }

    private async Task<bool> TryOwnerLoginAsync(HttpClient client, string password)
    {
        var html = await (await client.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = "owner@dechimexprinthub.com",
            ["Password"] = password,
            ["__RequestVerificationToken"] = token
        }));
        if (response.StatusCode != HttpStatusCode.Redirect)
            return false;

        var location = response.Headers.Location?.ToString() ?? "";
        if (location.Contains("/admin/security/password"))
            await CompleteForcedPasswordChangeAsync(client, password);
        return true;
    }

    private static async Task CompleteForcedPasswordChangeAsync(HttpClient client, string currentPassword)
    {
        var html = await (await client.GetAsync("/admin/security/password")).Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var response = await client.PostAsync("/admin/security/password", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["CurrentPassword"] = currentPassword,
            ["NewPassword"] = "Owner!Pass9",
            ["ConfirmPassword"] = "Owner!Pass9",
            ["__RequestVerificationToken"] = token
        }));
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
    }
    private static async Task<string> PermissionsLinkForRoleAsync(HttpClient client, string roleName)
    {
        var html = await (await client.GetAsync("/admin/users/roles")).Content.ReadAsStringAsync();
        var row = Regex.Split(html, "<tr").FirstOrDefault(part => part.Contains($">{roleName}</td>"));
        row.Should().NotBeNull($"the roles list should contain a row for {roleName}");
        return Regex.Match(row!, "href=\"(/admin/users/roles/[0-9a-fA-F-]{36}/permissions)\"").Groups[1].Value;
    }

    private static async Task<int> CheckedCountForRoleAsync(HttpClient client, string roleName) =>
        ParsePermissions(await (await client.GetAsync(await PermissionsLinkForRoleAsync(client, roleName))).Content.ReadAsStringAsync())
            .Count(p => p.Checked);

    private static List<Perm> ParsePermissions(string html)
    {
        // The checklist shows human-readable capability labels; the raw claim travels in an
        // invisible data-permission attribute (2026-10-08 portal cleanup).
        var rx = new Regex(@"<input\b(?=[^>]*\bname=""PermissionIds"")(?=[^>]*\bvalue=""(?<id>[0-9a-fA-F-]{36})"")(?=[^>]*\bdata-permission=""(?<name>[^""]+)"")(?<attrs>[^>]*)>", RegexOptions.IgnoreCase);
        return rx.Matches(html)
            .Select(match => new Perm(
                match.Groups["name"].Value,
                match.Groups["id"].Value,
                Regex.IsMatch(match.Groups["attrs"].Value, @"(?:^|\s)checked(?:\s|=|$)", RegexOptions.IgnoreCase)))
            .ToList();
    }
}
