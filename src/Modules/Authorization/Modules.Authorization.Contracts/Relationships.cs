namespace FSH.Modules.Authorization.Contracts;

public enum RelationshipOperation
{
    Touch = 1,
    Delete = 2,
}

public sealed record RelationshipMutation
{
    public RelationshipMutation(
        RelationshipOperation operation,
        AuthorizationObject resource,
        AuthorizationRelation relation,
        AuthorizationSubject subject,
        DateTimeOffset? expiresAt)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(subject);
        if (expiresAt is not null && relation is not (AuthorizationRelation.Viewer or AuthorizationRelation.Editor or AuthorizationRelation.ViewerUnit or AuthorizationRelation.EditorUnit))
        {
            throw new ArgumentException("Only viewer and editor relationships can expire.", nameof(expiresAt));
        }

        Operation = operation;
        Resource = resource;
        Relation = relation;
        Subject = subject;
        ExpiresAt = expiresAt;
    }
    public AuthorizationObject Resource { get; }
    public AuthorizationRelation Relation { get; }
    public AuthorizationSubject Subject { get; }
    public DateTimeOffset? ExpiresAt { get; }

    public static RelationshipMutation Touch(
        AuthorizationObject resource,
        AuthorizationRelation relation,
        AuthorizationSubject subject,
        DateTimeOffset? expiresAt = null)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(subject);
        if (expiresAt is not null && relation is not (AuthorizationRelation.Viewer or AuthorizationRelation.Editor or AuthorizationRelation.ViewerUnit or AuthorizationRelation.EditorUnit))
        {
            throw new ArgumentException("Only viewer and editor relationships can expire.", nameof(expiresAt));
        }

        return new RelationshipMutation(RelationshipOperation.Touch, resource, relation, subject, expiresAt);
    }

    public static RelationshipMutation Delete(
        AuthorizationObject resource,
        AuthorizationRelation relation,
        AuthorizationSubject subject)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(subject);
        return new RelationshipMutation(RelationshipOperation.Delete, resource, relation, subject, null);
    }
}

public sealed record RelationshipConfirmation(
    Guid OperationId,
    AuthorizationToken WrittenAt,
    DateTimeOffset ConfirmedAt);

public readonly record struct AuthorizationToken
{
    public AuthorizationToken(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Trim();
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct AuthorizationCursor
{
    public AuthorizationCursor(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }
}
