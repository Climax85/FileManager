using FSH.Modules.Authorization.Contracts.v1.GetOrganizationRoot;
using Mediator;

namespace FSH.Modules.Authorization.Contracts.v1.RenameFolder;

public sealed record RenameFolderCommand(
    Guid FolderId,
    string Name,
    string? AuthorizationToken = null) : ICommand<OrganizationRootResponse>;
