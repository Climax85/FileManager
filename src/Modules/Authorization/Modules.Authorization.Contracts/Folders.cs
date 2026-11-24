namespace FSH.Modules.Authorization.Contracts;

public sealed record FolderDescriptor(
    FolderId Id,
    OrganizationId OrganizationId,
    FolderId? ParentId,
    string Name,
    bool IsRoot,
    DateTimeOffset CreatedAt);

public interface IFolderHierarchy
{
    ValueTask<FolderDescriptor?> FindAsync(
        FolderId folderId,
        OrganizationId organizationId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<FolderDescriptor>> GetPathAsync(
        FolderId folderId,
        OrganizationId organizationId,
        CancellationToken cancellationToken = default);

    ValueTask EnsureValidParentAsync(
        OrganizationId organizationId,
        FolderId parentId,
        FolderId? childId,
        CancellationToken cancellationToken = default);
}

public interface IFolderAuthorization
{
    ValueTask<AuthorizationToken> RequireAsync(
        FolderId folderId,
        AuthorizationPermission permission,
        AuthorizationToken? requestedToken = null,
        CancellationToken cancellationToken = default);
}
