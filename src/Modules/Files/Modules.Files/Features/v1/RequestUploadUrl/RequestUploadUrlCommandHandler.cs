using System.Net;
using FSH.Framework.Core.Context;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Eventing.Outbox;
using FSH.Framework.Quota;
using FSH.Framework.Shared.Quota;
using FSH.Framework.Shared.Storage;
using FSH.Framework.Storage.Services;
using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Authorization.Contracts.Events;
using FSH.Modules.Files.Contracts;
using FSH.Modules.Files.Contracts.v1.Commands;
using FSH.Modules.Files.Contracts.v1.DTOs;
using FSH.Modules.Files.Data;
using FSH.Modules.Files.Domain;
using FSH.Modules.Files.Services;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FSH.Modules.Files.Features.v1.RequestUploadUrl;

public sealed class RequestUploadUrlCommandHandler(
    FilesDbContext db,
    IStorageService storage,
    FileAccessPolicyRegistry policies,
    IQuotaService quotas,
    ICurrentUser currentUser,
    IOrganizationRootProvisioner roots,
    IPermissionChecker permissionChecker,
    IRelationshipWriter relationshipWriter,
    EfCoreOutboxStore<FilesDbContext> outbox,
    TimeProvider timeProvider,
    IOptions<FilesOptions> options)
    : ICommandHandler<RequestUploadUrlCommand, PresignedUploadResponse>
{
    public async ValueTask<PresignedUploadResponse> Handle(RequestUploadUrlCommand cmd, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        string tenantId = currentUser.GetTenant() ?? throw new UnauthorizedException("invalid tenant");
        Guid userId = currentUser.GetUserId();
        if (userId == Guid.Empty)
        {
            throw new UnauthorizedException("no current user");
        }

        if (!options.Value.Categories.TryGetValue(cmd.Category, out FileCategoryOptions? category))
        {
            throw new CustomException($"Unknown category '{cmd.Category}'.", (IEnumerable<string>?)null, HttpStatusCode.BadRequest);
        }

        string extension = Path.GetExtension(cmd.FileName);
        if (string.IsNullOrWhiteSpace(extension) ||
            !category.AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new CustomException(
                $"Extension '{extension}' not allowed for category '{cmd.Category}'.",
                (IEnumerable<string>?)null,
                HttpStatusCode.BadRequest);
        }

        if (cmd.SizeBytes > category.MaxBytes)
        {
            throw new CustomException(
                $"File exceeds max size of {category.MaxBytes} bytes for category '{cmd.Category}'.",
                (IEnumerable<string>?)null,
                HttpStatusCode.BadRequest);
        }

        bool isFileManager = cmd.OwnerType is "MyFiles" or "User";
        Guid? parentFolderId = null;
        if (isFileManager)
        {
            OrganizationRoot root = await roots.EnsureRootAsync(new OrganizationId(tenantId), cancellationToken).ConfigureAwait(false);
            parentFolderId = cmd.ParentFolderId ?? root.Id.Value;
            AuthorizationConsistency consistency = parentFolderId == root.Id.Value && root.AuthorizationToken is AuthorizationToken rootToken
                ? AuthorizationConsistency.AtLeastAsFresh(rootToken)
                : AuthorizationConsistency.MinimizeLatency();
            PermissionCheckResult parentAccess = await permissionChecker.CheckAsync(new PermissionCheck(
                new UserId(userId.ToString("D")),
                AuthorizationObjects.For(new FolderId(parentFolderId.Value)),
                AuthorizationPermission.Edit,
                consistency), cancellationToken).ConfigureAwait(false);
            if (!parentAccess.IsAllowed)
            {
                throw new ForbiddenException("Editor access to the parent folder is required.");
            }
        }

        IFileAccessPolicy policy = policies.Resolve(cmd.OwnerType)
            ?? throw new ForbiddenException($"No file access policy registered for owner type '{cmd.OwnerType}'.");
        if (!await policy.CanAttachAsync(cmd.OwnerId, userId.ToString(), cancellationToken).ConfigureAwait(false))
        {
            throw new ForbiddenException("Not allowed to attach files to this owner.");
        }

        QuotaCheckResult quotaCheck = await quotas.CheckAsync(tenantId, QuotaResource.StorageBytes, cmd.SizeBytes, cancellationToken).ConfigureAwait(false);
        if (!quotaCheck.Allowed)
        {
            throw new CustomException(
                $"Storage quota exceeded ({quotaCheck.CurrentUsage}/{quotaCheck.Limit} bytes).",
                (IEnumerable<string>?)null,
                (HttpStatusCode)507);
        }

        Guid id = Guid.CreateVersion7();
        Guid? operationId = isFileManager ? Guid.CreateVersion7() : null;
        DateTimeOffset now = timeProvider.GetUtcNow();
        string storageKey = StorageKeyBuilder.Build(tenantId, cmd.OwnerType, id, cmd.FileName, now);
        TimeSpan ttl = TimeSpan.FromMinutes(options.Value.UploadUrlTtlMinutes);
        PresignedUploadUrl presigned = await storage.GenerateUploadUrlAsync(storageKey, cmd.ContentType, cmd.SizeBytes, ttl, cancellationToken).ConfigureAwait(false);
        FileAsset asset = FileAsset.CreatePending(
            id: id,
            ownerType: cmd.OwnerType,
            ownerId: cmd.OwnerId,
            originalFileName: cmd.FileName,
            sanitizedFileName: StorageKeyBuilder.Sanitize(cmd.FileName),
            contentType: cmd.ContentType,
            declaredSizeBytes: cmd.SizeBytes,
            storageKey: storageKey,
            visibility: cmd.Visibility,
            createdByUserId: userId.ToString(),
            uploadDeadline: now.Add(ttl),
            parentFolderId: parentFolderId,
            authorizationOperationId: operationId);

        IReadOnlyCollection<RelationshipMutation>? mutations = isFileManager
            ? CreatorMutations(tenantId, parentFolderId!.Value, id, userId)
            : null;
        db.FileAssets.Add(asset);
        if (operationId is Guid confirmedOperationId && mutations is not null)
        {
            await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await outbox.AddAsync(new AuthorizationRelationshipsChangedIntegrationEvent(
                    confirmedOperationId,
                    now.UtcDateTime,
                    tenantId,
                    confirmedOperationId.ToString("D"),
                    "Files",
                    mutations), cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            await ConfirmProjectionAsync(asset, mutations, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return new PresignedUploadResponse(asset.Id, presigned.Url, presigned.RequiredHeaders, presigned.ExpiresAt);
    }

    private async ValueTask ConfirmProjectionAsync(
        FileAsset asset,
        IReadOnlyCollection<RelationshipMutation> mutations,
        CancellationToken cancellationToken)
    {
        try
        {
            RelationshipConfirmation confirmation = await relationshipWriter
                .WriteAsync(asset.AuthorizationOperationId!.Value, mutations, cancellationToken)
                .ConfigureAwait(false);
            asset.MarkAuthorizationConfirmed(confirmation.WrittenAt.Value, confirmation.ConfirmedAt);
            OutboxMessage? message = await db.OutboxMessages
                .SingleOrDefaultAsync(x => x.Id == asset.AuthorizationOperationId.Value, cancellationToken)
                .ConfigureAwait(false);
            if (message is not null)
            {
                message.ProcessedOnUtc = confirmation.ConfirmedAt.UtcDateTime;
            }
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            asset.MarkAuthorizationFailed(exception.Message);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static IReadOnlyCollection<RelationshipMutation> CreatorMutations(
        string tenantId,
        Guid parentFolderId,
        Guid fileId,
        Guid creatorId)
    {
        AuthorizationObject file = AuthorizationObjects.For(new FileId(fileId));
        return
        [
            RelationshipMutation.Touch(
                file,
                AuthorizationRelation.Organization,
                new AuthorizationSubject(AuthorizationObjects.For(new OrganizationId(tenantId)))),
            RelationshipMutation.Touch(
                file,
                AuthorizationRelation.Folder,
                new AuthorizationSubject(AuthorizationObjects.For(new FolderId(parentFolderId)))),
            RelationshipMutation.Touch(
                file,
                AuthorizationRelation.Manager,
                AuthorizationSubjects.For(new UserId(creatorId.ToString("D")))),
        ];
    }
}
