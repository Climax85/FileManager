using FSH.Modules.Authorization.Contracts.v1.GetOrganizationRoot;
using FSH.Modules.Authorization.Services;
using Mediator;

namespace FSH.Modules.Authorization.Features.v1.GetOrganizationRoot;

public sealed class GetOrganizationRootQueryHandler(AuthorizedFolderReader reader)
    : IQueryHandler<GetOrganizationRootQuery, OrganizationRootResponse>
{
    public async ValueTask<OrganizationRootResponse> Handle(
        GetOrganizationRootQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await reader.GetRootAsync(query.AuthorizationToken, cancellationToken).ConfigureAwait(false);
    }
}
