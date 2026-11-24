using FSH.Framework.Core.Context;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Authorization.Contracts.v1.GetOrganizationRoot;
using FSH.Modules.Authorization.Contracts.v1.ListFolderChildren;
using FSH.Modules.Authorization.Data;
using FSH.Modules.Authorization.Domain;
using FSH.Modules.Identity.Contracts.Services;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Authorization.Services;

public sealed class AuthorizedFolderReader(
    AuthorizationDbContext db,
    ICurrentUser currentUser,
    IOrganizationMembershipReader membershipReader,
    IPermissionChecker permissionChecker)
{
    public async ValueTask<OrganizationRootResponse> GetRootAsync(
        string? requestedToken,
        CancellationToken cancellationToken)
    {
        OrganizationId organizationId = CurrentOrganization();
        Folder root = await db.Folders.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.IsRoot, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException("organization root not found");
        return await AuthorizeAsync(root, requestedToken, true, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<OrganizationRootResponse> GetAsync(
        FolderId folderId,
        string? requestedToken,
        CancellationToken cancellationToken)
    {
        OrganizationId organizationId = CurrentOrganization();
        Folder folder = await db.Folders.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == folderId && x.OrganizationId == organizationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException("folder not found");
        return await AuthorizeAsync(folder, requestedToken, true, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<FolderChildrenResponse> ListChildrenAsync(
        FolderId folderId,
        int pageSize,
        string? requestedCursor,
        string? requestedToken,
        CancellationToken cancellationToken)
    {
        OrganizationId organizationId = CurrentOrganization();
        Folder folder = await db.Folders.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == folderId && x.OrganizationId == organizationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException("folder not found");
        OrganizationRootResponse parent = await AuthorizeAsync(folder, requestedToken, false, cancellationToken).ConfigureAwait(false);
        FolderPageCursor? cursor = string.IsNullOrWhiteSpace(requestedCursor)
            ? null
            : FolderPageCursor.Decode(requestedCursor);
        var token = new AuthorizationToken(parent.AuthorizationToken);
        UserId userId = new(currentUser.GetUserId().ToString("D"));
        FolderPageResult result = await AuthorizedFolderPager.ReadAsync(
            folderId,
            userId,
            token,
            pageSize,
            cursor,
            LoadChildrenAsync,
            permissionChecker,
            cancellationToken).ConfigureAwait(false);
        return new FolderChildrenResponse(
            result.Items.Select(x => new FolderChild(x.Id.Value, folderId.Value, x.Name, x.CreatedAt)).ToArray(),
            result.NextCursor is null ? null : FolderPageCursor.Encode(result.NextCursor),
            result.AuthorizationToken.Value);
    }

    private async ValueTask<IReadOnlyList<Folder>> LoadChildrenAsync(
        FolderId parentId,
        FolderPageCursor? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        IQueryable<Folder> query = db.Folders.AsNoTracking()
            .Where(x => x.ParentId == parentId && !x.IsRoot
                && db.RelationshipSyncs.Any(sync => sync.OperationId == x.SynchronizationId
                    && sync.Status == RelationshipSynchronizationStatus.Confirmed));
        if (cursor is not null)
        {
            query = query.Where(x => x.CreatedAt > cursor.CreatedAt
                || x.CreatedAt == cursor.CreatedAt && x.Id.Value > cursor.Id.Value);
        }
        return await query
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id.Value)
            .Take(limit)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<OrganizationRootResponse> AuthorizeAsync(
        Folder folder,
        string? requestedToken,
        bool includeEdit,
        CancellationToken cancellationToken)
    {
        UserId userId = new(currentUser.GetUserId().ToString("D"));
        if (!await membershipReader.IsActiveMemberAsync(userId, cancellationToken).ConfigureAwait(false))
        {
            throw new NotFoundException("folder not found");
        }
        if (folder.SynchronizationId is not Guid synchronizationId)
        {
            throw new NotFoundException("folder not found");
        }
        var initialSync = await db.RelationshipSyncs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OperationId == synchronizationId, cancellationToken)
            .ConfigureAwait(false);
        if (initialSync?.Status != RelationshipSynchronizationStatus.Confirmed)
        {
            throw new NotFoundException("folder not found");
        }
        string? persistedToken = await db.RelationshipSyncs.AsNoTracking()
            .Where(x => x.OrganizationId == folder.OrganizationId && x.Status == RelationshipSynchronizationStatus.Confirmed)
            .OrderByDescending(x => x.ConfirmedAt)
            .Select(x => x.ZedToken)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        AuthorizationToken? token = !string.IsNullOrWhiteSpace(requestedToken)
            ? new AuthorizationToken(requestedToken)
            : persistedToken is not null ? new AuthorizationToken(persistedToken) : null;
        if (token is null) throw new NotFoundException("folder not found");

        PermissionCheckResult result = await permissionChecker.CheckAsync(new PermissionCheck(
            userId,
            AuthorizationObjects.For(folder.Id),
            AuthorizationPermission.View,
            AuthorizationConsistency.AtLeastAsFresh(token.Value)), cancellationToken).ConfigureAwait(false);
        if (!result.IsAllowed)
        {
            throw new NotFoundException("folder not found");
        }
        AuthorizationToken responseToken = result.CheckedAt ?? token.Value;
        bool canEdit = false;
        if (includeEdit)
        {
            PermissionCheckResult edit = await permissionChecker.CheckAsync(new PermissionCheck(
                userId,
                AuthorizationObjects.For(folder.Id),
                AuthorizationPermission.Edit,
                AuthorizationConsistency.AtLeastAsFresh(responseToken)), cancellationToken).ConfigureAwait(false);
            canEdit = edit.IsAllowed;
            responseToken = edit.CheckedAt ?? responseToken;
        }

        return new OrganizationRootResponse(
            folder.Id.Value,
            folder.OrganizationId.Value,
            folder.Name,
            folder.IsRoot,
            folder.CreatedAt,
            responseToken.Value,
            folder.ParentId?.Value,
            canEdit);
    }

    private OrganizationId CurrentOrganization()
    {
        string? tenant = currentUser.GetTenant();
        if (string.IsNullOrWhiteSpace(tenant)) throw new NotFoundException("organization root not found");
        return new OrganizationId(tenant);
    }
}
