using FSH.Framework.Core.Context;
using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Authorization.Data;
using FSH.Modules.Identity.Contracts.Services;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Authorization.Services;

/// <summary>
/// Adds durable PostgreSQL synchronization state to provider checks. Provider availability alone
/// is never sufficient: unconfirmed grants and revocations must fail closed even if SpiceDB is stale.
/// </summary>
public sealed class FailClosedPermissionChecker(
    Infrastructure.AuthzedAuthorizationClient inner,
    AuthorizationDbContext db,
    ICurrentUser currentUser,
    IOrganizationMembershipReader membershipReader,
    ResourceLineageReader lineageReader) : IPermissionChecker
{
    public async ValueTask<PermissionCheckResult> CheckAsync(
        PermissionCheck request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        PermissionCheckResult result = await inner.CheckAsync(request, cancellationToken).ConfigureAwait(false);
        return await ApplyDurableStateAsync(result, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<PermissionCheckResult>> CheckBulkAsync(
        BulkPermissionCheck request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<PermissionCheckResult> results = await inner.CheckBulkAsync(request, cancellationToken).ConfigureAwait(false);
        var guarded = new PermissionCheckResult[results.Count];
        for (int index = 0; index < results.Count; index++)
        {
            guarded[index] = await ApplyDurableStateAsync(results[index], cancellationToken).ConfigureAwait(false);
        }
        return guarded;
    }

    private async ValueTask<PermissionCheckResult> ApplyDurableStateAsync(
        PermissionCheckResult result,
        CancellationToken cancellationToken)
    {
        if (!result.IsAllowed) return result;
        if (!await membershipReader.IsActiveMemberAsync(result.Request.Subject, cancellationToken).ConfigureAwait(false))
        {
            return Deny(result);
        }

        string? tenant = currentUser.GetTenant();
        if (string.IsNullOrWhiteSpace(tenant)) return Deny(result);
        IReadOnlyList<AuthorizationObject> lineage = await lineageReader.ReadAsync(
            result.Request.Resource, new OrganizationId(tenant), result.Request.Consistency?.AtLeastAsFreshToken, cancellationToken)
            .ConfigureAwait(false);
        Guid[] resourceIds = [.. lineage.Select(x => Guid.Parse(x.ObjectId))];
        bool hasUnconfirmedMutation = await db.ResourceGrants.AsNoTracking().AnyAsync(x =>
            resourceIds.Contains(x.ResourceId) &&
            (x.Status == ResourceGrantStatus.Pending || x.Status == ResourceGrantStatus.RevocationPending) &&
            ((x.SubjectType == AuthorizationObjectType.User && x.SubjectId == result.Request.Subject.Value) ||
             (x.SubjectType == AuthorizationObjectType.Organization && x.SubjectId == tenant)), cancellationToken)
            .ConfigureAwait(false);
        return hasUnconfirmedMutation ? Deny(result) : result;
    }

    private static PermissionCheckResult Deny(PermissionCheckResult result) =>
        new(result.Request, false, result.CheckedAt, AuthorizationFailure.NoPermission);
}
