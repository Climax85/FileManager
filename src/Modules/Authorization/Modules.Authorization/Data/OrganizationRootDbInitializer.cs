using FSH.Framework.Persistence;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Authorization.Contracts;
using Finbuckle.MultiTenant.Abstractions;

namespace FSH.Modules.Authorization.Data;

public sealed class OrganizationRootDbInitializer(
    IOrganizationRootProvisioner provisioner,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor) : IDbInitializer
{
    public Task MigrateAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        string? organizationId = tenantAccessor.MultiTenantContext.TenantInfo?.Id;
        if (!string.IsNullOrWhiteSpace(organizationId))
        {
            await provisioner.EnsureRootAsync(new OrganizationId(organizationId), cancellationToken).ConfigureAwait(false);
        }
    }
}
