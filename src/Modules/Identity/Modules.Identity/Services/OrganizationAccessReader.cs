using FSH.Framework.Shared.Constants;
using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.Identity.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Identity.Services;

internal sealed class OrganizationAccessReader(UserManager<FshUser> userManager)
    : IOrganizationAdminReader, IOrganizationMembershipReader
{
    public async Task<IReadOnlyList<UserId>> GetAdministratorIdsAsync(CancellationToken cancellationToken = default)
    {
        System.Collections.Generic.IList<FshUser> administrators = await userManager.GetUsersInRoleAsync(RoleConstants.Admin)
            .ConfigureAwait(false);
        return administrators
            .Where(user => user.IsActive)
            .Select(user => new UserId(user.Id))
            .OrderBy(id => id.Value, StringComparer.Ordinal)
            .ToArray();
    }

    public Task<bool> IsActiveMemberAsync(UserId userId, CancellationToken cancellationToken = default) =>
        userManager.Users.AsNoTracking()
            .AnyAsync(user => user.Id == userId.Value && user.IsActive, cancellationToken);
}
