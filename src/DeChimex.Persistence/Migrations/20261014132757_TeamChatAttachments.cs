using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeChimex.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TeamChatAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Some installations have Team Chat schema objects outside EF history.
            // Reuse only objects whose shape matches the model; fail explicitly on drift.
            migrationBuilder.Sql("""
                IF COL_LENGTH(N'dbo.InternalMessages', N'ClientMessageId') IS NULL
                BEGIN
                    ALTER TABLE [dbo].[InternalMessages] ADD [ClientMessageId] uniqueidentifier NULL;
                END
                ELSE IF NOT EXISTS
                (
                    SELECT 1
                    FROM sys.columns AS c
                    INNER JOIN sys.types AS t ON t.user_type_id = c.user_type_id
                    WHERE c.object_id = OBJECT_ID(N'dbo.InternalMessages', N'U')
                      AND c.name = N'ClientMessageId'
                      AND t.name = N'uniqueidentifier'
                      AND c.is_nullable = 1
                )
                BEGIN
                    THROW 51001, N'InternalMessages.ClientMessageId exists with an unexpected type or nullability.', 1;
                END;

                IF OBJECT_ID(N'dbo.InternalMessageAttachments', N'U') IS NULL
                BEGIN
                    CREATE TABLE [dbo].[InternalMessageAttachments] (
                        [Id] uniqueidentifier NOT NULL,
                        [MessageId] uniqueidentifier NOT NULL,
                        [Kind] nvarchar(10) NOT NULL,
                        [OriginalFileName] nvarchar(260) NOT NULL,
                        [StoredFileName] nvarchar(500) NOT NULL,
                        [Url] nvarchar(1000) NOT NULL,
                        [ContentType] nvarchar(120) NOT NULL,
                        [SizeBytes] bigint NOT NULL,
                        [DurationSeconds] int NULL,
                        CONSTRAINT [PK_InternalMessageAttachments] PRIMARY KEY ([Id]),
                        CONSTRAINT [FK_InternalMessageAttachments_InternalMessages_MessageId]
                            FOREIGN KEY ([MessageId]) REFERENCES [dbo].[InternalMessages] ([Id]) ON DELETE CASCADE
                    );
                END
                ELSE
                BEGIN
                    IF EXISTS
                    (
                        SELECT 1
                        FROM (VALUES
                            (N'Id', N'uniqueidentifier', 16, CONVERT(bit, 0)),
                            (N'MessageId', N'uniqueidentifier', 16, CONVERT(bit, 0)),
                            (N'Kind', N'nvarchar', 20, CONVERT(bit, 0)),
                            (N'OriginalFileName', N'nvarchar', 520, CONVERT(bit, 0)),
                            (N'StoredFileName', N'nvarchar', 1000, CONVERT(bit, 0)),
                            (N'Url', N'nvarchar', 2000, CONVERT(bit, 0)),
                            (N'ContentType', N'nvarchar', 240, CONVERT(bit, 0)),
                            (N'SizeBytes', N'bigint', 8, CONVERT(bit, 0)),
                            (N'DurationSeconds', N'int', 4, CONVERT(bit, 1))
                        ) AS expected([ColumnName], [TypeName], [MaxLength], [IsNullable])
                        LEFT JOIN sys.columns AS c
                            ON c.object_id = OBJECT_ID(N'dbo.InternalMessageAttachments', N'U')
                           AND c.name = expected.[ColumnName]
                        LEFT JOIN sys.types AS t ON t.user_type_id = c.user_type_id
                        WHERE c.column_id IS NULL
                           OR t.name <> expected.[TypeName]
                           OR c.max_length <> expected.[MaxLength]
                           OR c.is_nullable <> expected.[IsNullable]
                    )
                    BEGIN
                        THROW 51002, N'InternalMessageAttachments has an unexpected column shape; inspect it before retrying.', 1;
                    END;

                    IF EXISTS
                    (
                        SELECT 1
                        FROM sys.columns AS c
                        WHERE c.object_id = OBJECT_ID(N'dbo.InternalMessageAttachments', N'U')
                          AND c.name NOT IN
                              (N'Id', N'MessageId', N'Kind', N'OriginalFileName', N'StoredFileName',
                               N'Url', N'ContentType', N'SizeBytes', N'DurationSeconds')
                          AND c.is_nullable = 0
                          AND c.default_object_id = 0
                          AND c.is_identity = 0
                          AND c.is_computed = 0
                          AND c.system_type_id <> 189
                    )
                    BEGIN
                        THROW 51003, N'InternalMessageAttachments has an additional required column without a default; inspect it before retrying.', 1;
                    END;

                    IF NOT EXISTS
                    (
                        SELECT 1
                        FROM sys.key_constraints AS kc
                        WHERE kc.parent_object_id = OBJECT_ID(N'dbo.InternalMessageAttachments', N'U')
                          AND kc.name = N'PK_InternalMessageAttachments'
                          AND kc.type = N'PK'
                          AND (SELECT COUNT(*)
                               FROM sys.index_columns AS ic
                               WHERE ic.object_id = kc.parent_object_id
                                 AND ic.index_id = kc.unique_index_id
                                 AND ic.key_ordinal > 0) = 1
                          AND EXISTS
                              (SELECT 1
                               FROM sys.index_columns AS ic
                               INNER JOIN sys.columns AS c
                                   ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                               WHERE ic.object_id = kc.parent_object_id
                                 AND ic.index_id = kc.unique_index_id
                                 AND ic.key_ordinal = 1
                                 AND c.name = N'Id')
                    )
                    BEGIN
                        THROW 51004, N'InternalMessageAttachments is missing the expected primary key; inspect it before retrying.', 1;
                    END;

                    IF NOT EXISTS
                    (
                        SELECT 1
                        FROM sys.foreign_keys AS fk
                        WHERE fk.parent_object_id = OBJECT_ID(N'dbo.InternalMessageAttachments', N'U')
                          AND fk.name = N'FK_InternalMessageAttachments_InternalMessages_MessageId'
                          AND fk.referenced_object_id = OBJECT_ID(N'dbo.InternalMessages', N'U')
                          AND fk.delete_referential_action = 1
                          AND fk.is_disabled = 0
                          AND fk.is_not_trusted = 0
                          AND (SELECT COUNT(*)
                               FROM sys.foreign_key_columns AS fkc
                               WHERE fkc.constraint_object_id = fk.object_id) = 1
                          AND EXISTS
                              (SELECT 1
                               FROM sys.foreign_key_columns AS fkc
                               INNER JOIN sys.columns AS pc
                                   ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
                               INNER JOIN sys.columns AS rc
                                   ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
                               WHERE fkc.constraint_object_id = fk.object_id
                                 AND pc.name = N'MessageId'
                                 AND rc.name = N'Id')
                    )
                    BEGIN
                        THROW 51005, N'InternalMessageAttachments is missing the expected cascading message foreign key; inspect it before retrying.', 1;
                    END;
                END;

                IF EXISTS
                (
                    SELECT 1
                    FROM sys.indexes
                    WHERE object_id = OBJECT_ID(N'dbo.InternalMessages', N'U')
                      AND name = N'IX_InternalMessages_ConversationId_SenderId_ClientMessageId'
                )
                BEGIN
                    IF NOT EXISTS
                    (
                        SELECT 1
                        FROM sys.indexes AS i
                        WHERE i.object_id = OBJECT_ID(N'dbo.InternalMessages', N'U')
                          AND i.name = N'IX_InternalMessages_ConversationId_SenderId_ClientMessageId'
                          AND i.is_unique = 1
                          AND i.is_disabled = 0
                          AND i.is_hypothetical = 0
                          AND i.has_filter = 1
                          AND REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(LOWER(i.filter_definition), N' ', N''), N'[', N''), N']', N''), N'(', N''), N')', N'') = N'clientmessageidisnotnull'
                          AND (SELECT COUNT(*)
                               FROM sys.index_columns AS ic
                               WHERE ic.object_id = i.object_id
                                 AND ic.index_id = i.index_id
                                 AND ic.key_ordinal > 0) = 3
                          AND EXISTS
                              (SELECT 1 FROM sys.index_columns AS ic
                               INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                               WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                                 AND ic.key_ordinal = 1 AND ic.is_descending_key = 0 AND c.name = N'ConversationId')
                          AND EXISTS
                              (SELECT 1 FROM sys.index_columns AS ic
                               INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                               WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                                 AND ic.key_ordinal = 2 AND ic.is_descending_key = 0 AND c.name = N'SenderId')
                          AND EXISTS
                              (SELECT 1 FROM sys.index_columns AS ic
                               INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                               WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                                 AND ic.key_ordinal = 3 AND ic.is_descending_key = 0 AND c.name = N'ClientMessageId')
                    )
                    BEGIN
                        THROW 51006, N'The existing ClientMessageId index does not match the expected unique filtered index.', 1;
                    END;
                END
                ELSE
                BEGIN
                    CREATE UNIQUE INDEX [IX_InternalMessages_ConversationId_SenderId_ClientMessageId]
                        ON [dbo].[InternalMessages] ([ConversationId], [SenderId], [ClientMessageId])
                        WHERE [ClientMessageId] IS NOT NULL;
                END;

                IF EXISTS
                (
                    SELECT 1
                    FROM sys.indexes
                    WHERE object_id = OBJECT_ID(N'dbo.InternalMessageAttachments', N'U')
                      AND name = N'IX_InternalMessageAttachments_MessageId'
                )
                BEGIN
                    IF NOT EXISTS
                    (
                        SELECT 1
                        FROM sys.indexes AS i
                        WHERE i.object_id = OBJECT_ID(N'dbo.InternalMessageAttachments', N'U')
                          AND i.name = N'IX_InternalMessageAttachments_MessageId'
                          AND i.is_unique = 0
                          AND i.is_primary_key = 0
                          AND i.is_disabled = 0
                          AND i.is_hypothetical = 0
                          AND i.has_filter = 0
                          AND (SELECT COUNT(*)
                               FROM sys.index_columns AS ic
                               WHERE ic.object_id = i.object_id
                                 AND ic.index_id = i.index_id
                                 AND ic.key_ordinal > 0) = 1
                          AND EXISTS
                              (SELECT 1 FROM sys.index_columns AS ic
                               INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                               WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                                 AND ic.key_ordinal = 1 AND ic.is_descending_key = 0 AND c.name = N'MessageId')
                    )
                    BEGIN
                        THROW 51007, N'The existing InternalMessageAttachments index does not match the expected MessageId index.', 1;
                    END;
                END
                ELSE
                BEGIN
                    CREATE INDEX [IX_InternalMessageAttachments_MessageId]
                        ON [dbo].[InternalMessageAttachments] ([MessageId]);
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally non-destructive. This migration can adopt pre-existing
            // columns, a populated attachment table, and indexes whose provenance is
            // outside EF history. Dropping them would risk losing media metadata or keys.
        }
    }
}
