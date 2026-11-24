using System.Net;
using FSH.Framework.Core.Context;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Auditing.Contracts;
using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Authorization.Contracts.v1.MoveResource;
using FSH.Modules.Authorization.Data;
using FSH.Modules.Authorization.Domain;
using FSH.Modules.Identity.Contracts.Services;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Authorization.Services;

public sealed class ResourceMoveAuthorizer(
    AuthorizationDbContext db,
    ICurrentUser currentUser,
    IOrganizationMembershipReader membershipReader,
    IPermissionChecker permissionChecker,
    IFolderAuthorization folderAuthorization,
    IAuditClient auditClient) : IResourceMoveAuthorization
{
    public async ValueTask<ResourceMoveAuthorization> RequireAsync(
        AuthorizationObject resource,
        FolderId destinationFolderId,
        string? requestedToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        string? tenant = currentUser.GetTenant();
        if (string.IsNullOrWhiteSpace(tenant))
        {
            await AuditDeniedAsync(resource, destinationFolderId, "OrganizationRequired", cancellationToken).ConfigureAwait(false);
            throw new NotFoundException("resource not found");
        }

        var organizationId = new OrganizationId(tenant);
        var actorId = new UserId(currentUser.GetUserId().ToString("D"));
        if (!await membershipReader.IsActiveMemberAsync(actorId, cancellationToken).ConfigureAwait(false))
        {
            await AuditDeniedAsync(resource, destinationFolderId, "ActiveMembershipRequired", cancellationToken).ConfigureAwait(false);
            throw new NotFoundException("resource not found");
        }

        AuthorizationToken? token = string.IsNullOrWhiteSpace(requestedToken)
            ? await LatestTokenAsync(organizationId, cancellationToken).ConfigureAwait(false)
            : new AuthorizationToken(requestedToken);
        PermissionCheckResult manager = await permissionChecker.CheckAsync(new PermissionCheck(
            actorId,
            resource,
            AuthorizationPermission.Manage,
            token is null ? AuthorizationConsistency.MinimizeLatency() : AuthorizationConsistency.AtLeastAsFresh(token.Value)),
            cancellationToken).ConfigureAwait(false);
        if (!manager.IsAllowed)
        {
            await AuditDeniedAsync(
                resource,
                destinationFolderId,
                manager.Failure == AuthorizationFailure.Unavailable ? "AuthorizationUnavailable" : "SourceManagerRequired",
                cancellationToken).ConfigureAwait(false);
            if (manager.Failure == AuthorizationFailure.Unavailable)
            {
                throw Unavailable();
            }
            throw new ForbiddenException("manager access on the source is required");
        }

        try
        {
            AuthorizationToken destinationToken = await folderAuthorization.RequireAsync(
                destinationFolderId,
                AuthorizationPermission.Edit,
                manager.CheckedAt ?? token,
                cancellationToken).ConfigureAwait(false);
            return new ResourceMoveAuthorization(organizationId, actorId, destinationToken);
        }
        catch (NotFoundException)
        {
            await AuditDeniedAsync(resource, destinationFolderId, "DestinationEditorRequired", cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask AuditDeniedAsync(
        AuthorizationObject resource,
        FolderId destinationFolderId,
        string reasonCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        return AuditAsync(SecurityAction.PermissionDenied, resource, destinationFolderId, null, reasonCode, cancellationToken);
    }

    public ValueTask AuditSucceededAsync(
        AuthorizationObject resource,
        FolderId destinationFolderId,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return AuditAsync(SecurityAction.ResourceMoved, resource, destinationFolderId, operationId, null, cancellationToken);
    }

    public ValueTask AuditSynchronizationFailedAsync(
        AuthorizationObject resource,
        FolderId destinationFolderId,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return AuditAsync(SecurityAction.PolicyFailed, resource, destinationFolderId, operationId,
            "AuthorizationSynchronizationUnavailable", cancellationToken);
    }

    private async ValueTask<AuthorizationToken?> LatestTokenAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken)
    {
        string? token = await db.RelationshipSyncs.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Status == RelationshipSynchronizationStatus.Confirmed)
            .OrderByDescending(x => x.ConfirmedAt)
            .Select(x => x.ZedToken)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(token) ? null : new AuthorizationToken(token);
    }

    private ValueTask AuditAsync(
        SecurityAction action,
        AuthorizationObject resource,
        FolderId destinationFolderId,
        Guid? operationId,
        string? reasonCode,
        CancellationToken cancellationToken) =>
        auditClient.WriteSecurityAsync(
            action,
            currentUser.GetUserId().ToString("D"),
            authMethod: "SpiceDB",
            reasonCode: reasonCode,
            claims: new Dictionary<string, object?>
            {
                ["resourceType"] = resource.Type.ToString(),
                ["resourceId"] = resource.ObjectId,
                ["destinationFolderId"] = destinationFolderId.Value,
                ["operationId"] = operationId,
            },
            source: "Authorization",
            ct: cancellationToken);

    private static CustomException Unavailable() =>
        new("Authorization is temporarily unavailable.", [], HttpStatusCode.ServiceUnavailable);
}
