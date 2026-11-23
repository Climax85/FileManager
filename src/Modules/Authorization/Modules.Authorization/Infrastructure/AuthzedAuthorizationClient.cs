using Authzed.Api.V1;
using FSH.Modules.Authorization.Contracts;
using Grpc.Core;

namespace FSH.Modules.Authorization.Infrastructure;

public sealed class AuthzedAuthorizationClient(
    PermissionsService.PermissionsServiceClient client,
    AuthzedRetryPolicy retryPolicy,
    TimeProvider timeProvider)
    : IPermissionChecker, IRelationshipWriter, IRelationshipReader, IAuthorizationDiscovery
{
    public async ValueTask<PermissionCheckResult> CheckAsync(
        PermissionCheck request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            CheckPermissionResponse response = await retryPolicy.ExecuteAsync(
                async ct => await client.CheckPermissionAsync(new CheckPermissionRequest
                {
                    Resource = AuthzedChannelFactory.Object(request.Resource),
                    Permission = AuthorizationSchema.Permission(request.Permission),
                    Subject = AuthzedChannelFactory.Subject(AuthorizationSubjects.For(request.Subject)),
                    Consistency = AuthzedChannelFactory.Consistency(request.Consistency),
                }, cancellationToken: ct).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);

            return new PermissionCheckResult(
                request,
                response.Permissionship == CheckPermissionResponse.Types.Permissionship.HasPermission,
                Token(response.CheckedAt),
                response.Permissionship switch
                {
                    CheckPermissionResponse.Types.Permissionship.HasPermission => null,
                    CheckPermissionResponse.Types.Permissionship.ConditionalPermission => AuthorizationFailure.ConditionalPermission,
                    _ => AuthorizationFailure.NoPermission,
                });
        }
        catch (RpcException)
        {
            return new PermissionCheckResult(request, false, null, AuthorizationFailure.Unavailable);
        }
    }

    public async ValueTask<IReadOnlyList<PermissionCheckResult>> CheckBulkAsync(
        BulkPermissionCheck request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Items.Count == 0) return [];
        if (request.Items.Count > 1000) throw new ArgumentException("Bulk permission checks are limited to 1000 items.", nameof(request));

        var providerRequest = new CheckBulkPermissionsRequest();
        foreach (PermissionCheck item in request.Items)
        {
            providerRequest.Items.Add(new CheckBulkPermissionsRequestItem
            {
                Resource = AuthzedChannelFactory.Object(item.Resource),
                Permission = AuthorizationSchema.Permission(item.Permission),
                Subject = AuthzedChannelFactory.Subject(AuthorizationSubjects.For(item.Subject)),
            });
        }
        providerRequest.Consistency = AuthzedChannelFactory.Consistency(request.Items[0].Consistency);

        try
        {
            CheckBulkPermissionsResponse response = await retryPolicy.ExecuteAsync(
                async ct => await client.CheckBulkPermissionsAsync(providerRequest, cancellationToken: ct).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
            var results = new PermissionCheckResult[request.Items.Count];
            var assigned = new bool[request.Items.Count];
            foreach (CheckBulkPermissionsPair pair in response.Pairs)
            {
                int index = FindRequestIndex(request.Items, pair.Request, assigned);
                if (index < 0) continue;
                assigned[index] = true;
                CheckBulkPermissionsResponseItem? item = pair.Item;
                results[index] = new PermissionCheckResult(
                    request.Items[index],
                    item?.Permissionship == CheckPermissionResponse.Types.Permissionship.HasPermission,
                    Token(response.CheckedAt),
                    pair.ResponseCase == CheckBulkPermissionsPair.ResponseOneofCase.Error
                        ? AuthorizationFailure.Unavailable
                        : item?.Permissionship == CheckPermissionResponse.Types.Permissionship.ConditionalPermission
                            ? AuthorizationFailure.ConditionalPermission
                            : item?.Permissionship == CheckPermissionResponse.Types.Permissionship.HasPermission
                                ? null
                                : AuthorizationFailure.NoPermission);
            }

            for (int index = 0; index < results.Length; index++)
            {
                results[index] ??= new PermissionCheckResult(request.Items[index], false, null, AuthorizationFailure.Unavailable);
            }
            return results;
        }
        catch (RpcException)
        {
            return request.Items.Select(item => new PermissionCheckResult(item, false, null, AuthorizationFailure.Unavailable)).ToArray();
        }
    }

    public async ValueTask<RelationshipConfirmation> WriteAsync(
        Guid operationId,
        IReadOnlyCollection<RelationshipMutation> mutations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        if (mutations.Count == 0) throw new ArgumentException("At least one relationship mutation is required.", nameof(mutations));
        if (mutations.Count > 1000) throw new ArgumentException("Relationship writes are limited to 1000 mutations.", nameof(mutations));

        var request = new WriteRelationshipsRequest();
        foreach (RelationshipMutation mutation in mutations) request.Updates.Add(AuthzedChannelFactory.Update(mutation));
        WriteRelationshipsResponse response = await retryPolicy.ExecuteAsync(
            async ct => await client.WriteRelationshipsAsync(request, cancellationToken: ct).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        return new RelationshipConfirmation(
            operationId,
            new AuthorizationToken(response.WrittenAt.Token),
            timeProvider.GetUtcNow());
    }

    public async ValueTask<AuthorizationResourcePage> LookupResourcesAsync(
        LookupResourcesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(request));
        var providerRequest = new LookupResourcesRequest
        {
            ResourceObjectType = AuthorizationSchema.Definition(request.ResourceType),
            Permission = AuthorizationSchema.Permission(request.Permission),
            Subject = AuthzedChannelFactory.Subject(AuthorizationSubjects.For(request.Subject)),
            Consistency = AuthzedChannelFactory.Consistency(request.Consistency),
            OptionalLimit = (uint)request.Limit,
            OptionalCursor = request.Cursor is null ? null : new Cursor { Token = request.Cursor.Value.Value },
        };
        using var call = client.LookupResources(providerRequest, cancellationToken: cancellationToken);
        var resources = new List<AuthorizationObject>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Cursor? cursor = null;
        ZedToken? lookedUpAt = null;
        await foreach (LookupResourcesResponse response in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            lookedUpAt = response.LookedUpAt ?? lookedUpAt;
            cursor = response.AfterResultCursor ?? cursor;
            if (response.Permissionship == LookupPermissionship.HasPermission && seen.Add(response.ResourceObjectId))
            {
                resources.Add(new AuthorizationObject(request.ResourceType, response.ResourceObjectId));
            }
        }
        return new AuthorizationResourcePage(resources, Cursor(cursor), Token(lookedUpAt));
    }

    public async ValueTask<AuthorizationRelationshipPage> ReadAsync(
        RelationshipQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(query));
        var filter = new RelationshipFilter
        {
            ResourceType = AuthorizationSchema.Definition(query.ResourceType),
            OptionalResourceId = query.ResourceId ?? string.Empty,
            OptionalRelation = query.Relation is null ? string.Empty : AuthorizationSchema.Relation(query.Relation.Value),
            OptionalSubjectFilter = query.Subject is null ? null : new SubjectFilter
            {
                SubjectType = query.Subject.Object.Definition,
                OptionalSubjectId = query.Subject.Object.ObjectId,
                OptionalRelation = query.Subject.OptionalRelation is null
                    ? null
                    : new SubjectFilter.Types.RelationFilter
                    {
                        Relation = AuthorizationSchema.Relation(query.Subject.OptionalRelation.Value),
                    },
            },
        };
        var providerRequest = new ReadRelationshipsRequest
        {
            RelationshipFilter = filter,
            Consistency = AuthzedChannelFactory.Consistency(query.Consistency),
            OptionalLimit = (uint)query.Limit,
            OptionalCursor = query.Cursor is null ? null : new Cursor { Token = query.Cursor.Value.Value },
        };
        using var call = client.ReadRelationships(providerRequest, cancellationToken: cancellationToken);
        var relationships = new List<AuthorizationRelationship>();
        Cursor? cursor = null;
        ZedToken? readAt = null;
        await foreach (ReadRelationshipsResponse response in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            readAt = response.ReadAt ?? readAt;
            cursor = response.AfterResultCursor ?? cursor;
            Relationship relationship = response.Relationship;
            var subjectObject = new AuthorizationObject(
                AuthorizationSchema.ParseDefinition(relationship.Subject.Object.ObjectType),
                relationship.Subject.Object.ObjectId);
            relationships.Add(new AuthorizationRelationship(
                new AuthorizationObject(
                    AuthorizationSchema.ParseDefinition(relationship.Resource.ObjectType),
                    relationship.Resource.ObjectId),
                AuthorizationSchema.ParseRelation(relationship.Relation),
                new AuthorizationSubject(
                    subjectObject,
                    string.IsNullOrEmpty(relationship.Subject.OptionalRelation)
                        ? null
                        : AuthorizationSchema.ParseRelation(relationship.Subject.OptionalRelation)),
                relationship.OptionalExpiresAt?.ToDateTimeOffset()));
        }
        return new AuthorizationRelationshipPage(relationships, Cursor(cursor), Token(readAt));
    }

    private static int FindRequestIndex(
        IReadOnlyList<PermissionCheck> requests,
        CheckBulkPermissionsRequestItem providerRequest,
        IReadOnlyList<bool> assigned)
    {
        for (int index = 0; index < requests.Count; index++)
        {
            PermissionCheck candidate = requests[index];
            if (!assigned[index]
                && candidate.Resource.ObjectId == providerRequest.Resource.ObjectId
                && candidate.Resource.Definition == providerRequest.Resource.ObjectType
                && candidate.Subject.Value == providerRequest.Subject.Object.ObjectId
                && AuthorizationSchema.Permission(candidate.Permission) == providerRequest.Permission)
            {
                return index;
            }
        }
        return -1;
    }

    private static AuthorizationToken? Token(ZedToken? token) =>
        string.IsNullOrWhiteSpace(token?.Token) ? null : new AuthorizationToken(token.Token);

    private static AuthorizationCursor? Cursor(Cursor? cursor) =>
        string.IsNullOrWhiteSpace(cursor?.Token) ? null : new AuthorizationCursor(cursor.Token);
}
