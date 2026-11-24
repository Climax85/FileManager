using System;
using FSH.Modules.Authorization.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileManager.Migrations.PostgreSQL.Authorization;

[DbContext(typeof(AuthorizationDbContext))]
[Migration("20261123230000_AddFolderHierarchy")]
public partial class AddFolderHierarchy : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "ParentId",
            schema: "authorization",
            table: "Folders",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddUniqueConstraint(
            name: "AK_Folders_OrganizationId_Id",
            schema: "authorization",
            table: "Folders",
            columns: new[] { "OrganizationId", "Id" });

        migrationBuilder.CreateIndex(
            name: "IX_Folders_ParentCursor",
            schema: "authorization",
            table: "Folders",
            columns: new[] { "ParentId", "CreatedAt", "Id" });

        migrationBuilder.AddCheckConstraint(
            name: "CK_Folders_RootParent",
            schema: "authorization",
            table: "Folders",
            sql: "(\"IsRoot\" = TRUE AND \"ParentId\" IS NULL) OR (\"IsRoot\" = FALSE AND \"ParentId\" IS NOT NULL)");

        migrationBuilder.AddForeignKey(
            name: "FK_Folders_Folders_OrganizationId_ParentId",
            schema: "authorization",
            table: "Folders",
            columns: new[] { "OrganizationId", "ParentId" },
            principalSchema: "authorization",
            principalTable: "Folders",
            principalColumns: new[] { "OrganizationId", "Id" },
            onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_Folders_Folders_OrganizationId_ParentId",
            schema: "authorization",
            table: "Folders");
        migrationBuilder.DropCheckConstraint(
            name: "CK_Folders_RootParent",
            schema: "authorization",
            table: "Folders");
        migrationBuilder.DropIndex(
            name: "IX_Folders_ParentCursor",
            schema: "authorization",
            table: "Folders");
        migrationBuilder.DropUniqueConstraint(
            name: "AK_Folders_OrganizationId_Id",
            schema: "authorization",
            table: "Folders");
        migrationBuilder.DropColumn(
            name: "ParentId",
            schema: "authorization",
            table: "Folders");
    }
}
