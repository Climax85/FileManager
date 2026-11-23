using Mediator;

namespace FSH.Modules.Authorization.Contracts.v1.GetOrganizationRoot;

public sealed record GetOrganizationRootQuery(string? AuthorizationToken = null) : IQuery<OrganizationRootResponse>;

public sealed record OrganizationRootResponse(
    Guid Id,
    string OrganizationId,
    string Name,
    bool IsRoot,
    DateTimeOffset CreatedAt,
    string AuthorizationToken);
