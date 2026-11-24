using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Authorization.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSH.Modules.Authorization.Data.Configurations;

public sealed class OrganizationProjectionConfiguration : IEntityTypeConfiguration<OrganizationProjection>
{
    public void Configure(EntityTypeBuilder<OrganizationProjection> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
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
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("Folders");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id)
            .HasConversion(id => id.Value, value => new FolderId(value));
        builder.Property(x => x.OrganizationId)
            .HasConversion(id => id.Value, value => new OrganizationId(value))
            .HasMaxLength(64)
            .IsRequired();
        builder.HasIndex(x => x.IsRoot)
            .IsUnique()
            .HasFilter("\"IsRoot\" = TRUE")
            .HasDatabaseName("UX_Folders_OneRootPerOrganization");
    }
}

public sealed class RelationshipSynchronizationConfiguration : IEntityTypeConfiguration<RelationshipSynchronization>
{
    public void Configure(EntityTypeBuilder<RelationshipSynchronization> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
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
