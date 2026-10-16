using System.Text.RegularExpressions;
using DeChimex.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace DeChimex.UnitTests.Persistence;

/// <summary>
/// Regression guards for the SQL Server migration chain (regeneration of the real DDL via
/// IMigrator.GenerateScript — no database connection is opened).
///
/// Bug being guarded against: 20261004110951_PwaPushSubscriptions originally only diffed an
/// index against a stale snapshot, so a database at 20260925232100 (i.e. any real database)
/// hit "Cannot drop the index 'PushSubscriptions.IX_PushSubscriptions_UserId_Endpoint',
/// because it does not exist or you do not have permission." (SQL error 3701) during
/// Update-Database — the PushSubscriptions table itself was never created by any migration.
/// </summary>
public class MigrationChainTests
{
    private static string Script(string from, string? to = null)
    {
        using var context = new DesignTimeDbContextFactory().CreateDbContext(Array.Empty<string>());
        return context.GetService<IMigrator>().GenerateScript(from, to);
    }

    [Fact]
    public void Upgrade_from_20260925_creates_the_push_table_without_dropping_indexes()
    {
        var hop = Script("20260925232100_AddDeveloperPortalDiagnostics");

        Assert.Contains("CREATE TABLE [PushSubscriptions]", hop);
        Assert.Contains("CREATE INDEX [IX_PushSubscriptions_Endpoint] ON [PushSubscriptions] ([Endpoint]);", hop);
        Assert.Contains(
            "CREATE UNIQUE INDEX [IX_PushSubscriptions_UserId_Endpoint] ON [PushSubscriptions] ([UserId], [Endpoint]) WHERE [IsDeleted] = 0",
            hop);
        Assert.DoesNotContain("DROP INDEX", hop);
    }

    [Fact]
    public void PushSubscriptions_is_created_exactly_once_by_the_full_chain()
    {
        var full = Script("0");

        Assert.Equal(1, Regex.Matches(full, @"CREATE TABLE \[PushSubscriptions\]").Count);
    }

    [Fact]
    public void No_index_or_table_is_dropped_before_its_table_exists_in_the_full_chain()
    {
        var full = Script("0");

        var tableCreates = Regex.Matches(full, @"CREATE TABLE \[([^\]]+)\]")
            .ToDictionary(m => m.Groups[1].Value, m => m.Index);
        Assert.NotEmpty(tableCreates);

        foreach (Match drop in Regex.Matches(full, @"DROP (?:INDEX \[[^\]]+\] ON|TABLE) \[([^\]]+)\]"))
        {
            var table = drop.Groups[1].Value;
            Assert.True(
                tableCreates.TryGetValue(table, out var createdAt) && drop.Index > createdAt,
                $"'{drop.Value}' at offset {drop.Index} executes before/at the creation of table '{table}' " +
                "— a fresh Update-Database would fail with SQL error 3701.");
        }
    }

    [Fact]
    public void TeamChat_schema_changes_are_additive_separate_and_snapshot_consistent()
    {
        using var context = new DesignTimeDbContextFactory().CreateDbContext(Array.Empty<string>());
        var migrations = context.Database.GetMigrations().ToArray();
        Assert.True(migrations.Length >= 3);
        Assert.EndsWith("_PersistOwnerBusinessPermissionDefaults", migrations[^2]);
        Assert.EndsWith("_TeamChatAttachments", migrations[^1]);

        // A pending owner-permission schema correction predates this feature and remains isolated.
        // Some databases already have the column outside EF history, so the upgrade is conditional;
        // rollback must retain it because its pre-migration provenance cannot be known safely.
        var ownerHop = Script(migrations[^3], migrations[^2]);
        Assert.Contains("IF COL_LENGTH(N'dbo.Roles', N'OwnerBusinessPermissionDefaultsSnapshot') IS NULL", ownerHop);
        Assert.Contains("ALTER TABLE [dbo].[Roles] ADD [OwnerBusinessPermissionDefaultsSnapshot] nvarchar(max) NULL", ownerHop);
        Assert.DoesNotContain("InternalMessageAttachments", ownerHop);
        Assert.DoesNotContain("ClientMessageId", ownerHop);
        var ownerRollback = Script(migrations[^2], migrations[^3]);
        Assert.DoesNotContain("DROP COLUMN [OwnerBusinessPermissionDefaultsSnapshot]", ownerRollback);

        // Team Chat adds an optional retry key and a private-message attachment child table.
        // Pre-existing objects are adopted only after their schema/constraints are validated;
        // rollback must not delete objects whose provenance may predate EF history.
        var chatHop = Script(migrations[^2], migrations[^1]);
        Assert.Contains("IF COL_LENGTH(N'dbo.InternalMessages', N'ClientMessageId') IS NULL", chatHop);
        Assert.Contains("ALTER TABLE [dbo].[InternalMessages] ADD [ClientMessageId] uniqueidentifier NULL;", chatHop);
        Assert.Contains("THROW 51001", chatHop);
        Assert.Contains("IF OBJECT_ID(N'dbo.InternalMessageAttachments', N'U') IS NULL", chatHop);
        Assert.Contains("CREATE TABLE [dbo].[InternalMessageAttachments]", chatHop);
        Assert.Contains("THROW 51002", chatHop);
        Assert.Contains("THROW 51004", chatHop);
        Assert.Contains("THROW 51005", chatHop);
        Assert.Contains("ON DELETE CASCADE", chatHop);
        Assert.Contains("CREATE UNIQUE INDEX [IX_InternalMessages_ConversationId_SenderId_ClientMessageId]", chatHop);
        Assert.Contains("WHERE [ClientMessageId] IS NOT NULL", chatHop);
        Assert.Contains("THROW 51006", chatHop);
        Assert.Contains("CREATE INDEX [IX_InternalMessageAttachments_MessageId]", chatHop);
        Assert.Contains("THROW 51007", chatHop);
        Assert.DoesNotContain("DROP TABLE", chatHop);
        var chatRollback = Script(migrations[^1], migrations[^2]);
        Assert.DoesNotContain("DROP TABLE", chatRollback);
        Assert.DoesNotContain("DROP INDEX", chatRollback);
        Assert.DoesNotContain("DROP COLUMN", chatRollback);

        Assert.False(context.Database.HasPendingModelChanges());
    }
}
