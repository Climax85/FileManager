using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Files.Data;
using FSH.Modules.Files.Domain;
using FSH.Modules.Identity.Contracts.Services;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Files.Services;

public sealed class AuthorizedFileLister(
    IPermissionChecker permissionChecker,
    IOrganizationMembershipReader membershipReader)
{
    private const int BulkCheckLimit = 1000;

    public async ValueTask<IReadOnlyList<FileAsset>> ReadAsync(
        IQueryable<FileAsset> candidates,
        string currentUserId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        UserId userId = new(currentUserId);
        if (!await membershipReader.IsActiveMemberAsync(userId, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        List<FileAsset> rows = await candidates.ToListAsync(cancellationToken).ConfigureAwait(false);
        string? latestToken = rows
            .OrderByDescending(x => x.AuthorizationConfirmedAtUtc)
            .Select(x => x.AuthorizationToken)
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        if (latestToken is null)
        {
            return [];
        }

        var authorized = new HashSet<Guid>();
        for (int offset = 0; offset < rows.Count; offset += BulkCheckLimit)
        {
            PermissionCheck[] checks = rows.Skip(offset).Take(BulkCheckLimit).Select(file => new PermissionCheck(
                userId,
                AuthorizationObjects.For(new FileId(file.Id)),
                AuthorizationPermission.View,
                AuthorizationConsistency.AtLeastAsFresh(new AuthorizationToken(latestToken)))).ToArray();
            IReadOnlyList<PermissionCheckResult> results = await permissionChecker.CheckBulkAsync(
                new BulkPermissionCheck(checks), cancellationToken).ConfigureAwait(false);
            foreach (PermissionCheckResult result in results)
            {
                if (result.IsAllowed && Guid.TryParse(result.Request.Resource.ObjectId, out Guid fileId))
                {
                    authorized.Add(fileId);
                }
            }
        }

        return rows
            .Where(file => authorized.Contains(file.Id))
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToArray();
    }
}
