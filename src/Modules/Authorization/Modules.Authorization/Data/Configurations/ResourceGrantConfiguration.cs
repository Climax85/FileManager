using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Authorization.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSH.Modules.Authorization.Data.Configurations;

public sealed class ResourceGrantConfiguration : IEntityTypeConfiguration<ResourceGrant>
{
    public void Configure(EntityTypeBuilder<ResourceGrant> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("ResourceGrants");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.OrganizationId)
            .HasConversion(id => id.Value, value => new OrganizationId(value))
            .HasMaxLength(64)
            .IsRequired();
        builder.Property(x => x.ResourceType).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.SubjectType).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.SubjectId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.SubjectRelation).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.Relation).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.ZedToken).HasMaxLength(1024);
        builder.Property(x => x.LastError).HasMaxLength(2048);
        builder.HasIndex(x => x.OperationId).IsUnique();
        builder.HasIndex(x => new
        {
            x.ResourceType,
            x.ResourceId,
            x.SubjectType,
            x.SubjectId,
            x.SubjectRelation,
            x.Relation,
        }).IsUnique().HasFilter("\"Status\" <> 'Revoked'").HasDatabaseName("UX_ResourceGrants_ActiveSubjectRelation");
        builder.HasIndex(x => new { x.ResourceType, x.ResourceId, x.Status });
    }
}
