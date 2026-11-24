using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Authorization.Domain;
using Shouldly;
using Xunit;

namespace Authorization.Tests;

public sealed class FolderTests
{
    private static readonly OrganizationId Organization = new("acme");
    private static readonly FolderId RootId = FolderId.New();

    [Fact]
    public void NonRootFolder_HasExactlyOneParent_AndRootHasNone()
    {
        var root = Folder.CreateRoot(Organization, DateTimeOffset.UtcNow, Guid.CreateVersion7());
        var child = Folder.Create(FolderId.New(), Organization, RootId, "  Projects  ", DateTimeOffset.UtcNow);

        root.ParentId.ShouldBeNull();
        child.ParentId.ShouldBe(RootId);
        child.IsRoot.ShouldBeFalse();
        child.Name.ShouldBe("Projects");
    }

    [Fact]
    public void Root_CannotBeGivenAParent()
    {
        var root = Folder.CreateRoot(Organization, DateTimeOffset.UtcNow, Guid.CreateVersion7());
        var parent = Folder.Create(FolderId.New(), Organization, RootId, "Parent", DateTimeOffset.UtcNow);

        Action act = () => root.ChangeParent(parent);

        act.ShouldThrow<InvalidOperationException>();
    }

    [Fact]
    public void Parent_MustBelongToTheSameOrganization()
    {
        var parent = Folder.Create(FolderId.New(), Organization, RootId, "Parent", DateTimeOffset.UtcNow);
        var child = Folder.Create(FolderId.New(), Organization, parent.Id, "Child", DateTimeOffset.UtcNow);
        var otherOrganizationParent = Folder.Create(
            FolderId.New(), new OrganizationId("other"), FolderId.New(), "Other", DateTimeOffset.UtcNow);

        Action act = () => child.ChangeParent(otherOrganizationParent);

        act.ShouldThrow<InvalidOperationException>();
        child.ParentId.ShouldBe(parent.Id);
    }

    [Fact]
    public void Folder_CannotBeItsOwnParent()
    {
        var folder = Folder.Create(FolderId.New(), Organization, RootId, "Folder", DateTimeOffset.UtcNow);

        Action act = () => folder.ChangeParent(folder);

        act.ShouldThrow<InvalidOperationException>();
        folder.ParentId.ShouldBe(RootId);
    }

    [Fact]
    public void Rename_TrimsAndValidatesTheDisplayName()
    {
        var folder = Folder.Create(FolderId.New(), Organization, RootId, "Before", DateTimeOffset.UtcNow);

        folder.Rename("  After  ");

        folder.Name.ShouldBe("After");
        Action invalidRename = () => folder.Rename(" ");
        invalidRename.ShouldThrow<ArgumentException>();
    }
}
