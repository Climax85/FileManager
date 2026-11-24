using System;
using FSH.Modules.Authorization.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileManager.Migrations.PostgreSQL.Authorization;

[DbContext(typeof(AuthorizationDbContext))]
[Migration("20261123223000_AddResourceGrants")]
public partial class AddResourceGrants : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ResourceGrants",
            schema: "authorization",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                OrganizationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ResourceType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ResourceId = table.Column<Guid>(type: "uuid", nullable: false),
                SubjectType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                SubjectId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                SubjectRelation = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                Relation = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ZedToken = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                LastError = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                TenantId = table.Column<string>(type: "text", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_ResourceGrants", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_ResourceGrants_ResourceType_ResourceId_Status",
            schema: "authorization",
            table: "ResourceGrants",
            columns: new[] { "TenantId", "ResourceType", "ResourceId", "Status" });

        migrationBuilder.CreateIndex(
            name: "IX_ResourceGrants_TenantId_OperationId",
            schema: "authorization",
            table: "ResourceGrants",
            columns: new[] { "TenantId", "OperationId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "UX_ResourceGrants_ActiveSubjectRelation",
            schema: "authorization",
            table: "ResourceGrants",
            columns: new[] { "TenantId", "ResourceType", "ResourceId", "SubjectType", "SubjectId", "SubjectRelation", "Relation" },
            unique: true,
            filter: "\"Status\" <> 'Revoked'");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "ResourceGrants", schema: "authorization");
}
