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
