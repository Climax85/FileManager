using System.Collections.ObjectModel;
using FSH.Framework.Core.Context;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Files.Contracts.v1.DTOs;
using FSH.Modules.Files.Contracts.v1.Queries;
using FSH.Modules.Files.Data;
using FSH.Modules.Files.Domain;
using FSH.Modules.Files.Features.v1.Internal;
using FSH.Modules.Files.Services;
using Mediator;

namespace FSH.Modules.Files.Features.v1.ListSharedFiles;

/// <summary>
/// Returns files created by someone else for which the caller has explicit or inherited
/// resource view access. Public visibility and organization membership alone expose nothing.
/// </summary>
public sealed class ListSharedFilesQueryHandler(
    FilesDbContext db,
    ICurrentUser currentUser,
    AuthorizedFileLister authorizedFiles)
    : IQueryHandler<ListSharedFilesQuery, ReadOnlyCollection<FileAssetDto>>
{
    public async ValueTask<ReadOnlyCollection<FileAssetDto>> Handle(ListSharedFilesQuery q, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(q);
        string userId = currentUser.GetUserId().ToString();
        if (string.IsNullOrEmpty(userId) || userId == Guid.Empty.ToString())
        {
            throw new UnauthorizedException("no current user");
        }

        int page = Math.Max(1, q.Page);
        int pageSize = Math.Clamp(q.PageSize, 1, 100);
        IQueryable<FileAsset> candidates = db.FileAssets.AsNoTracking()
            .Where(f => f.CreatedByUserId != userId
                && (f.OwnerType == "MyFiles" || f.OwnerType == "User")
                && f.Status == FileAssetStatus.Available
                && f.AuthorizationSynchronizationStatus == RelationshipSynchronizationStatus.Confirmed
                && f.AuthorizationToken != null)
            .OrderByDescending(f => f.CreatedAtUtc);
        IReadOnlyList<FileAsset> rows = await authorizedFiles.ReadAsync(
            candidates, userId, page, pageSize, cancellationToken).ConfigureAwait(false);

        return rows.Select(FileAssetMapper.ToDto).ToList().AsReadOnly();
    }
}
