using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Authorization.Contracts.v1.ListFolderChildren;
using FSH.Modules.Authorization.Services;
using Mediator;

namespace FSH.Modules.Authorization.Features.v1.ListFolderChildren;

public sealed class ListFolderChildrenQueryHandler(AuthorizedFolderReader reader)
    : IQueryHandler<ListFolderChildrenQuery, FolderChildrenResponse>
{
    public async ValueTask<FolderChildrenResponse> Handle(ListFolderChildrenQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await reader.ListChildrenAsync(
            new FolderId(query.FolderId), query.PageSize, query.Cursor, query.AuthorizationToken, cancellationToken).ConfigureAwait(false);
    }
}
