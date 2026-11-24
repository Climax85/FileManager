using Mediator;

namespace FSH.Modules.Authorization.Contracts.v1.MoveResource;

public sealed record MoveResourceRequest(Guid DestinationFolderId);

public sealed record MoveResourceCommand(
    AuthorizationObjectType ResourceType,
    Guid ResourceId,
    Guid DestinationFolderId,
    string? AuthorizationToken = null) : ICommand<MoveResourceResponse>;

public sealed record MoveResourceResponse(
    AuthorizationObjectType ResourceType,
    Guid ResourceId,
    Guid ParentFolderId,
    Guid OperationId,
    RelationshipSynchronizationStatus SynchronizationStatus,
    string AuthorizationToken);

public sealed record ResourceMoveAuthorization(
    OrganizationId OrganizationId,
    UserId ActorId,
    AuthorizationToken AuthorizationToken);

public interface IResourceMoveAuthorization
{
    ValueTask<ResourceMoveAuthorization> RequireAsync(
        AuthorizationObject resource,
        FolderId destinationFolderId,
        string? requestedToken,
        CancellationToken cancellationToken = default);

    ValueTask AuditDeniedAsync(
        AuthorizationObject resource,
        FolderId destinationFolderId,
        string reasonCode,
        CancellationToken cancellationToken = default);

    ValueTask AuditSucceededAsync(
        AuthorizationObject resource,
        FolderId destinationFolderId,
        Guid operationId,
        CancellationToken cancellationToken = default);

    ValueTask AuditSynchronizationFailedAsync(
        AuthorizationObject resource,
        FolderId destinationFolderId,
        Guid operationId,
        CancellationToken cancellationToken = default);
}

public interface IResourceMoveHandler
{
    AuthorizationObjectType ResourceType { get; }

    ValueTask<MoveResourceResponse> MoveAsync(
        MoveResourceCommand command,
        CancellationToken cancellationToken = default);
}
