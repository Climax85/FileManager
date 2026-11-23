using FSH.Framework.Persistence;
using FSH.Modules.Authorization.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Authorization.Data;

public sealed class AuthorizationDbInitializer(
    AuthorizationDbContext dbContext,
    IAuthorizationSchemaManager schemaManager) : IDbInitializer
{
    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await schemaManager.EnsureCurrentAsync(cancellationToken).ConfigureAwait(false);
        if ((await dbContext.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).Any())
        {
            await dbContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task SeedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
