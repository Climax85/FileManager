using System.Globalization;

namespace FSH.Modules.Authorization.Contracts;

public readonly record struct UserId
{
    public UserId(string value) => Value = CanonicalIds.String(value);
    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct OrganizationId
{
    public OrganizationId(string value) => Value = CanonicalIds.String(value);
    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct GroupId
{
    public GroupId(Guid value) => Value = CanonicalIds.Guid(value);
    public Guid Value { get; }
    public override string ToString() => CanonicalIds.GuidString(Value);
}

public readonly record struct OrganizationalUnitId
{
    public OrganizationalUnitId(Guid value) => Value = CanonicalIds.Guid(value);
    public Guid Value { get; }
    public override string ToString() => CanonicalIds.GuidString(Value);
}

public readonly record struct FolderId
{
    public FolderId(Guid value) => Value = CanonicalIds.Guid(value);
    public Guid Value { get; }
    public static FolderId New() => new(Guid.CreateVersion7());
    public override string ToString() => CanonicalIds.GuidString(Value);
}

public readonly record struct FileId
{
    public FileId(Guid value) => Value = CanonicalIds.Guid(value);
    public Guid Value { get; }
    public static FileId New() => new(Guid.CreateVersion7());
    public override string ToString() => CanonicalIds.GuidString(Value);
}

internal static class CanonicalIds
{
    public static string String(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string canonical = value.Trim();
        if (canonical.IndexOfAny([':', '#', '@', '*']) >= 0)
        {
            throw new ArgumentException("Authorization IDs cannot contain type, relation, subject, or wildcard delimiters.", nameof(value));
        }

        return canonical;
    }

    public static Guid Guid(Guid value) => value == System.Guid.Empty
        ? throw new ArgumentException("Authorization IDs cannot be empty.", nameof(value))
        : value;

    public static string GuidString(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);
}
