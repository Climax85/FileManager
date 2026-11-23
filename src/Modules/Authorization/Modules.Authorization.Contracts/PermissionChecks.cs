namespace FSH.Modules.Authorization.Contracts;

public sealed record AuthorizationConsistency
{
    private AuthorizationConsistency(AuthorizationToken? atLeastAsFreshToken) =>
        AtLeastAsFreshToken = atLeastAsFreshToken;

    public AuthorizationToken? AtLeastAsFreshToken { get; }

    public static AuthorizationConsistency MinimizeLatency() => new(null);

    public static AuthorizationConsistency AtLeastAsFresh(AuthorizationToken token) => new(token);
}

public sealed record PermissionCheck(
    UserId Subject,
    AuthorizationObject Resource,
    AuthorizationPermission Permission,
    AuthorizationConsistency? Consistency = null);

public sealed record PermissionCheckResult(
    PermissionCheck Request,
    bool IsAllowed,
    AuthorizationToken? CheckedAt,
    AuthorizationFailure? Failure = null);

public enum AuthorizationFailure
{
    NoPermission = 1,
    ConditionalPermission = 2,
    Unavailable = 3,
}

public sealed record BulkPermissionCheck(IReadOnlyList<PermissionCheck> Items);

public sealed record LookupResourcesRequest(
    UserId Subject,
    AuthorizationObjectType ResourceType,
    AuthorizationPermission Permission,
    int Limit = 100,
    AuthorizationCursor? Cursor = null,
    AuthorizationConsistency? Consistency = null);

public sealed record AuthorizationResourcePage(
    IReadOnlyList<AuthorizationObject> Resources,
    AuthorizationCursor? NextCursor,
    AuthorizationToken? LookedUpAt);
