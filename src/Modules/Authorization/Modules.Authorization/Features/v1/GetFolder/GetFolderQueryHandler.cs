using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Authorization.Contracts.v1.GetFolder;
using FSH.Modules.Authorization.Contracts.v1.GetOrganizationRoot;
using FSH.Modules.Authorization.Services;
using Mediator;

namespace FSH.Modules.Authorization.Features.v1.GetFolder;

public sealed class GetFolderQueryHandler(AuthorizedFolderReader reader)
    : IQueryHandler<GetFolderQuery, OrganizationRootResponse>
{
    public async ValueTask<OrganizationRootResponse> Handle(GetFolderQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await reader.GetAsync(new FolderId(query.FolderId), query.AuthorizationToken, cancellationToken).ConfigureAwait(false);
    }
}
