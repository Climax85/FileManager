using FSH.Framework.Core.Exceptions;
using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Authorization.Data;
using FSH.Modules.Authorization.Domain;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Authorization.Services;

public sealed class FolderHierarchy(AuthorizationDbContext db) : IFolderHierarchy
{
    public async ValueTask<FolderDescriptor?> FindAsync(
        FolderId folderId,
        OrganizationId organizationId,
        CancellationToken cancellationToken = default)
    {
        Folder? folder = await db.Folders.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == folderId && x.OrganizationId == organizationId, cancellationToken)
            .ConfigureAwait(false);
        return folder is null ? null : Descriptor(folder);
    }

    public async ValueTask<IReadOnlyList<FolderDescriptor>> GetPathAsync(
        FolderId folderId,
        OrganizationId organizationId,
        CancellationToken cancellationToken = default)
    {
        FolderDescriptor current = await FindAsync(folderId, organizationId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("folder not found");
        var path = new List<FolderDescriptor>();
        var visited = new HashSet<FolderId>();
        while (true)
        {
            if (!visited.Add(current.Id))
            {
                throw new InvalidOperationException("The folder hierarchy contains a cycle.");
            }
            path.Add(current);
            if (current.ParentId is not FolderId parentId) break;
            current = await FindAsync(parentId, organizationId, cancellationToken).ConfigureAwait(false)
                ?? throw new NotFoundException("folder parent not found");
        }
        path.Reverse();
        return path;
    }

    public async ValueTask EnsureValidParentAsync(
        OrganizationId organizationId,
        FolderId parentId,
        FolderId? childId,
        CancellationToken cancellationToken = default)
    {
        FolderDescriptor parent = await FindAsync(parentId, organizationId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("folder parent not found");
        if (childId is null) return;
        if (parent.Id == childId.Value)
        {
            throw new InvalidOperationException("A folder cannot be its own parent.");
        }

        FolderDescriptor child = await FindAsync(childId.Value, organizationId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("folder not found");
        if (child.IsRoot)
        {
            throw new InvalidOperationException("The organization root cannot have a parent.");
        }

        var visited = new HashSet<FolderId>();
        FolderDescriptor current = parent;
        while (true)
        {
            if (current.Id == childId.Value)
            {
                throw new InvalidOperationException("A folder cannot be moved below itself or one of its descendants.");
            }
            if (!visited.Add(current.Id))
            {
                throw new InvalidOperationException("The folder hierarchy contains a cycle.");
            }
            if (current.ParentId is not FolderId ancestorId) break;
            current = await FindAsync(ancestorId, organizationId, cancellationToken).ConfigureAwait(false)
                ?? throw new NotFoundException("folder parent not found");
        }
    }

    private static FolderDescriptor Descriptor(Folder folder) => new(
        folder.Id, folder.OrganizationId, folder.ParentId, folder.Name, folder.IsRoot, folder.CreatedAt);
}
