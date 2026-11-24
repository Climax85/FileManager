using FSH.Framework.Core.Context;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Authorization.Contracts.v1.GetOrganizationRoot;
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
        return await AuthorizeAsync(root, requestedToken, cancellationToken).ConfigureAwait(false);
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
        return await AuthorizeAsync(folder, requestedToken, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<OrganizationRootResponse> AuthorizeAsync(
        Folder folder,
        string? requestedToken,
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
        AuthorizationToken? token = null;
        if (!string.IsNullOrWhiteSpace(requestedToken))
        {
            token = new AuthorizationToken(requestedToken);
        }
        else if (persistedToken is not null)
        {
            token = new AuthorizationToken(persistedToken);
        }
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

        return new OrganizationRootResponse(
            folder.Id.Value,
            folder.OrganizationId.Value,
            folder.Name,
            folder.IsRoot,
            folder.CreatedAt,
            token.Value.Value);
    }

    private OrganizationId CurrentOrganization()
    {
        string? tenant = currentUser.GetTenant();
        if (string.IsNullOrWhiteSpace(tenant)) throw new NotFoundException("organization root not found");
        return new OrganizationId(tenant);
    }
}
