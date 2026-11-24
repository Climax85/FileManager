using System;
using FSH.Modules.Files.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileManager.Migrations.PostgreSQL.Files;

[DbContext(typeof(FilesDbContext))]
[Migration("20261123223000_AddFileResourceAuthorization")]
public partial class AddFileResourceAuthorization : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "ParentFolderId",
            schema: "files",
            table: "FileAssets",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "AuthorizationOperationId",
            schema: "files",
            table: "FileAssets",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "AuthorizationSynchronizationStatus",
            schema: "files",
            table: "FileAssets",
            type: "character varying(16)",
            maxLength: 16,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "AuthorizationToken",
            schema: "files",
            table: "FileAssets",
            type: "character varying(1024)",
            maxLength: 1024,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "AuthorizationSynchronizationError",
            schema: "files",
            table: "FileAssets",
            type: "character varying(2048)",
            maxLength: 2048,
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "AuthorizationConfirmedAtUtc",
            schema: "files",
            table: "FileAssets",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "OutboxMessages",
            schema: "files",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedOnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                Type = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                Payload = table.Column<string>(type: "text", nullable: false),
                TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                ProcessedOnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                RetryCount = table.Column<int>(type: "integer", nullable: false),
                LastError = table.Column<string>(type: "text", nullable: true),
                IsDead = table.Column<bool>(type: "boolean", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_OutboxMessages", x => x.Id));

        migrationBuilder.CreateTable(
            name: "InboxMessages",
            schema: "files",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                HandlerName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                EventType = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                ProcessedOnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
            },
            constraints: table => table.PrimaryKey("PK_InboxMessages", x => new { x.Id, x.HandlerName }));

        migrationBuilder.CreateIndex(
            name: "IX_FileAsset_ParentFolder",
            schema: "files",
            table: "FileAssets",
            column: "ParentFolderId");

        migrationBuilder.CreateIndex(
            name: "UX_FileAsset_AuthorizationOperation",
            schema: "files",
            table: "FileAssets",
            columns: new[] { "AuthorizationOperationId", "TenantId" },
            unique: true,
            filter: "\"AuthorizationOperationId\" IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "InboxMessages", schema: "files");
        migrationBuilder.DropTable(name: "OutboxMessages", schema: "files");

        migrationBuilder.DropIndex(
            name: "IX_FileAsset_ParentFolder",
            schema: "files",
            table: "FileAssets");

        migrationBuilder.DropIndex(
            name: "UX_FileAsset_AuthorizationOperation",
            schema: "files",
            table: "FileAssets");

        migrationBuilder.DropColumn(name: "ParentFolderId", schema: "files", table: "FileAssets");
        migrationBuilder.DropColumn(name: "AuthorizationOperationId", schema: "files", table: "FileAssets");
        migrationBuilder.DropColumn(name: "AuthorizationSynchronizationStatus", schema: "files", table: "FileAssets");
        migrationBuilder.DropColumn(name: "AuthorizationToken", schema: "files", table: "FileAssets");
        migrationBuilder.DropColumn(name: "AuthorizationSynchronizationError", schema: "files", table: "FileAssets");
        migrationBuilder.DropColumn(name: "AuthorizationConfirmedAtUtc", schema: "files", table: "FileAssets");
    }
}
