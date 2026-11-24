using FSH.Modules.Authorization.Contracts;

namespace FSH.Modules.Identity.Contracts.Services;

public interface IOrganizationAdminReader
{
    Task<IReadOnlyList<UserId>> GetAdministratorIdsAsync(CancellationToken cancellationToken = default);
}

public interface IOrganizationMembershipReader
{
    Task<bool> IsActiveMemberAsync(UserId userId, CancellationToken cancellationToken = default);
}

public interface IOrganizationGroupReader
{
    Task<bool> GroupExistsAsync(GroupId groupId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GroupId>> GetGroupIdsAsync(UserId userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UserId>> GetMemberIdsAsync(GroupId groupId, CancellationToken cancellationToken = default);
}
