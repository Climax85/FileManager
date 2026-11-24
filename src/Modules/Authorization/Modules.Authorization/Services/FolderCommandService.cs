using FSH.Framework.Core.Context;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Eventing.Outbox;
using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Authorization.Contracts.Events;
using FSH.Modules.Authorization.Contracts.v1.GetOrganizationRoot;
using FSH.Modules.Authorization.Data;
using FSH.Modules.Authorization.Domain;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Authorization.Services;

public sealed class FolderCommandService(
    AuthorizationDbContext db,
    ICurrentUser currentUser,
    IFolderHierarchy hierarchy,
    IFolderAuthorization authorization,
    IRelationshipWriter relationshipWriter,
    EfCoreOutboxStore<AuthorizationDbContext> outbox,
    TimeProvider timeProvider)
{
    public async ValueTask<OrganizationRootResponse> CreateAsync(
        FolderId parentId,
        string name,
        AuthorizationToken? requestedToken,
        CancellationToken cancellationToken)
    {
        OrganizationId organizationId = CurrentOrganization();
        FolderDescriptor parent = await hierarchy.FindAsync(parentId, organizationId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("folder parent not found");
        await authorization.RequireAsync(
            parent.Id, AuthorizationPermission.Edit, requestedToken, cancellationToken).ConfigureAwait(false);

        Guid operationId = Guid.CreateVersion7();
        var folder = Folder.Create(FolderId.New(), organizationId, parent.Id, name, timeProvider.GetUtcNow(), operationId);
        var sync = RelationshipSynchronization.Pending(operationId, organizationId);
        IReadOnlyCollection<RelationshipMutation> mutations =
        [
            RelationshipMutation.Touch(
                AuthorizationObjects.For(folder.Id),
                AuthorizationRelation.Organization,
                new AuthorizationSubject(AuthorizationObjects.For(organizationId))),
            RelationshipMutation.Touch(
                AuthorizationObjects.For(folder.Id),
                AuthorizationRelation.Parent,
                new AuthorizationSubject(AuthorizationObjects.For(parent.Id))),
        ];
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            db.Folders.Add(folder);
            db.RelationshipSyncs.Add(sync);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await outbox.AddAsync(Event(operationId, organizationId, mutations), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        RelationshipConfirmation confirmation = await ConfirmAsync(sync, mutations, cancellationToken).ConfigureAwait(false);
        return Response(folder, confirmation.WrittenAt, true);
    }

    public async ValueTask<OrganizationRootResponse> RenameAsync(
        FolderId folderId,
        string name,
        AuthorizationToken? requestedToken,
        CancellationToken cancellationToken)
    {
        OrganizationId organizationId = CurrentOrganization();
        AuthorizationToken authorizedAt = await authorization.RequireAsync(
            folderId, AuthorizationPermission.Edit, requestedToken, cancellationToken).ConfigureAwait(false);
        Folder folder = await db.Folders
            .SingleOrDefaultAsync(x => x.Id == folderId && x.OrganizationId == organizationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException("folder not found");
        folder.Rename(name);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Response(folder, authorizedAt, true);
    }

    private async ValueTask<RelationshipConfirmation> ConfirmAsync(
        RelationshipSynchronization sync,
        IReadOnlyCollection<RelationshipMutation> mutations,
        CancellationToken cancellationToken)
    {
        try
        {
            RelationshipConfirmation confirmation = await relationshipWriter
                .WriteAsync(sync.OperationId, mutations, cancellationToken)
                .ConfigureAwait(false);
            sync.MarkConfirmed(confirmation.WrittenAt.Value);
            OutboxMessage? message = await db.OutboxMessages
                .SingleOrDefaultAsync(x => x.Id == sync.OperationId, cancellationToken)
                .ConfigureAwait(false);
            if (message is not null) message.ProcessedOnUtc = confirmation.ConfirmedAt.UtcDateTime;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return confirmation;
        }
        catch (RpcException exception)
        {
            sync.MarkFailed(exception.Status.Detail);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private OrganizationId CurrentOrganization()
    {
        string? tenant = currentUser.GetTenant();
        if (string.IsNullOrWhiteSpace(tenant)) throw new NotFoundException("folder not found");
        return new OrganizationId(tenant);
    }

    private AuthorizationRelationshipsChangedIntegrationEvent Event(
        Guid operationId,
        OrganizationId organizationId,
        IReadOnlyCollection<RelationshipMutation> mutations) =>
        new(
            operationId,
            timeProvider.GetUtcNow().UtcDateTime,
            organizationId.Value,
            operationId.ToString("D"),
            "Authorization",
            mutations);

    private static OrganizationRootResponse Response(
        Folder folder,
        AuthorizationToken token,
        bool canEdit) => new(
            folder.Id.Value,
            folder.OrganizationId.Value,
            folder.Name,
            folder.IsRoot,
            folder.CreatedAt,
            token.Value,
            folder.ParentId?.Value,
            canEdit);
}
