using Mediator;
using FSH.Modules.Authorization.Contracts.v1.GetOrganizationRoot;

namespace FSH.Modules.Authorization.Contracts.v1.GetFolder;

public sealed record GetFolderQuery(Guid FolderId, string? AuthorizationToken = null) : IQuery<OrganizationRootResponse>;
