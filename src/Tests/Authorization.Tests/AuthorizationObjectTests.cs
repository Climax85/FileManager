using FSH.Modules.Authorization.Contracts;
using Shouldly;
using Xunit;

namespace Authorization.Tests;

public sealed class AuthorizationObjectTests
{
    [Fact]
    public void TypedIds_MapToProviderNeutralDefinitions_AndCanonicalObjectIds()
    {
        // Arrange
        var folderId = new FolderId(Guid.Parse("019B2A6D-7A4C-7B2D-8E3F-123456789ABC"));
        var organizationId = new OrganizationId("  acme-west  ");

        // Act
        AuthorizationObject folder = AuthorizationObjects.For(folderId);
        AuthorizationObject organization = AuthorizationObjects.For(organizationId);
        AuthorizationObject unit = AuthorizationObjects.For(new OrganizationalUnitId(Guid.Parse("019B2A6D-7A4C-7B2D-8E3F-123456789ABD")));

        // Assert
        folder.Definition.ShouldBe("folder");
        folder.ObjectId.ShouldBe("019b2a6d-7a4c-7b2d-8e3f-123456789abc");
        organization.Definition.ShouldBe("organization");
        organization.ObjectId.ShouldBe("acme-west");
        unit.Definition.ShouldBe("organizational_unit");
        unit.ObjectId.ShouldNotStartWith("organizational_unit:");
    }

    [Fact]
    public void RelationshipMutation_PreservesSubjectSetAndExpiration_WithoutProviderTypes()
    {
        // Arrange
        DateTimeOffset expiresAt = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
        AuthorizationObject folder = AuthorizationObjects.For(FolderId.New());
        AuthorizationSubject groupMembers = AuthorizationSubjects.Members(AuthorizationObjects.For(GroupId.New()));

        // Act
        RelationshipMutation mutation = RelationshipMutation.Touch(
            folder,
            AuthorizationRelation.Viewer,
            groupMembers,
            expiresAt);

        // Assert
        mutation.Operation.ShouldBe(RelationshipOperation.Touch);
        mutation.Subject.OptionalRelation.ShouldBe(AuthorizationRelation.Member);
        mutation.ExpiresAt.ShouldBe(expiresAt);
        mutation.Resource.Definition.ShouldBe("folder");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("folder:abc")]
    [InlineData("folder#manager")]
    public void StringBackedIds_RejectNonCanonicalValues(string value)
    {
        // Arrange / Act
        Action act = () => _ = new OrganizationId(value);

        // Assert
        act.ShouldThrow<ArgumentException>();
    }
}
