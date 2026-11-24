using System.Net;
using FSH.Framework.Core.Context;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Eventing.Outbox;
using FSH.Modules.Auditing.Contracts;
using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Authorization.Contracts.Events;
using FSH.Modules.Authorization.Contracts.v1.Sharing;
using FSH.Modules.Authorization.Data;
using FSH.Modules.Authorization.Domain;
using FSH.Modules.Identity.Contracts.Services;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Authorization.Services;

public sealed class ResourceSharingService(
    AuthorizationDbContext db,
    ICurrentUser currentUser,
    IOrganizationMembershipReader membershipReader,
    IOrganizationGroupReader groupReader,
    IPermissionChecker permissionChecker,
    IRelationshipWriter relationshipWriter,
    ResourceLineageReader lineageReader,
    EfCoreOutboxStore<AuthorizationDbContext> outbox,
    IAuditClient auditClient,
    TimeProvider timeProvider)
{
    public async ValueTask<ResourceGrantResponse> GrantAsync(
        CreateResourceGrantCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        AccessContext context = await RequireActiveActorAsync(cancellationToken).ConfigureAwait(false);
        AuthorizationObject resource = Resource(command.ResourceType, command.ResourceId);
        AuthorizationSubject subject = await SubjectAsync(context.OrganizationId, command, cancellationToken).ConfigureAwait(false);
        PermissionCheckResult manager = await RequireManagerAsync(context, resource, command.AuthorizationToken, cancellationToken).ConfigureAwait(false);

        ResourceGrant? existing = await db.ResourceGrants.SingleOrDefaultAsync(x =>
            x.ResourceType == resource.Type && x.ResourceId == command.ResourceId &&
            x.SubjectType == subject.Object.Type && x.SubjectId == subject.Object.ObjectId &&
            x.SubjectRelation == subject.OptionalRelation && x.Relation == command.Relation &&
            x.Status != ResourceGrantStatus.Revoked, cancellationToken).ConfigureAwait(false);
        if (existing?.Status == ResourceGrantStatus.Active)
        {
            return Response(existing, existing.ZedToken ?? Token(manager, command.AuthorizationToken));
        }
        if (existing?.Status == ResourceGrantStatus.RevocationPending)
        {
            throw new CustomException("This grant is being revoked; retry after revocation is confirmed.", [], HttpStatusCode.Conflict);
        }
        if (existing is not null)
        {
            return await ConfirmGrantAsync(existing, cancellationToken).ConfigureAwait(false);
        }
        if (subject.Object.Type == AuthorizationObjectType.Group)
        {
            await ReconcileGroupAsync(context.OrganizationId, subject.Object, cancellationToken).ConfigureAwait(false);
        }


        Guid operationId = Guid.CreateVersion7();
        DateTimeOffset now = timeProvider.GetUtcNow();
        var grant = ResourceGrant.CreatePending(
            Guid.CreateVersion7(), operationId, context.OrganizationId, resource, subject, command.Relation, now);
        var sync = RelationshipSynchronization.Pending(operationId, context.OrganizationId);
        RelationshipMutation mutation = grant.GrantMutation();
        await PersistMutationAsync(grant, sync, mutation, true, cancellationToken).ConfigureAwait(false);
        return await ConfirmGrantAsync(grant, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ResourceGrantResponse> RevokeAsync(
        RevokeResourceGrantCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        AccessContext context = await RequireActiveActorAsync(cancellationToken).ConfigureAwait(false);
        ResourceGrant grant = await db.ResourceGrants.SingleOrDefaultAsync(x => x.Id == command.GrantId, cancellationToken)
            .ConfigureAwait(false) ?? throw new NotFoundException("grant not found");
        if (grant.OrganizationId != context.OrganizationId) throw new NotFoundException("grant not found");
        PermissionCheckResult manager = await RequireManagerAsync(
            context, grant.Resource(), command.AuthorizationToken, cancellationToken).ConfigureAwait(false);
        if (grant.Status == ResourceGrantStatus.Revoked)
        {
            return Response(grant, grant.ZedToken ?? Token(manager, command.AuthorizationToken));
        }
        if (grant.Status == ResourceGrantStatus.RevocationPending)
        {
            return await ConfirmRevocationAsync(grant, cancellationToken).ConfigureAwait(false);
        }

        Guid operationId = Guid.CreateVersion7();
        grant.BeginRevocation(operationId, timeProvider.GetUtcNow());
        var sync = RelationshipSynchronization.Pending(operationId, context.OrganizationId);
        RelationshipMutation mutation = grant.RevocationMutation();
        await PersistMutationAsync(grant, sync, mutation, false, cancellationToken).ConfigureAwait(false);
        return await ConfirmRevocationAsync(grant, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ResourceGrantListResponse> ListAsync(
        GetResourceGrantsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        AccessContext context = await RequireActiveActorAsync(cancellationToken).ConfigureAwait(false);
        AuthorizationObject resource = Resource(query.ResourceType, query.ResourceId);
        PermissionCheckResult manager = await RequireManagerAsync(context, resource, query.AuthorizationToken, cancellationToken).ConfigureAwait(false);
        List<ResourceGrant> grants = await db.ResourceGrants.AsNoTracking()
            .Where(x => x.ResourceType == query.ResourceType && x.ResourceId == query.ResourceId)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        string token = Token(manager, query.AuthorizationToken);
        return new ResourceGrantListResponse([.. grants.Select(x => Response(x, token))], token);
    }

    public async ValueTask<EffectiveAccessResponse> EffectiveAccessAsync(
        GetEffectiveAccessQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        AccessContext context = await RequireActiveActorAsync(cancellationToken).ConfigureAwait(false);
        UserId subjectId = string.IsNullOrWhiteSpace(query.SubjectId) ? context.UserId : new UserId(query.SubjectId);
        if (!await membershipReader.IsActiveMemberAsync(subjectId, cancellationToken).ConfigureAwait(false))
        {
            throw new NotFoundException("organization member not found");
        }

        AuthorizationObject resource = Resource(query.ResourceType, query.ResourceId);
        PermissionCheckResult? manager = null;
        if (subjectId != context.UserId)
        {
            manager = await RequireManagerAsync(context, resource, query.AuthorizationToken, cancellationToken).ConfigureAwait(false);
        }

        AuthorizationToken? requestedToken = TokenOrNull(query.AuthorizationToken)
            ?? await LatestTokenAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false);
        AuthorizationConsistency? consistency = requestedToken is null
            ? AuthorizationConsistency.MinimizeLatency()
            : AuthorizationConsistency.AtLeastAsFresh(requestedToken.Value);
        PermissionCheckResult manage = await CheckAsync(subjectId, resource, AuthorizationPermission.Manage, consistency, cancellationToken).ConfigureAwait(false);
        PermissionCheckResult edit = manage.IsAllowed
            ? manage
            : await CheckAsync(subjectId, resource, AuthorizationPermission.Edit, consistency, cancellationToken).ConfigureAwait(false);
        PermissionCheckResult view = edit.IsAllowed
            ? edit
            : await CheckAsync(subjectId, resource, AuthorizationPermission.View, consistency, cancellationToken).ConfigureAwait(false);
        EffectiveAccessLevel access = (manage.IsAllowed, edit.IsAllowed, view.IsAllowed) switch
        {
            (true, _, _) => EffectiveAccessLevel.Manager,
            (_, true, _) => EffectiveAccessLevel.Editor,
            (_, _, true) => EffectiveAccessLevel.Viewer,
            _ => EffectiveAccessLevel.None,
        };
        if (access == EffectiveAccessLevel.None && view.Failure == AuthorizationFailure.Unavailable)
        {
            throw Unavailable("Effective access is temporarily unavailable.");
        }

        IReadOnlyList<AuthorizationObject> lineage = await lineageReader.ReadAsync(resource, context.OrganizationId, requestedToken, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<GroupId> groupIds = access == EffectiveAccessLevel.None
            ? []
            : await groupReader.GetGroupIdsAsync(subjectId, cancellationToken).ConfigureAwait(false);
        string[] groupSubjectIds = [.. groupIds.Select(x => x.ToString())];
        Guid[] resourceIds = [.. lineage.Select(x => Guid.Parse(x.ObjectId))];
        List<ResourceGrant> grants = access == EffectiveAccessLevel.None ? [] : await db.ResourceGrants.AsNoTracking()
            .Where(x => resourceIds.Contains(x.ResourceId) && x.Status == ResourceGrantStatus.Active &&
                ((x.SubjectType == AuthorizationObjectType.User && x.SubjectId == subjectId.Value) ||
                 (x.SubjectType == AuthorizationObjectType.Group && groupSubjectIds.Contains(x.SubjectId)) ||
                 (x.SubjectType == AuthorizationObjectType.Organization && x.SubjectId == context.OrganizationId.Value)))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        EffectiveAccessSourceResponse[] sources = [.. grants.Select(grant =>
        {
            bool sameResource = grant.ResourceType == resource.Type && grant.ResourceId == query.ResourceId;
            EffectiveAccessSourceKind source;
            if (grant.SubjectType == AuthorizationObjectType.Organization)
            {
                source = EffectiveAccessSourceKind.Organization;
            }
            else if (grant.SubjectType == AuthorizationObjectType.Group)
            {
                source = EffectiveAccessSourceKind.Group;
            }
            else
            {
                source = sameResource ? EffectiveAccessSourceKind.Direct : EffectiveAccessSourceKind.Inherited;
            }
            return new EffectiveAccessSourceResponse(
                grant.Id, source, grant.ResourceType, grant.ResourceId, grant.Relation, grant.SubjectType, grant.SubjectId);
        })];

        string token = requestedToken?.Value ?? manage.CheckedAt?.Value ?? edit.CheckedAt?.Value ?? view.CheckedAt?.Value
            ?? manager?.CheckedAt?.Value ?? string.Empty;
        return new EffectiveAccessResponse(resource.Type, query.ResourceId, subjectId.Value, access, sources, token);
    }

    private async ValueTask<AccessContext> RequireActiveActorAsync(CancellationToken cancellationToken)
    {
        string? tenant = currentUser.GetTenant();
        if (string.IsNullOrWhiteSpace(tenant)) throw new NotFoundException("resource not found");
        var organizationId = new OrganizationId(tenant);
        var userId = new UserId(currentUser.GetUserId().ToString("D"));
        if (!await membershipReader.IsActiveMemberAsync(userId, cancellationToken).ConfigureAwait(false))
        {
            throw new NotFoundException("resource not found");
        }
        return new AccessContext(organizationId, userId);
    }

    private async ValueTask<AuthorizationSubject> SubjectAsync(
        OrganizationId organizationId,
        CreateResourceGrantCommand command,
        CancellationToken cancellationToken)
    {
        if (command.SubjectType == AuthorizationObjectType.User)
        {
            var userId = new UserId(command.SubjectId);
            if (!await membershipReader.IsActiveMemberAsync(userId, cancellationToken).ConfigureAwait(false))
            {
                throw new NotFoundException("organization member not found");
            }
            return AuthorizationSubjects.For(userId);
        }
        if (command.SubjectType == AuthorizationObjectType.Group)
        {
            if (!Guid.TryParse(command.SubjectId, out Guid parsedGroupId) || parsedGroupId == Guid.Empty)
            {
                throw new CustomException("A valid group is required.", [], HttpStatusCode.BadRequest);
            }

            var groupId = new GroupId(parsedGroupId);
            if (!await groupReader.GroupExistsAsync(groupId, cancellationToken).ConfigureAwait(false))
            {
                throw new NotFoundException("group not found");
            }

            return AuthorizationSubjects.Members(AuthorizationObjects.For(groupId));
        }

        if (command.SubjectType != AuthorizationObjectType.Organization ||
            !string.Equals(command.SubjectId.Trim(), organizationId.Value, StringComparison.Ordinal))
        {
            throw new CustomException("Sharing subjects must be an active member or the current organization.", [], HttpStatusCode.BadRequest);
        }
        if (!command.ConfirmOrganizationWide)
        {
            throw new CustomException("Organization-wide sharing requires explicit confirmation.", [], HttpStatusCode.BadRequest);
        }
        if (command.Relation == AuthorizationRelation.Manager)
        {
            throw new CustomException("Organization-wide grants support viewer or editor access, not manager access.", [], HttpStatusCode.BadRequest);
        }
        return AuthorizationSubjects.Members(AuthorizationObjects.For(organizationId));
    }

    private async ValueTask ReconcileGroupAsync(
        OrganizationId organizationId,
        AuthorizationObject group,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<UserId> members = await groupReader.GetMemberIdsAsync(
            new GroupId(Guid.Parse(group.ObjectId)), cancellationToken).ConfigureAwait(false);
        List<RelationshipMutation> mutations =
        [
            RelationshipMutation.Touch(group, AuthorizationRelation.Organization,
                new AuthorizationSubject(AuthorizationObjects.For(organizationId))),
        ];
        foreach (UserId member in members)
        {
            mutations.Add(RelationshipMutation.Touch(
                group, AuthorizationRelation.Member, AuthorizationSubjects.For(member)));
        }

        try
        {
            _ = await relationshipWriter.WriteAsync(Guid.CreateVersion7(), mutations, cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException exception)
        {
            throw Unavailable("Group membership could not be synchronized; the grant was not created.", exception);
        }
    }

    private async ValueTask<PermissionCheckResult> RequireManagerAsync(
        AccessContext context,
        AuthorizationObject resource,
        string? requestedToken,
        CancellationToken cancellationToken)
    {
        AuthorizationToken? token = TokenOrNull(requestedToken)
            ?? await LatestTokenAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false);
        PermissionCheckResult result = await CheckAsync(
            context.UserId, resource, AuthorizationPermission.Manage,
            token is null ? AuthorizationConsistency.MinimizeLatency() : AuthorizationConsistency.AtLeastAsFresh(token.Value),
            cancellationToken).ConfigureAwait(false);
        if (result.IsAllowed) return result;
        await AuditAsync(
            result.Failure == AuthorizationFailure.Unavailable ? SecurityAction.PolicyFailed : SecurityAction.PermissionDenied,
            context.UserId.Value,
            result.Failure == AuthorizationFailure.Unavailable ? "AuthorizationUnavailable" : "ManageRequired",
            resource,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.Failure == AuthorizationFailure.Unavailable) throw Unavailable("Authorization is temporarily unavailable.");
        throw new ForbiddenException("manager access is required");
    }

    private ValueTask<PermissionCheckResult> CheckAsync(
        UserId subject,
        AuthorizationObject resource,
        AuthorizationPermission permission,
        AuthorizationConsistency consistency,
        CancellationToken cancellationToken) =>
        permissionChecker.CheckAsync(new PermissionCheck(subject, resource, permission, consistency), cancellationToken);


    private async ValueTask PersistMutationAsync(
        ResourceGrant grant,
        RelationshipSynchronization sync,
        RelationshipMutation mutation,
        bool isNew,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (isNew) db.ResourceGrants.Add(grant);
        db.RelationshipSyncs.Add(sync);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await outbox.AddAsync(Event(sync.OperationId, grant.OrganizationId, mutation), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ResourceGrantResponse> ConfirmGrantAsync(ResourceGrant grant, CancellationToken cancellationToken)
    {
        RelationshipSynchronization sync = await db.RelationshipSyncs.SingleAsync(
            x => x.OperationId == grant.OperationId, cancellationToken).ConfigureAwait(false);
        try
        {
            RelationshipConfirmation confirmation = await relationshipWriter.WriteAsync(
                grant.OperationId, [grant.GrantMutation()], cancellationToken).ConfigureAwait(false);
            sync.MarkConfirmed(confirmation.WrittenAt.Value);
            grant.MarkConfirmed(confirmation.WrittenAt.Value, confirmation.ConfirmedAt);
            await MarkOutboxProcessedAsync(sync.OperationId, confirmation.ConfirmedAt, cancellationToken).ConfigureAwait(false);
            await AuditAsync(SecurityAction.ResourceGranted, grant.SubjectId, null, grant.Resource(), grant, cancellationToken).ConfigureAwait(false);
            return Response(grant, confirmation.WrittenAt.Value);
        }
        catch (RpcException exception)
        {
            await MarkFailedAsync(grant, sync, exception, cancellationToken).ConfigureAwait(false);
            throw Unavailable("The grant remains pending and ineffective until authorization synchronization is confirmed.", exception);
        }
    }

    private async ValueTask<ResourceGrantResponse> ConfirmRevocationAsync(ResourceGrant grant, CancellationToken cancellationToken)
    {
        RelationshipSynchronization sync = await db.RelationshipSyncs.SingleAsync(
            x => x.OperationId == grant.OperationId, cancellationToken).ConfigureAwait(false);
        try
        {
            RelationshipConfirmation confirmation = await relationshipWriter.WriteAsync(
                grant.OperationId, [grant.RevocationMutation()], cancellationToken).ConfigureAwait(false);
            sync.MarkConfirmed(confirmation.WrittenAt.Value);
            grant.MarkRevoked(confirmation.WrittenAt.Value, confirmation.ConfirmedAt);
            await MarkOutboxProcessedAsync(sync.OperationId, confirmation.ConfirmedAt, cancellationToken).ConfigureAwait(false);
            await AuditAsync(SecurityAction.ResourceRevoked, grant.SubjectId, null, grant.Resource(), grant, cancellationToken).ConfigureAwait(false);
            return Response(grant, confirmation.WrittenAt.Value);
        }
        catch (RpcException exception)
        {
            await MarkFailedAsync(grant, sync, exception, cancellationToken).ConfigureAwait(false);
            throw Unavailable("Revocation remains pending and access stays denied until synchronization is confirmed.", exception);
        }
    }

    private async ValueTask MarkFailedAsync(
        ResourceGrant grant,
        RelationshipSynchronization sync,
        RpcException exception,
        CancellationToken cancellationToken)
    {
        sync.MarkFailed(exception.Status.Detail);
        grant.MarkSynchronizationFailed(exception.Status.Detail);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await AuditAsync(SecurityAction.PolicyFailed, grant.SubjectId, "AuthorizationSynchronizationUnavailable",
            grant.Resource(), grant, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask MarkOutboxProcessedAsync(Guid operationId, DateTimeOffset confirmedAt, CancellationToken cancellationToken)
    {
        OutboxMessage? message = await db.OutboxMessages.SingleOrDefaultAsync(x => x.Id == operationId, cancellationToken).ConfigureAwait(false);
        if (message is not null) message.ProcessedOnUtc = confirmedAt.UtcDateTime;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<AuthorizationToken?> LatestTokenAsync(OrganizationId organizationId, CancellationToken cancellationToken)
    {
        string? token = await db.RelationshipSyncs.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Status == RelationshipSynchronizationStatus.Confirmed)
            .OrderByDescending(x => x.ConfirmedAt).Select(x => x.ZedToken)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(token) ? null : new AuthorizationToken(token);
    }

    private AuthorizationRelationshipsChangedIntegrationEvent Event(
        Guid operationId,
        OrganizationId organizationId,
        RelationshipMutation mutation) =>
        new(operationId, timeProvider.GetUtcNow().UtcDateTime, organizationId.Value,
            operationId.ToString("D"), "Authorization", [mutation]);

    private ValueTask AuditAsync(
        SecurityAction action,
        string subjectId,
        string? reasonCode,
        AuthorizationObject resource,
        ResourceGrant? grant = null,
        CancellationToken cancellationToken = default) =>
        auditClient.WriteSecurityAsync(action, subjectId, authMethod: "SpiceDB", reasonCode: reasonCode,
            claims: new Dictionary<string, object?>
            {
                ["resourceType"] = resource.Type.ToString(),
                ["resourceId"] = resource.ObjectId,
                ["grantId"] = grant?.Id,
                ["operationId"] = grant?.OperationId,
                ["relation"] = grant?.Relation.ToString(),
            }, source: "Authorization", ct: cancellationToken);

    private static AuthorizationObject Resource(AuthorizationObjectType type, Guid id)
    {
        if (id == Guid.Empty || type is not (AuthorizationObjectType.File or AuthorizationObjectType.Folder))
        {
            throw new CustomException("A valid file or folder resource is required.", [], HttpStatusCode.BadRequest);
        }
        return new AuthorizationObject(type, id.ToString("D"));
    }

    private static ResourceGrantResponse Response(ResourceGrant grant, string token) =>
        new(grant.Id, grant.ResourceType, grant.ResourceId, grant.SubjectType, grant.SubjectId,
            grant.Relation, grant.Status, token, grant.CreatedAt, grant.ConfirmedAt);

    private static AuthorizationToken? TokenOrNull(string? token) =>
        string.IsNullOrWhiteSpace(token) ? null : new AuthorizationToken(token);

    private static string Token(PermissionCheckResult result, string? requested) =>
        requested ?? result.CheckedAt?.Value ?? string.Empty;

    private static CustomException Unavailable(string message, Exception? inner = null) =>
        inner is null ? new CustomException(message, [], HttpStatusCode.ServiceUnavailable)
            : new CustomException(message, inner, HttpStatusCode.ServiceUnavailable);

    private sealed record AccessContext(OrganizationId OrganizationId, UserId UserId);
}
