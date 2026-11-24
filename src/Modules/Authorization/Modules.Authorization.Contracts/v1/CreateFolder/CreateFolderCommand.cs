using FSH.Modules.Authorization.Contracts.v1.GetOrganizationRoot;
using Mediator;

namespace FSH.Modules.Authorization.Contracts.v1.CreateFolder;

public sealed record CreateFolderCommand(
    Guid ParentId,
    string Name,
    string? AuthorizationToken = null) : ICommand<OrganizationRootResponse>;
