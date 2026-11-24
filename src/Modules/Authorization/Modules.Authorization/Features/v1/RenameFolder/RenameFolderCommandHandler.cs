using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Authorization.Contracts.v1.GetOrganizationRoot;
using FSH.Modules.Authorization.Contracts.v1.RenameFolder;
using FSH.Modules.Authorization.Services;
using Mediator;

namespace FSH.Modules.Authorization.Features.v1.RenameFolder;

public sealed class RenameFolderCommandHandler(FolderCommandService service)
    : ICommandHandler<RenameFolderCommand, OrganizationRootResponse>
{
    public async ValueTask<OrganizationRootResponse> Handle(RenameFolderCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        AuthorizationToken? token = string.IsNullOrWhiteSpace(command.AuthorizationToken)
            ? null
            : new AuthorizationToken(command.AuthorizationToken);
        return await service.RenameAsync(new FolderId(command.FolderId), command.Name, token, cancellationToken).ConfigureAwait(false);
    }
}
