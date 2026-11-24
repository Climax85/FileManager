using Mediator;

namespace FSH.Modules.Authorization.Contracts.v1.ListFolderChildren;

public sealed record ListFolderChildrenQuery(
    Guid FolderId,
    int PageSize = 50,
    string? Cursor = null,
    string? AuthorizationToken = null) : IQuery<FolderChildrenResponse>;

public sealed record FolderChild(
    Guid Id,
    Guid ParentId,
    string Name,
    DateTimeOffset CreatedAt);

public sealed record FolderChildrenResponse(
    IReadOnlyList<FolderChild> Items,
    string? NextCursor,
    string AuthorizationToken);
