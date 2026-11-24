using FSH.Framework.Shared.Constants;
using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.Identity.Data;
using FSH.Modules.Identity.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Identity.Services;

internal sealed class OrganizationAccessReader(UserManager<FshUser> userManager, IdentityDbContext db)
    : IOrganizationAdminReader, IOrganizationMembershipReader, IOrganizationGroupReader
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

    public Task<bool> GroupExistsAsync(GroupId groupId, CancellationToken cancellationToken = default) =>
        db.Groups.AsNoTracking()
            .AnyAsync(group => group.Id == groupId.Value && !group.IsDeleted, cancellationToken);

    public async Task<IReadOnlyList<GroupId>> GetGroupIdsAsync(
        UserId userId,
        CancellationToken cancellationToken = default)
    {
        List<Guid> ids = await db.UserGroups.AsNoTracking()
            .Where(membership => membership.UserId == userId.Value &&
                membership.Group != null && !membership.Group.IsDeleted)
            .Select(membership => membership.GroupId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return ids.Select(id => new GroupId(id)).ToArray();
    }

    public async Task<IReadOnlyList<UserId>> GetMemberIdsAsync(
        GroupId groupId,
        CancellationToken cancellationToken = default)
    {
        List<string> ids = await db.UserGroups.AsNoTracking()
            .Where(membership => membership.GroupId == groupId.Value &&
                membership.User != null && membership.User.IsActive)
            .Select(membership => membership.UserId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return ids.Select(id => new UserId(id)).ToArray();
    }
}
