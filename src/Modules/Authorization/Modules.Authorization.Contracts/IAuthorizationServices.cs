namespace FSH.Modules.Authorization.Contracts;

public interface IPermissionChecker
{
    ValueTask<PermissionCheckResult> CheckAsync(PermissionCheck request, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<PermissionCheckResult>> CheckBulkAsync(
        BulkPermissionCheck request,
        CancellationToken cancellationToken = default);
}

public interface IRelationshipWriter
{
    ValueTask<RelationshipConfirmation> WriteAsync(
        Guid operationId,
        IReadOnlyCollection<RelationshipMutation> mutations,
        CancellationToken cancellationToken = default);
}

public interface IRelationshipReader
{
    ValueTask<AuthorizationRelationshipPage> ReadAsync(
        RelationshipQuery query,
        CancellationToken cancellationToken = default);
}

public interface IAuthorizationDiscovery
{
    ValueTask<AuthorizationResourcePage> LookupResourcesAsync(
        LookupResourcesRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record RelationshipQuery(
    AuthorizationObjectType ResourceType,
    string? ResourceId = null,
    AuthorizationRelation? Relation = null,
    AuthorizationSubject? Subject = null,
    int Limit = 100,
    AuthorizationCursor? Cursor = null,
    AuthorizationConsistency? Consistency = null);

public sealed record AuthorizationRelationship(
    AuthorizationObject Resource,
    AuthorizationRelation Relation,
    AuthorizationSubject Subject,
    DateTimeOffset? ExpiresAt);

public sealed record AuthorizationRelationshipPage(
    IReadOnlyList<AuthorizationRelationship> Relationships,
    AuthorizationCursor? NextCursor,
    AuthorizationToken? ReadAt);
