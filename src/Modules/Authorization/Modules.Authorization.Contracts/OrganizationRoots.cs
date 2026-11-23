namespace FSH.Modules.Authorization.Contracts;

public enum RelationshipSynchronizationStatus
{
    Pending = 1,
    Confirmed = 2,
}

public sealed record OrganizationRoot(
    FolderId Id,
    OrganizationId OrganizationId,
    string Name,
    DateTimeOffset CreatedAt,
    AuthorizationToken? AuthorizationToken);

public interface IOrganizationRootProvisioner
{
    ValueTask<OrganizationRoot> EnsureRootAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken = default);

    ValueTask<RelationshipConfirmation> SynchronizeAdministratorAsync(
        OrganizationId organizationId,
        UserId userId,
        bool isAdministrator,
        CancellationToken cancellationToken = default);
}
