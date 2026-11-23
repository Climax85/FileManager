using System.Globalization;
using System.Net;
using FSH.Framework.Core.Context;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Storage.Services;
using FSH.Modules.Auditing.Contracts;
using FSH.Modules.Files.Contracts;
using FSH.Modules.Files.Contracts.v1.DTOs;
using FSH.Modules.Files.Contracts.v1.Queries;
using FSH.Modules.Files.Data;
using FSH.Modules.Files.Domain;
using FSH.Modules.Files.Services;
using Mediator;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FSH.Modules.Files.Features.v1.GetFileDownloadUrl;

public sealed class GetFileDownloadUrlQueryHandler(
    FilesDbContext db,
    IStorageService storage,
    FileAccessPolicyRegistry policies,
    ICurrentUser currentUser,
    ISecurityAudit securityAudit,
    IOptions<FilesOptions> options)
    : IQueryHandler<GetFileDownloadUrlQuery, PresignedDownloadResponse>
{
    private const int MaxDownloadUrlTtlMinutes = 5;

    public async ValueTask<PresignedDownloadResponse> Handle(GetFileDownloadUrlQuery q, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(q);
        var userId = currentUser.GetUserId().ToString();
        var f = await db.FileAssets.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == q.FileAssetId, cancellationToken)
            .ConfigureAwait(false);
        if (f is null)
        {
            await AuditDenialAsync(userId, q.FileAssetId, "NotFound", cancellationToken).ConfigureAwait(false);
            throw new NotFoundException("file not found");
        }

        var policy = policies.Resolve(f.OwnerType);
        if (policy is null)
        {
            await AuditDenialAsync(userId, f.Id, "NoAccessPolicy", cancellationToken).ConfigureAwait(false);
            throw new NotFoundException("file not found");
        }

        var ctx = new FileAccessContext(f.Id, f.OwnerType, f.OwnerId, f.CreatedByUserId, (int)f.Visibility);
        if (!await policy.CanDownloadAsync(ctx, userId, cancellationToken).ConfigureAwait(false))
        {
            await AuditDenialAsync(userId, f.Id, "NotAuthorized", cancellationToken).ConfigureAwait(false);
            throw new NotFoundException("file not found");
        }

        if (f.Status != FileAssetStatus.Available || f.ScanStatus != ScanStatus.Clean)
        {
            await AuditDenialAsync(userId, f.Id, "NotAvailableAndScanned", cancellationToken).ConfigureAwait(false);
            throw new NotFoundException("file not found");
        }

        int ttlMinutes = Math.Clamp(options.Value.DownloadUrlTtlMinutes, 1, MaxDownloadUrlTtlMinutes);
        var ttl = TimeSpan.FromMinutes(ttlMinutes);
        var expiresAt = DateTimeOffset.UtcNow.Add(ttl);
        var mode = q.Inline ? "inline" : "attachment";
        var disposition = $"{mode}; filename=\"{f.OriginalFileName}\"";
        var url = await storage.GenerateDownloadUrlAsync(f.StorageKey, ttl, disposition, cancellationToken).ConfigureAwait(false);
        if (!IsShortLivedAuthorizedUrl(url, ttl))
        {
            await AuditDenialAsync(userId, f.Id, "UnsignedStorageUrl", cancellationToken).ConfigureAwait(false);
            throw new CustomException(
                "private download URL unavailable",
                (IEnumerable<string>?)null,
                HttpStatusCode.ServiceUnavailable);
        }

        await securityAudit.FileDownloadIssuedAsync(userId, f.Id, expiresAt, cancellationToken).ConfigureAwait(false);
        return new PresignedDownloadResponse(url, expiresAt);
    }

    private ValueTask AuditDenialAsync(
        string userId,
        Guid fileAssetId,
        string reason,
        CancellationToken cancellationToken) =>
        securityAudit.FileDownloadDeniedAsync(userId, fileAssetId, reason, cancellationToken);

    private static bool IsShortLivedAuthorizedUrl(Uri url, TimeSpan ttl)
    {
        if (!url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            return false;
        }

        var query = QueryHelpers.ParseQuery(url.Query);
        if (string.IsNullOrWhiteSpace(query["X-Amz-Signature"].ToString()))
        {
            return false;
        }

        return long.TryParse(query["X-Amz-Expires"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)
               && seconds > 0
               && seconds <= (long)ttl.TotalSeconds;
    }
}
