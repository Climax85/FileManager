using Mediator;

namespace FSH.Modules.Authorization.Contracts.v1.Sharing;

public sealed record CreateResourceGrantRequest(
    AuthorizationObjectType SubjectType,
    string SubjectId,
    AuthorizationRelation Relation,
    bool ConfirmOrganizationWide = false);

public sealed record CreateResourceGrantCommand(
    AuthorizationObjectType ResourceType,
    Guid ResourceId,
    AuthorizationObjectType SubjectType,
    string SubjectId,
    AuthorizationRelation Relation,
    bool ConfirmOrganizationWide,
    string? AuthorizationToken) : ICommand<ResourceGrantResponse>;

public sealed record RevokeResourceGrantCommand(
    Guid GrantId,
    string? AuthorizationToken) : ICommand<ResourceGrantResponse>;

public sealed record GetResourceGrantsQuery(
    AuthorizationObjectType ResourceType,
    Guid ResourceId,
    string? AuthorizationToken) : IQuery<ResourceGrantListResponse>;

public sealed record GetEffectiveAccessQuery(
    AuthorizationObjectType ResourceType,
    Guid ResourceId,
    string? SubjectId,
    string? AuthorizationToken) : IQuery<EffectiveAccessResponse>;

public sealed record ResourceGrantResponse(
    Guid Id,
    AuthorizationObjectType ResourceType,
    Guid ResourceId,
    AuthorizationObjectType SubjectType,
    string SubjectId,
    AuthorizationRelation Relation,
    ResourceGrantStatus Status,
    string AuthorizationToken,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ConfirmedAt);

public sealed record ResourceGrantListResponse(
    IReadOnlyList<ResourceGrantResponse> Grants,
    string AuthorizationToken);

public enum EffectiveAccessLevel
{
    None = 0,
    Viewer = 1,
    Editor = 2,
    Manager = 3,
}

public enum EffectiveAccessSourceKind
{
    None = 0,
    Direct = 1,
    Inherited = 2,
    Organization = 3,
}

public sealed record EffectiveAccessSourceResponse(
    Guid? GrantId,
    EffectiveAccessSourceKind Source,
    AuthorizationObjectType ResourceType,
    Guid ResourceId,
    AuthorizationRelation Relation,
    AuthorizationObjectType SubjectType,
    string SubjectId);

public sealed record EffectiveAccessResponse(
    AuthorizationObjectType ResourceType,
    Guid ResourceId,
    string SubjectId,
    EffectiveAccessLevel Access,
    IReadOnlyList<EffectiveAccessSourceResponse> Sources,
    string AuthorizationToken);
