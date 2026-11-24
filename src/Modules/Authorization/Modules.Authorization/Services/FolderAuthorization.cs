using FSH.Framework.Core.Context;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Authorization.Data;
using FSH.Modules.Authorization.Domain;
using FSH.Modules.Identity.Contracts.Services;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Authorization.Services;

public sealed class FolderAuthorization(
    AuthorizationDbContext db,
    ICurrentUser currentUser,
    IOrganizationMembershipReader membershipReader,
    IPermissionChecker permissionChecker) : IFolderAuthorization
{
    public async ValueTask<AuthorizationToken> RequireAsync(
        FolderId folderId,
        AuthorizationPermission permission,
        AuthorizationToken? requestedToken = null,
        CancellationToken cancellationToken = default)
    {
        string? tenant = currentUser.GetTenant();
        if (string.IsNullOrWhiteSpace(tenant)) throw new NotFoundException("folder not found");
        OrganizationId organizationId = new(tenant);
        UserId userId = new(currentUser.GetUserId().ToString("D"));
        if (!await membershipReader.IsActiveMemberAsync(userId, cancellationToken).ConfigureAwait(false))
        {
            throw new NotFoundException("folder not found");
        }

        Folder folder = await db.Folders.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == folderId && x.OrganizationId == organizationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException("folder not found");
        if (folder.SynchronizationId is not Guid synchronizationId)
        {
            throw new NotFoundException("folder not found");
        }
        RelationshipSynchronization? initialSync = await db.RelationshipSyncs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OperationId == synchronizationId, cancellationToken)
            .ConfigureAwait(false);
        if (initialSync?.Status != RelationshipSynchronizationStatus.Confirmed)
        {
            throw new NotFoundException("folder not found");
        }

        string? persistedToken = await db.RelationshipSyncs.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Status == RelationshipSynchronizationStatus.Confirmed)
            .OrderByDescending(x => x.ConfirmedAt)
            .Select(x => x.ZedToken)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        AuthorizationToken? token = requestedToken
            ?? (persistedToken is null ? null : new AuthorizationToken(persistedToken));
        if (token is null) throw new NotFoundException("folder not found");

        PermissionCheckResult result = await permissionChecker.CheckAsync(new PermissionCheck(
            userId,
            AuthorizationObjects.For(folderId),
            permission,
            AuthorizationConsistency.AtLeastAsFresh(token.Value)), cancellationToken).ConfigureAwait(false);
        if (!result.IsAllowed) throw new NotFoundException("folder not found");
        return result.CheckedAt ?? token.Value;
    }
}
