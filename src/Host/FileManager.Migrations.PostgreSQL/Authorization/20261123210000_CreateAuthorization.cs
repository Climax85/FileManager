using System;
using FSH.Modules.Authorization.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileManager.Migrations.PostgreSQL.Authorization;

[DbContext(typeof(AuthorizationDbContext))]
[Migration("20261123210000_CreateAuthorization")]
public partial class CreateAuthorization : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(name: "authorization");

        migrationBuilder.CreateTable(
            name: "Organizations",
            schema: "authorization",
            columns: table => new
            {
                Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                TenantId = table.Column<string>(type: "text", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_Organizations", x => x.Id));

        migrationBuilder.CreateTable(
            name: "Folders",
            schema: "authorization",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OrganizationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                IsRoot = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                SynchronizationId = table.Column<Guid>(type: "uuid", nullable: true),
                TenantId = table.Column<string>(type: "text", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_Folders", x => x.Id));

        migrationBuilder.CreateTable(
            name: "RelationshipSynchronizations",
            schema: "authorization",
            columns: table => new
            {
                OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                OrganizationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                ZedToken = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                LastError = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                TenantId = table.Column<string>(type: "text", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_RelationshipSynchronizations", x => x.OperationId));

        migrationBuilder.CreateTable(
            name: "OutboxMessages",
            schema: "authorization",
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
            schema: "authorization",
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
            name: "UX_Folders_OneRootPerOrganization",
            schema: "authorization",
            table: "Folders",
            columns: new[] { "TenantId", "IsRoot" },
            unique: true,
            filter: "\"IsRoot\" = TRUE");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "InboxMessages", schema: "authorization");
        migrationBuilder.DropTable(name: "OutboxMessages", schema: "authorization");
        migrationBuilder.DropTable(name: "RelationshipSynchronizations", schema: "authorization");
        migrationBuilder.DropTable(name: "Folders", schema: "authorization");
        migrationBuilder.DropTable(name: "Organizations", schema: "authorization");
    }
}
