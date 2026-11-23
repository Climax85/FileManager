namespace FSH.Modules.Authorization.Contracts;

public enum AuthorizationObjectType
{
    User = 1,
    Organization = 2,
    Group = 3,
    OrganizationalUnit = 4,
    Folder = 5,
    File = 6,
}

public enum AuthorizationRelation
{
    Admin = 1,
    Member = 2,
    DirectMember = 3,
    ChildOf = 4,
    Parent = 5,
    Organization = 6,
    Folder = 7,
    Viewer = 8,
    Editor = 9,
    Manager = 10,
    ViewerUnit = 11,
    EditorUnit = 12,
}

public enum AuthorizationPermission
{
    View = 1,
    Download = 2,
    Edit = 3,
    Manage = 4,
}

public static class AuthorizationSchema
{
    public const string CurrentVersion = "v1";

    public static string Definition(AuthorizationObjectType type) => type switch
    {
        AuthorizationObjectType.User => "user",
        AuthorizationObjectType.Organization => "organization",
        AuthorizationObjectType.Group => "group",
        AuthorizationObjectType.OrganizationalUnit => "organizational_unit",
        AuthorizationObjectType.Folder => "folder",
        AuthorizationObjectType.File => "file",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    public static string Relation(AuthorizationRelation relation) => relation switch
    {
        AuthorizationRelation.Admin => "admin",
        AuthorizationRelation.Member => "member",
        AuthorizationRelation.DirectMember => "direct_member",
        AuthorizationRelation.ChildOf => "child_of",
        AuthorizationRelation.Parent => "parent",
        AuthorizationRelation.Organization => "organization",
        AuthorizationRelation.Folder => "folder",
        AuthorizationRelation.Viewer => "viewer",
        AuthorizationRelation.Editor => "editor",
        AuthorizationRelation.ViewerUnit => "viewer_unit",
        AuthorizationRelation.EditorUnit => "editor_unit",
        AuthorizationRelation.Manager => "manager",
        _ => throw new ArgumentOutOfRangeException(nameof(relation), relation, null),
    };

    public static AuthorizationObjectType ParseDefinition(string definition) => definition switch
    {
        "user" => AuthorizationObjectType.User,
        "organization" => AuthorizationObjectType.Organization,
        "group" => AuthorizationObjectType.Group,
        "organizational_unit" => AuthorizationObjectType.OrganizationalUnit,
        "folder" => AuthorizationObjectType.Folder,
        "file" => AuthorizationObjectType.File,
        _ => throw new ArgumentException("Unknown authorization definition.", nameof(definition)),
    };

    public static AuthorizationRelation ParseRelation(string relation) => relation switch
    {
        "admin" => AuthorizationRelation.Admin,
        "member" => AuthorizationRelation.Member,
        "direct_member" => AuthorizationRelation.DirectMember,
        "child_of" => AuthorizationRelation.ChildOf,
        "parent" => AuthorizationRelation.Parent,
        "organization" => AuthorizationRelation.Organization,
        "folder" => AuthorizationRelation.Folder,
        "viewer" => AuthorizationRelation.Viewer,
        "editor" => AuthorizationRelation.Editor,
        "viewer_unit" => AuthorizationRelation.ViewerUnit,
        "editor_unit" => AuthorizationRelation.EditorUnit,
        "manager" => AuthorizationRelation.Manager,
        _ => throw new ArgumentException("Unknown authorization relation.", nameof(relation)),
    };

    public static string Permission(AuthorizationPermission permission) => permission switch
    {
        AuthorizationPermission.View => "view",
        AuthorizationPermission.Download => "download",
        AuthorizationPermission.Edit => "edit",
        AuthorizationPermission.Manage => "manage",
        _ => throw new ArgumentOutOfRangeException(nameof(permission), permission, null),
    };
}
