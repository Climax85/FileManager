using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Authorization.Contracts.v1.CreateFolder;
using FSH.Modules.Authorization.Contracts.v1.GetOrganizationRoot;
using FSH.Modules.Authorization.Services;
using Mediator;

namespace FSH.Modules.Authorization.Features.v1.CreateFolder;

public sealed class CreateFolderCommandHandler(FolderCommandService service)
    : ICommandHandler<CreateFolderCommand, OrganizationRootResponse>
{
    public async ValueTask<OrganizationRootResponse> Handle(CreateFolderCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        AuthorizationToken? token = string.IsNullOrWhiteSpace(command.AuthorizationToken)
            ? null
            : new AuthorizationToken(command.AuthorizationToken);
        return await service.CreateAsync(new FolderId(command.ParentId), command.Name, token, cancellationToken).ConfigureAwait(false);
    }
}
