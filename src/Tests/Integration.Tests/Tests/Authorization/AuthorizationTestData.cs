using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.Authorization;

internal static class AuthorizationTestData
{
    public static async Task UseRootTenantAsync(IServiceScope scope)
    {
        var store = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>();
        AppTenantInfo tenant = await store.GetAsync(TestConstants.RootTenantId)
            ?? throw new InvalidOperationException("Root organization is missing.");
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>()
            .MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);
    }
}
