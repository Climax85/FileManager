namespace FSH.Modules.Authorization.Contracts;

public sealed record AuthorizationObject
{
    public AuthorizationObject(AuthorizationObjectType type, string objectId)
    {
        Type = type;
        Definition = AuthorizationSchema.Definition(type);
        ObjectId = CanonicalIds.String(objectId);
    }

    public AuthorizationObjectType Type { get; }
    public string Definition { get; }
    public string ObjectId { get; }
}

public static class AuthorizationObjects
{
    public static AuthorizationObject For(UserId id) => new(AuthorizationObjectType.User, id.Value);
    public static AuthorizationObject For(OrganizationId id) => new(AuthorizationObjectType.Organization, id.Value);
    public static AuthorizationObject For(GroupId id) => new(AuthorizationObjectType.Group, id.ToString());
    public static AuthorizationObject For(OrganizationalUnitId id) => new(AuthorizationObjectType.OrganizationalUnit, id.ToString());
    public static AuthorizationObject For(FolderId id) => new(AuthorizationObjectType.Folder, id.ToString());
    public static AuthorizationObject For(FileId id) => new(AuthorizationObjectType.File, id.ToString());
}

public sealed record AuthorizationSubject
{
    public AuthorizationSubject(AuthorizationObject @object, AuthorizationRelation? optionalRelation = null)
    {
        ArgumentNullException.ThrowIfNull(@object);
        if (optionalRelation is not null && optionalRelation is not AuthorizationRelation.Member)
        {
            throw new ArgumentException("Only member subject sets are valid sharing subjects.", nameof(optionalRelation));
        }

        Object = @object;
        OptionalRelation = optionalRelation;
    }

    public AuthorizationObject Object { get; }
    public AuthorizationRelation? OptionalRelation { get; }
}

public static class AuthorizationSubjects
{
    public static AuthorizationSubject For(UserId id) => new(AuthorizationObjects.For(id));

    public static AuthorizationSubject For(OrganizationalUnitId id) => new(AuthorizationObjects.For(id));

    public static AuthorizationSubject Members(AuthorizationObject subjectSet)
    {
        ArgumentNullException.ThrowIfNull(subjectSet);
        if (subjectSet.Type is not (AuthorizationObjectType.Group or AuthorizationObjectType.Organization))
        {
            throw new ArgumentException("Only groups and organizations have member subject sets; organizational units use unit grant relations.", nameof(subjectSet));
        }

        return new AuthorizationSubject(subjectSet, AuthorizationRelation.Member);
    }
}
