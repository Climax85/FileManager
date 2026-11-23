namespace FSH.Modules.Authorization.Domain;

using FSH.Modules.Authorization.Contracts;

public sealed class OrganizationProjection
{
    private OrganizationProjection() { }

    public OrganizationProjection(OrganizationId id, DateTimeOffset createdAt)
    {
        Id = id;
        CreatedAt = createdAt;
    }

    public OrganizationId Id { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}

public sealed class Folder
{
    private Folder() { }

    public Folder(FolderId id, OrganizationId organizationId, string name, bool isRoot, DateTimeOffset createdAt, Guid? synchronizationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Id = id;
        OrganizationId = organizationId;
        Name = name.Trim();
        IsRoot = isRoot;
        CreatedAt = createdAt;
        SynchronizationId = synchronizationId;
    }

    public FolderId Id { get; private set; }
    public OrganizationId OrganizationId { get; private set; }
    public string Name { get; private set; } = default!;
    public bool IsRoot { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? SynchronizationId { get; private set; }

    public static Folder CreateRoot(OrganizationId organizationId, DateTimeOffset createdAt, Guid synchronizationId) =>
        new(FolderId.New(), organizationId, $"{organizationId.Value} files", true, createdAt, synchronizationId);
}

public sealed class RelationshipSynchronization
{
    private RelationshipSynchronization() { }

    private RelationshipSynchronization(Guid operationId, OrganizationId organizationId)
    {
        OperationId = operationId;
        OrganizationId = organizationId;
        Status = RelationshipSynchronizationStatus.Pending;
    }

    public Guid OperationId { get; private set; }
    public OrganizationId OrganizationId { get; private set; }
    public RelationshipSynchronizationStatus Status { get; private set; }
    public string? ZedToken { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset? ConfirmedAt { get; private set; }

    public static RelationshipSynchronization Pending(Guid operationId, OrganizationId organizationId) =>
        new(operationId, organizationId);

    public void MarkConfirmed(string zedToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zedToken);
        ZedToken = zedToken.Trim();
        Status = RelationshipSynchronizationStatus.Confirmed;
        LastError = null;
        ConfirmedAt = TimeProvider.System.GetUtcNow();
    }

    public void MarkFailed(string error)
    {
        LastError = error;
        Status = RelationshipSynchronizationStatus.Pending;
        ConfirmedAt = null;
    }
}
