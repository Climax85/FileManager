namespace FSH.Modules.Files.Contracts;

/// <summary>
/// Per-OwnerType authorization for FileAssets. Each owning module (Catalog, Tickets, ...) registers
/// its own implementation via <c>services.AddFileAccessPolicy&lt;TPolicy&gt;()</c>. Built-in File Manager
/// owner types use active organization membership and provider-neutral resource authorization.
/// Tenant scoping is enforced by the framework's BaseDbContext (schema-per-tenant) and is NOT
/// delegated to policies. Policies receive a primitive <c>currentUserId</c> rather than a
/// <c>ClaimsPrincipal</c> so the contract stays free of ASP.NET Core types — owning modules that
/// need richer authz can inject their own dependencies.
/// </summary>
public interface IFileAccessPolicy
{
    /// <summary>The OwnerType this policy handles. Must be unique across registered policies.</summary>
    string OwnerType { get; }

    Task<bool> CanAttachAsync(Guid? ownerId, string currentUserId, CancellationToken cancellationToken);

    Task<bool> CanReadAsync(FileAccessContext context, string currentUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the caller may receive a short-lived download credential. Defaults to the owning
    /// module's read policy so specialized Catalog and Chat behavior remains unchanged.
    /// </summary>
    Task<bool> CanDownloadAsync(FileAccessContext context, string currentUserId, CancellationToken cancellationToken)
        => CanReadAsync(context, currentUserId, cancellationToken);

    /// <summary>
    /// Whether the caller may finalize pending content. Specialized policies retain the historic
    /// uploader check; the File Manager overrides this with resource editor permission.
    /// </summary>
    Task<bool> CanEditAsync(FileAccessContext context, string currentUserId, CancellationToken cancellationToken)
        => Task.FromResult(context is not null && string.Equals(context.CreatedByUserId, currentUserId, StringComparison.Ordinal));

    Task<bool> CanDeleteAsync(FileAccessContext context, string currentUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the caller may change a file's <see cref="FileAccessContext.Visibility"/> after
    /// upload. Defaults to the same rule as <see cref="CanDeleteAsync"/> — only the uploader.
    /// Modules whose files are tied to a domain entity (Catalog product images, Chat attachments)
    /// can override to disallow visibility flips entirely.
    /// </summary>
    Task<bool> CanChangeVisibilityAsync(FileAccessContext context, string currentUserId, CancellationToken cancellationToken)
        => CanDeleteAsync(context, currentUserId, cancellationToken);
}

/// <summary>
/// Minimal projection of a FileAsset passed to <see cref="IFileAccessPolicy"/> methods so owning
/// modules can make access decisions without depending on the Files module runtime.
/// </summary>
/// <param name="FileAssetId">FileAsset identity.</param>
/// <param name="OwnerType">OwnerType value.</param>
/// <param name="OwnerId">OwnerId value, or null when the file isn't bound to an owner (e.g. MyFiles).</param>
/// <param name="CreatedByUserId">User who uploaded the file.</param>
/// <param name="Visibility">0 = Public, 1 = Private (matches the runtime enum int).</param>
/// <param name="AuthorizationToken">Optional read-your-writes token for resource authorization.</param>
public sealed record FileAccessContext(
    Guid FileAssetId,
    string OwnerType,
    Guid? OwnerId,
    string CreatedByUserId,
    int Visibility,
    string? AuthorizationToken = null);
