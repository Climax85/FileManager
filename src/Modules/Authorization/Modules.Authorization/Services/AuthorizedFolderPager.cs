using System.Text.Json;
using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Authorization.Domain;
using Microsoft.AspNetCore.WebUtilities;

namespace FSH.Modules.Authorization.Services;

internal sealed record FolderPageCursor(DateTimeOffset CreatedAt, FolderId Id)
{
    public static string Encode(FolderPageCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        return WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new Payload(cursor.CreatedAt, cursor.Id.Value)));
    }

    public static FolderPageCursor Decode(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        try
        {
            Payload payload = JsonSerializer.Deserialize<Payload>(WebEncoders.Base64UrlDecode(value))
                ?? throw new ArgumentException("The folder cursor is invalid.", nameof(value));
            return new FolderPageCursor(payload.CreatedAt, new FolderId(payload.Id));
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            throw new ArgumentException("The folder cursor is invalid.", nameof(value), exception);
        }
    }

    private sealed record Payload(DateTimeOffset CreatedAt, Guid Id);
}

internal sealed record FolderPageResult(
    IReadOnlyList<Folder> Items,
    FolderPageCursor? NextCursor,
    AuthorizationToken AuthorizationToken);

internal delegate ValueTask<IReadOnlyList<Folder>> FolderBatchLoader(
    FolderId parentId,
    FolderPageCursor? cursor,
    int limit,
    CancellationToken cancellationToken);

internal static class AuthorizedFolderPager
{
    public static async ValueTask<FolderPageResult> ReadAsync(
        FolderId parentId,
        UserId userId,
        AuthorizationToken token,
        int pageSize,
        FolderPageCursor? cursor,
        FolderBatchLoader loadBatch,
        IPermissionChecker permissionChecker,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 100);
        ArgumentNullException.ThrowIfNull(loadBatch);
        ArgumentNullException.ThrowIfNull(permissionChecker);

        var visible = new List<Folder>(pageSize);
        AuthorizationToken responseToken = token;
        while (true)
        {
            int slots = pageSize - visible.Count;
            IReadOnlyList<Folder> fetched = await loadBatch(parentId, cursor, slots + 1, cancellationToken).ConfigureAwait(false);
            if (fetched.Count == 0) return new FolderPageResult(visible, null, responseToken);
            int checkedCount = Math.Min(slots, fetched.Count);
            var requests = new PermissionCheck[checkedCount];
            for (int index = 0; index < checkedCount; index++)
            {
                requests[index] = new PermissionCheck(
                    userId,
                    AuthorizationObjects.For(fetched[index].Id),
                    AuthorizationPermission.View,
                    AuthorizationConsistency.AtLeastAsFresh(token));
            }

            IReadOnlyList<PermissionCheckResult> results = await permissionChecker.CheckBulkAsync(
                new BulkPermissionCheck(requests), cancellationToken).ConfigureAwait(false);
            var decisions = new Dictionary<FolderId, bool>(checkedCount);
            foreach (PermissionCheckResult result in results)
            {
                if (result.Request.Resource.Type == AuthorizationObjectType.Folder
                    && Guid.TryParse(result.Request.Resource.ObjectId, out Guid id))
                {
                    decisions[new FolderId(id)] = result.IsAllowed;
                    if (result.CheckedAt is AuthorizationToken checkedAt) responseToken = checkedAt;
                }
            }
            for (int index = 0; index < checkedCount; index++)
            {
                Folder folder = fetched[index];
                if (decisions.TryGetValue(folder.Id, out bool allowed) && allowed) visible.Add(folder);
            }

            Folder lastChecked = fetched[checkedCount - 1];
            cursor = new FolderPageCursor(lastChecked.CreatedAt, lastChecked.Id);
            bool hasUncheckedRows = fetched.Count > checkedCount;
            if (visible.Count == pageSize)
            {
                return new FolderPageResult(visible, hasUncheckedRows ? cursor : null, responseToken);
            }
            if (!hasUncheckedRows) return new FolderPageResult(visible, null, responseToken);
        }
    }
}
