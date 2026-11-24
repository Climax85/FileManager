using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Authorization.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSH.Modules.Authorization.Data.Configurations;

public sealed class OrganizationProjectionConfiguration : IEntityTypeConfiguration<OrganizationProjection>
{
    public void Configure(EntityTypeBuilder<OrganizationProjection> builder)
    {
        builder.ToTable("Organizations");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id)
            .HasConversion(id => id.Value, value => new OrganizationId(value))
            .HasMaxLength(64);
        builder.Property(x => x.CreatedAt).IsRequired();
    }
}

public sealed class FolderConfiguration : IEntityTypeConfiguration<Folder>
{
    public void Configure(EntityTypeBuilder<Folder> builder)
    {
        builder.ToTable("Folders", table => table.HasCheckConstraint(
            "CK_Folders_RootParent",
            "(\"IsRoot\" = TRUE AND \"ParentId\" IS NULL) OR (\"IsRoot\" = FALSE AND \"ParentId\" IS NOT NULL)"));
        builder.HasKey(x => x.Id);
        builder.HasAlternateKey(x => new { x.OrganizationId, x.Id });
        builder.Property(x => x.Id)
            .HasConversion(id => id.Value, value => new FolderId(value));
        builder.Property(x => x.ParentId)
            .HasConversion(
                id => id.HasValue ? id.Value.Value : (Guid?)null,
                value => value.HasValue ? new FolderId(value.Value) : (FolderId?)null);
        builder.Property(x => x.OrganizationId)
            .HasConversion(id => id.Value, value => new OrganizationId(value))
            .HasMaxLength(64)
            .IsRequired();
        builder.Property(x => x.Name).HasMaxLength(256).IsRequired();
        builder.HasOne<Folder>()
            .WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.ParentId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.ParentId, x.CreatedAt, x.Id })
            .HasDatabaseName("IX_Folders_ParentCursor");
        builder.HasIndex(x => x.IsRoot)
            .IsUnique()
            .HasFilter("\"IsRoot\" = TRUE")
            .HasDatabaseName("UX_Folders_OneRootPerOrganization");
    }

public sealed class RelationshipSynchronizationConfiguration : IEntityTypeConfiguration<RelationshipSynchronization>
{
    public void Configure(EntityTypeBuilder<RelationshipSynchronization> builder)
    {
        builder.ToTable("RelationshipSynchronizations");
        builder.HasKey(x => x.OperationId);
        builder.Property(x => x.OrganizationId)
            .HasConversion(id => id.Value, value => new OrganizationId(value))
            .HasMaxLength(64)
            .IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.ZedToken).HasMaxLength(1024);
        builder.Property(x => x.LastError).HasMaxLength(2048);
    }
}
