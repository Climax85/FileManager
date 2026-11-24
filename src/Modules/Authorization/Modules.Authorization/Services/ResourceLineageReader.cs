using FSH.Modules.Authorization.Contracts;

namespace FSH.Modules.Authorization.Services;

public sealed class ResourceLineageReader(
    Infrastructure.AuthzedAuthorizationClient authorizationClient,
    IFolderHierarchy folderHierarchy)
{
    public async ValueTask<IReadOnlyList<AuthorizationObject>> ReadAsync(
        AuthorizationObject resource,
        OrganizationId organizationId,
        AuthorizationToken? token,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var lineage = new List<AuthorizationObject> { resource };
        FolderId folderId;
        if (resource.Type == AuthorizationObjectType.File)
        {
            AuthorizationRelationshipPage parents = await authorizationClient.ReadAsync(new RelationshipQuery(
                AuthorizationObjectType.File,
                resource.ObjectId,
                AuthorizationRelation.Folder,
                Consistency: token is null
                    ? AuthorizationConsistency.MinimizeLatency()
                    : AuthorizationConsistency.AtLeastAsFresh(token.Value)), cancellationToken).ConfigureAwait(false);
            AuthorizationRelationship? parent = parents.Relationships.FirstOrDefault();
            if (parent is null || !Guid.TryParse(parent.Subject.Object.ObjectId, out Guid parentId)) return lineage;
            folderId = new FolderId(parentId);
        }
        else
        {
            folderId = new FolderId(Guid.Parse(resource.ObjectId));
        }

        IReadOnlyList<FolderDescriptor> path = await folderHierarchy
            .GetPathAsync(folderId, organizationId, cancellationToken).ConfigureAwait(false);
        foreach (FolderDescriptor folder in path)
        {
            if (lineage.Any(x => x.Type == AuthorizationObjectType.Folder && x.ObjectId == folder.Id.ToString())) continue;
            lineage.Add(AuthorizationObjects.For(folder.Id));
        }
        return lineage;
    }
}
