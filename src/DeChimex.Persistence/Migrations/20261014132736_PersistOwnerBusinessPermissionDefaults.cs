using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeChimex.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersistOwnerBusinessPermissionDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Some existing installations already have this nullable column even though
            // this migration is not in their EF history. Reconcile both states safely:
            // add it for fresh/older databases and leave an existing column untouched.
            migrationBuilder.Sql("""
                IF COL_LENGTH(N'dbo.Roles', N'OwnerBusinessPermissionDefaultsSnapshot') IS NULL
                BEGIN
                    ALTER TABLE [dbo].[Roles] ADD [OwnerBusinessPermissionDefaultsSnapshot] nvarchar(max) NULL;
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally retain the nullable column. Its provenance is ambiguous on
            // databases where it pre-existed this migration, and dropping it could erase
            // persisted Owner permission-default state. An extra nullable column is safe
            // for the previous application version.
        }
    }
}
