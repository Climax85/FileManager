using System.Net;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Eventing.Outbox;
using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Authorization.Contracts.Events;
using FSH.Modules.Authorization.Contracts.v1.MoveResource;
using FSH.Modules.Authorization.Data;
using FSH.Modules.Authorization.Domain;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Authorization.Services;

public sealed class FolderResourceMoveHandler(
    AuthorizationDbContext db,
    IFolderHierarchy hierarchy,
    IResourceMoveAuthorization moveAuthorization,
    IRelationshipWriter relationshipWriter,
    EfCoreOutboxStore<AuthorizationDbContext> outbox,
    TimeProvider timeProvider) : IResourceMoveHandler
{
    public AuthorizationObjectType ResourceType => AuthorizationObjectType.Folder;

    public async ValueTask<MoveResourceResponse> MoveAsync(
        MoveResourceCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var folderId = new FolderId(command.ResourceId);
        var destinationId = new FolderId(command.DestinationFolderId);
        AuthorizationObject resource = AuthorizationObjects.For(folderId);
        ResourceMoveAuthorization authorization = await moveAuthorization.RequireAsync(
            resource, destinationId, command.AuthorizationToken, cancellationToken).ConfigureAwait(false);
        Folder folder = await db.Folders.SingleOrDefaultAsync(
            x => x.Id == folderId && x.OrganizationId == authorization.OrganizationId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("folder not found");
        Folder destination = await db.Folders.SingleOrDefaultAsync(
            x => x.Id == destinationId && x.OrganizationId == authorization.OrganizationId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("destination folder not found");
        if (folder.SynchronizationId is not Guid currentOperationId ||
            !await db.RelationshipSyncs.AsNoTracking().AnyAsync(
                x => x.OperationId == currentOperationId && x.Status == RelationshipSynchronizationStatus.Confirmed,
                cancellationToken).ConfigureAwait(false))
        {
            await moveAuthorization.AuditDeniedAsync(resource, destinationId, "SourceSynchronizationPending", cancellationToken)
                .ConfigureAwait(false);
            throw new CustomException("The folder cannot be moved until its current authorization synchronization is confirmed.",
                [], HttpStatusCode.Conflict);
        }

        FolderId? oldParentId = folder.ParentId;
        try
        {
            await hierarchy.EnsureValidParentAsync(
                authorization.OrganizationId, destinationId, folderId, cancellationToken).ConfigureAwait(false);
            folder.ChangeParent(destination);
        }
        catch (InvalidOperationException exception)
        {
            await moveAuthorization.AuditDeniedAsync(resource, destinationId, "InvalidFolderHierarchy", cancellationToken)
                .ConfigureAwait(false);
            throw new CustomException(exception.Message, [], HttpStatusCode.BadRequest);
        }

        Guid operationId = Guid.CreateVersion7();
        db.Entry(folder).Property(x => x.SynchronizationId).CurrentValue = operationId;
        var sync = RelationshipSynchronization.Pending(operationId, authorization.OrganizationId);
        db.RelationshipSyncs.Add(sync);
        IReadOnlyCollection<RelationshipMutation> mutations = ParentMutations(folderId, oldParentId, destinationId);
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await outbox.AddAsync(Event(operationId, authorization.OrganizationId, mutations), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            RelationshipConfirmation confirmation = await relationshipWriter.WriteAsync(operationId, mutations, cancellationToken)
                .ConfigureAwait(false);
            sync.MarkConfirmed(confirmation.WrittenAt.Value);
            OutboxMessage? message = await db.OutboxMessages.SingleOrDefaultAsync(x => x.Id == operationId, cancellationToken)
                .ConfigureAwait(false);
            if (message is not null) message.ProcessedOnUtc = confirmation.ConfirmedAt.UtcDateTime;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await moveAuthorization.AuditSucceededAsync(resource, destinationId, operationId, cancellationToken).ConfigureAwait(false);
            return new MoveResourceResponse(
                AuthorizationObjectType.Folder,
                folder.Id.Value,
                destinationId.Value,
                operationId,
                RelationshipSynchronizationStatus.Confirmed,
                confirmation.WrittenAt.Value);
        }
        catch (RpcException)
        {
            sync.MarkFailed("Authorization synchronization is unavailable.");
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await moveAuthorization.AuditSynchronizationFailedAsync(resource, destinationId, operationId, cancellationToken)
                .ConfigureAwait(false);
            throw new CustomException("The move remains pending until authorization synchronization is confirmed.",
                [], HttpStatusCode.ServiceUnavailable);
        }
    }

    private static List<RelationshipMutation> ParentMutations(
        FolderId folderId,
        FolderId? oldParentId,
        FolderId newParentId)
    {
        AuthorizationObject resource = AuthorizationObjects.For(folderId);
        var mutations = new List<RelationshipMutation>
        {
            RelationshipMutation.Touch(resource, AuthorizationRelation.Parent,
                new AuthorizationSubject(AuthorizationObjects.For(newParentId))),
        };
        if (oldParentId is FolderId oldParent && oldParent != newParentId)
        {
            mutations.Insert(0, RelationshipMutation.Delete(resource, AuthorizationRelation.Parent,
                new AuthorizationSubject(AuthorizationObjects.For(oldParent))));
        }
        return mutations;
    }

    private AuthorizationRelationshipsChangedIntegrationEvent Event(
        Guid operationId,
        OrganizationId organizationId,
        IReadOnlyCollection<RelationshipMutation> mutations) =>
        new(operationId, timeProvider.GetUtcNow().UtcDateTime, organizationId.Value,
            operationId.ToString("D"), "Authorization", mutations);
}
