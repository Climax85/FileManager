namespace FSH.Modules.Authorization.Domain;

using FSH.Modules.Authorization.Contracts;

public sealed class ResourceGrant
{
    private ResourceGrant() { }

    private ResourceGrant(
        Guid id,
        Guid operationId,
        OrganizationId organizationId,
        AuthorizationObject resource,
        AuthorizationSubject subject,
        AuthorizationRelation relation,
        DateTimeOffset createdAt)
    {
        if (resource.Type is not (AuthorizationObjectType.Folder or AuthorizationObjectType.File))
        {
            throw new ArgumentException("Only files and folders can receive direct grants.", nameof(resource));
        }
        if (relation is not (AuthorizationRelation.Viewer or AuthorizationRelation.Editor or AuthorizationRelation.Manager))
        {
            throw new ArgumentException("Only viewer, editor, and manager relations are shareable.", nameof(relation));
        }
        if (!Guid.TryParse(resource.ObjectId, out Guid resourceId))
        {
            throw new ArgumentException("A shareable resource must have a GUID identifier.", nameof(resource));
        }

        Id = id;
        OperationId = operationId;
        OrganizationId = organizationId;
        ResourceType = resource.Type;
        ResourceId = resourceId;
        SubjectType = subject.Object.Type;
        SubjectId = subject.Object.ObjectId;
        SubjectRelation = subject.OptionalRelation;
        Relation = relation;
        CreatedAt = createdAt;
        Status = ResourceGrantStatus.Pending;
    }

    public Guid Id { get; private set; }
    public Guid OperationId { get; private set; }
    public OrganizationId OrganizationId { get; private set; }
    public AuthorizationObjectType ResourceType { get; private set; }
    public Guid ResourceId { get; private set; }
    public AuthorizationObjectType SubjectType { get; private set; }
    public string SubjectId { get; private set; } = default!;
    public AuthorizationRelation? SubjectRelation { get; private set; }
    public AuthorizationRelation Relation { get; private set; }
    public ResourceGrantStatus Status { get; private set; }
    public string? ZedToken { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ConfirmedAt { get; private set; }
    public bool IsEffective => Status == ResourceGrantStatus.Active;

    public static ResourceGrant CreatePending(
        Guid id,
        Guid operationId,
        OrganizationId organizationId,
        AuthorizationObject resource,
        AuthorizationSubject subject,
        AuthorizationRelation relation,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(subject);
        return new ResourceGrant(id, operationId, organizationId, resource, subject, relation, createdAt);
    }

    public AuthorizationObject Resource() => new(ResourceType, ResourceId.ToString("D"));

    public AuthorizationSubject Subject() => new(
        new AuthorizationObject(SubjectType, SubjectId), SubjectRelation);

    public RelationshipMutation GrantMutation() => RelationshipMutation.Touch(Resource(), Relation, Subject());

    public RelationshipMutation RevocationMutation() => RelationshipMutation.Delete(Resource(), Relation, Subject());

    public void MarkConfirmed(string zedToken, DateTimeOffset confirmedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zedToken);
        if (Status == ResourceGrantStatus.Active) return;
        if (Status != ResourceGrantStatus.Pending)
        {
            throw new InvalidOperationException("Only a pending grant can be confirmed as active.");
        }
        ZedToken = zedToken.Trim();
        Status = ResourceGrantStatus.Active;
        ConfirmedAt = confirmedAt;
        LastError = null;
    }

    public void BeginRevocation(Guid operationId, DateTimeOffset changedAt)
    {
        if (Status is ResourceGrantStatus.Revoked or ResourceGrantStatus.RevocationPending)
        {
            throw new InvalidOperationException("The grant is already revoked or revocation is pending.");
        }
        OperationId = operationId;
        Status = ResourceGrantStatus.RevocationPending;
        ConfirmedAt = changedAt;
        LastError = null;
    }

    public void MarkRevoked(string zedToken, DateTimeOffset confirmedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zedToken);
        if (Status == ResourceGrantStatus.Revoked) return;
        if (Status != ResourceGrantStatus.RevocationPending)
        {
            throw new InvalidOperationException("Only a pending revocation can be confirmed.");
        }
        ZedToken = zedToken.Trim();
        Status = ResourceGrantStatus.Revoked;
        ConfirmedAt = confirmedAt;
        LastError = null;
    }

    public void MarkSynchronizationFailed(string error)
    {
        LastError = error;
        if (Status != ResourceGrantStatus.RevocationPending) Status = ResourceGrantStatus.Pending;
    }
}
