using System.Diagnostics;
using System.Net;
using FSH.Framework.Core.Context;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Eventing.Outbox;
using FSH.Framework.Quota;
using FSH.Framework.Shared.Quota;
using FSH.Framework.Shared.Storage;
using FSH.Framework.Storage.Services;
using FSH.Modules.Files.Contracts;
using FSH.Modules.Files.Contracts.Events;
using FSH.Modules.Files.Contracts.v1.Commands;
using FSH.Modules.Files.Contracts.v1.DTOs;
using FSH.Modules.Files.Data;
using FSH.Modules.Files.Domain;
using FSH.Modules.Files.Features.v1.Internal;
using FSH.Modules.Files.Services;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Files.Features.v1.FinalizeUpload;

public sealed class FinalizeUploadCommandHandler(
    FilesDbContext db,
    IStorageService storage,
    IFileScanner scanner,
    IQuotaService quotas,
    FileAccessPolicyRegistry policies,
    EfCoreOutboxStore<FilesDbContext> outbox,
    TimeProvider timeProvider,
    ICurrentUser currentUser)
    : ICommandHandler<FinalizeUploadCommand, FileAssetDto>
{
    public async ValueTask<FileAssetDto> Handle(FinalizeUploadCommand cmd, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        string tenantId = currentUser.GetTenant() ?? throw new UnauthorizedException("invalid tenant");
        string userId = currentUser.GetUserId().ToString();

        FileAsset asset = await db.FileAssets
            .FirstOrDefaultAsync(f => f.Id == cmd.FileAssetId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException("file not found");

        IFileAccessPolicy policy = policies.Resolve(asset.OwnerType)
            ?? throw new ForbiddenException("no file access policy");
        var access = new FileAccessContext(
            asset.Id,
            asset.OwnerType,
            asset.OwnerId,
            asset.CreatedByUserId,
            (int)asset.Visibility,
            asset.AuthorizationToken);
        if (!await policy.CanEditAsync(access, userId, cancellationToken).ConfigureAwait(false))
        {
            throw new ForbiddenException("editor access to the pending file is required");
        }
        if (asset.Status != FileAssetStatus.PendingUpload)
        {
            throw new CustomException("file already finalized", (IEnumerable<string>?)null, HttpStatusCode.Conflict);
        }

        StoredObjectMetadata head = await storage.HeadObjectAsync(asset.StorageKey, cancellationToken).ConfigureAwait(false)
            ?? throw new CustomException("upload not received", (IEnumerable<string>?)null, HttpStatusCode.Conflict);

        if (head.SizeBytes <= 0 || head.SizeBytes > asset.SizeBytes)
        {
            await storage.RemoveAsync(asset.StorageKey, cancellationToken).ConfigureAwait(false);
            db.FileAssets.Remove(asset);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            throw new CustomException(
                $"uploaded size ({head.SizeBytes}) is invalid for declared ({asset.SizeBytes})",
                (IEnumerable<string>?)null,
                HttpStatusCode.BadRequest);
        }

        if (!string.Equals(head.ContentType, asset.ContentType, StringComparison.OrdinalIgnoreCase))
        {
            await storage.RemoveAsync(asset.StorageKey, cancellationToken).ConfigureAwait(false);
            db.FileAssets.Remove(asset);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            throw new CustomException(
                "uploaded content-type mismatch",
                (IEnumerable<string>?)null,
                HttpStatusCode.BadRequest);
        }

        ScanStatus scanResult = await scanner.ScanAsync(asset.StorageKey, cancellationToken).ConfigureAwait(false);
        asset.MarkAvailable(head.SizeBytes, scanResult);

        QuotaCheckResult quota = await quotas.CheckAndRecordAsync(tenantId, QuotaResource.StorageBytes, head.SizeBytes, cancellationToken).ConfigureAwait(false);
        if (!quota.Allowed)
        {
            throw new CustomException(
                "storage quota exceeded by uploaded size",
                (IEnumerable<string>?)null,
                HttpStatusCode.TooManyRequests);
        }

        string correlationId = Activity.Current?.Id ?? Guid.NewGuid().ToString();
        DateTime occurredOnUtc = timeProvider.GetUtcNow().UtcDateTime;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await outbox.AddAsync(new FileFinalizedIntegrationEvent(
                Id: Guid.CreateVersion7(),
                OccurredOnUtc: occurredOnUtc,
                TenantId: tenantId,
                CorrelationId: correlationId,
                Source: "Files",
                FileAssetId: asset.Id,
                OwnerType: asset.OwnerType,
                OwnerId: asset.OwnerId,
                ContentType: asset.ContentType,
                SizeBytes: asset.SizeBytes,
                FinalStatus: (int)asset.Status), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return FileAssetMapper.ToDto(asset);
    }
}
