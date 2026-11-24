using System.Net;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Eventing.Outbox;
using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Authorization.Contracts.Events;
using FSH.Modules.Authorization.Contracts.v1.MoveResource;
using FSH.Modules.Files.Data;
using FSH.Modules.Files.Domain;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Files.Services;

public sealed class FileResourceMoveHandler(
    FilesDbContext db,
    IResourceMoveAuthorization moveAuthorization,
    IRelationshipWriter relationshipWriter,
    EfCoreOutboxStore<FilesDbContext> outbox,
    TimeProvider timeProvider) : IResourceMoveHandler
{
    public AuthorizationObjectType ResourceType => AuthorizationObjectType.File;

    public async ValueTask<MoveResourceResponse> MoveAsync(
        MoveResourceCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var fileId = new FileId(command.ResourceId);
        var destinationId = new FolderId(command.DestinationFolderId);
        AuthorizationObject resource = AuthorizationObjects.For(fileId);
        ResourceMoveAuthorization authorization = await moveAuthorization.RequireAsync(
            resource, destinationId, command.AuthorizationToken, cancellationToken).ConfigureAwait(false);
        FileAsset? file = await db.FileAssets.SingleOrDefaultAsync(x => x.Id == fileId.Value, cancellationToken)
            .ConfigureAwait(false);
        if (file is null || file.OwnerType is not ("MyFiles" or "User") || file.ParentFolderId is not Guid oldParentId)
        {
            await moveAuthorization.AuditDeniedAsync(resource, destinationId, "FileManagerResourceRequired", cancellationToken)
                .ConfigureAwait(false);
            throw new NotFoundException("file not found");
        }
        if (file.AuthorizationSynchronizationStatus != RelationshipSynchronizationStatus.Confirmed)
        {
            await moveAuthorization.AuditDeniedAsync(resource, destinationId, "SourceSynchronizationPending", cancellationToken)
                .ConfigureAwait(false);
            throw new CustomException("The file cannot be moved until its current authorization synchronization is confirmed.",
                [], HttpStatusCode.Conflict);
        }

        Guid operationId = Guid.CreateVersion7();
        file.BeginMove(destinationId.Value, operationId);
        IReadOnlyCollection<RelationshipMutation> mutations = ParentMutations(fileId, oldParentId, destinationId);
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
            file.MarkAuthorizationConfirmed(confirmation.WrittenAt.Value, confirmation.ConfirmedAt);
            OutboxMessage? message = await db.OutboxMessages.SingleOrDefaultAsync(x => x.Id == operationId, cancellationToken)
                .ConfigureAwait(false);
            if (message is not null) message.ProcessedOnUtc = confirmation.ConfirmedAt.UtcDateTime;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await moveAuthorization.AuditSucceededAsync(resource, destinationId, operationId, cancellationToken).ConfigureAwait(false);
            return new MoveResourceResponse(
                AuthorizationObjectType.File,
                file.Id,
                destinationId.Value,
                operationId,
                RelationshipSynchronizationStatus.Confirmed,
                confirmation.WrittenAt.Value);
        }
        catch (RpcException exception)
        {
            file.MarkAuthorizationFailed(exception.Status.Detail);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await moveAuthorization.AuditSynchronizationFailedAsync(resource, destinationId, operationId, cancellationToken)
                .ConfigureAwait(false);
            throw new CustomException("The move remains pending until authorization synchronization is confirmed.",
                [], HttpStatusCode.ServiceUnavailable);
        }
    }

    private static IReadOnlyCollection<RelationshipMutation> ParentMutations(
        FileId fileId,
        Guid oldParentId,
        FolderId newParentId)
    {
        AuthorizationObject resource = AuthorizationObjects.For(fileId);
        AuthorizationSubject oldParent = new(AuthorizationObjects.For(new FolderId(oldParentId)));
        AuthorizationSubject newParent = new(AuthorizationObjects.For(newParentId));
        return oldParentId == newParentId.Value
            ? [RelationshipMutation.Touch(resource, AuthorizationRelation.Folder, newParent)]
            : [
                RelationshipMutation.Delete(resource, AuthorizationRelation.Folder, oldParent),
                RelationshipMutation.Touch(resource, AuthorizationRelation.Folder, newParent),
            ];
    }

    private AuthorizationRelationshipsChangedIntegrationEvent Event(
        Guid operationId,
        OrganizationId organizationId,
        IReadOnlyCollection<RelationshipMutation> mutations) =>
        new(operationId, timeProvider.GetUtcNow().UtcDateTime, organizationId.Value,
            operationId.ToString("D"), "Files", mutations);
}
