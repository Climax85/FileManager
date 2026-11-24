using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Files.Contracts;
using FSH.Modules.Identity.Contracts.Services;

namespace FSH.Modules.Files.Authorization;

/// <summary>
/// Authorizes built-in File Manager owner types exclusively through active organization
/// membership and the confirmed resource graph. Uploader identity and visibility never grant access.
/// </summary>
internal sealed class FileManagerFileAccessPolicy(
    string ownerType,
    IPermissionChecker permissionChecker,
    IOrganizationMembershipReader membershipReader) : IFileAccessPolicy
{
    public string OwnerType { get; } = !string.IsNullOrWhiteSpace(ownerType)
        ? ownerType
        : throw new ArgumentException("An owner type is required.", nameof(ownerType));

    public async Task<bool> CanAttachAsync(
        Guid? ownerId,
        string currentUserId,
        CancellationToken cancellationToken) =>
        !string.IsNullOrWhiteSpace(currentUserId)
        && await membershipReader.IsActiveMemberAsync(new UserId(currentUserId), cancellationToken).ConfigureAwait(false);

    public Task<bool> CanReadAsync(
        FileAccessContext context,
        string currentUserId,
        CancellationToken cancellationToken) =>
        AuthorizeAsync(context, currentUserId, AuthorizationPermission.View, cancellationToken);

    public Task<bool> CanDownloadAsync(
        FileAccessContext context,
        string currentUserId,
        CancellationToken cancellationToken) =>
        AuthorizeAsync(context, currentUserId, AuthorizationPermission.Download, cancellationToken);
    public Task<bool> CanEditAsync(
        FileAccessContext context,
        string currentUserId,
        CancellationToken cancellationToken) =>
        AuthorizeAsync(context, currentUserId, AuthorizationPermission.Edit, cancellationToken);


    public Task<bool> CanDeleteAsync(
        FileAccessContext context,
        string currentUserId,
        CancellationToken cancellationToken) =>
        AuthorizeAsync(context, currentUserId, AuthorizationPermission.Manage, cancellationToken);

    private async Task<bool> AuthorizeAsync(
        FileAccessContext context,
        string currentUserId,
        AuthorizationPermission permission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(currentUserId) || string.IsNullOrWhiteSpace(context.AuthorizationToken))
        {
            return false;
        }

        UserId userId = new(currentUserId);
        if (!await membershipReader.IsActiveMemberAsync(userId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        PermissionCheckResult result = await permissionChecker.CheckAsync(new PermissionCheck(
            userId,
            AuthorizationObjects.For(new FileId(context.FileAssetId)),
            permission,
            AuthorizationConsistency.AtLeastAsFresh(new AuthorizationToken(context.AuthorizationToken))),
            cancellationToken).ConfigureAwait(false);
        return result.IsAllowed;
    }
}
