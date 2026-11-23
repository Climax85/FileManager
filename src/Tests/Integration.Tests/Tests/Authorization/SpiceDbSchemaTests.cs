using FSH.Modules.Authorization.Contracts;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.Authorization;

[Collection(FshCollectionDefinition.Name)]
public sealed class SpiceDbSchemaTests(FshWebApplicationFactory factory)
{
    [Fact]
    public async Task RootAdministrator_HasReadYourWritesAccess_AndOrdinaryAndCrossOrganizationUsersDoNot()
    {
        // Arrange
        using var scope = factory.Services.CreateScope();
        await AuthorizationTestData.UseRootTenantAsync(scope);
        var provisioner = scope.ServiceProvider.GetRequiredService<IOrganizationRootProvisioner>();
        var checker = scope.ServiceProvider.GetRequiredService<IPermissionChecker>();
        var profiles = scope.ServiceProvider.GetRequiredService<FSH.Modules.Identity.Contracts.Services.IUserProfileService>();
        OrganizationId organization = new(TestConstants.RootTenantId);
        OrganizationRoot root = await provisioner.EnsureRootAsync(organization, CancellationToken.None);
        var users = await profiles.GetListAsync(CancellationToken.None);
        UserId administrator = new(users.Single(user => user.Email == TestConstants.RootAdminEmail).Id);
        UserId ordinary = new(Guid.NewGuid().ToString());
        AuthorizationConsistency fresh = AuthorizationConsistency.AtLeastAsFresh(root.AuthorizationToken!.Value);

        // Act
        PermissionCheckResult adminResult = await checker.CheckAsync(new PermissionCheck(
            administrator,
            AuthorizationObjects.For(root.Id),
            AuthorizationPermission.Manage,
            fresh), CancellationToken.None);
        PermissionCheckResult ordinaryResult = await checker.CheckAsync(new PermissionCheck(
            ordinary,
            AuthorizationObjects.For(root.Id),
            AuthorizationPermission.View,
            fresh), CancellationToken.None);
        PermissionCheckResult crossOrganizationResult = await checker.CheckAsync(new PermissionCheck(
            administrator,
            AuthorizationObjects.For(FolderId.New()),
            AuthorizationPermission.View,
            fresh), CancellationToken.None);

        // Assert
        adminResult.IsAllowed.ShouldBeTrue();
        adminResult.CheckedAt.ShouldNotBeNull();
        ordinaryResult.IsAllowed.ShouldBeFalse();
        crossOrganizationResult.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task RelationshipTouch_CanBeConfirmedAndReplayed_WithoutDuplicates()
    {
        // Arrange
        using var scope = factory.Services.CreateScope();
        await AuthorizationTestData.UseRootTenantAsync(scope);
        var provisioner = scope.ServiceProvider.GetRequiredService<IOrganizationRootProvisioner>();
        var writer = scope.ServiceProvider.GetRequiredService<IRelationshipWriter>();
        var reader = scope.ServiceProvider.GetRequiredService<IRelationshipReader>();
        var profiles = scope.ServiceProvider.GetRequiredService<FSH.Modules.Identity.Contracts.Services.IUserProfileService>();
        OrganizationRoot root = await provisioner.EnsureRootAsync(new OrganizationId(TestConstants.RootTenantId), CancellationToken.None);
        UserId administrator = new((await profiles.GetListAsync(CancellationToken.None)).Single(user => user.Email == TestConstants.RootAdminEmail).Id);
        RelationshipMutation mutation = RelationshipMutation.Touch(
            AuthorizationObjects.For(root.Id),
            AuthorizationRelation.Manager,
            AuthorizationSubjects.For(administrator));
        Guid operationId = Guid.CreateVersion7();

        // Act
        RelationshipConfirmation first = await writer.WriteAsync(operationId, [mutation], CancellationToken.None);
        RelationshipConfirmation replay = await writer.WriteAsync(operationId, [mutation], CancellationToken.None);
        AuthorizationRelationshipPage stored = await reader.ReadAsync(new RelationshipQuery(
            AuthorizationObjectType.Folder,
            root.Id.ToString(),
            AuthorizationRelation.Manager,
            AuthorizationSubjects.For(administrator),
            Consistency: AuthorizationConsistency.AtLeastAsFresh(replay.WrittenAt)), CancellationToken.None);

        // Assert
        first.WrittenAt.Value.ShouldNotBeNullOrWhiteSpace();
        replay.WrittenAt.Value.ShouldNotBeNullOrWhiteSpace();
        stored.Relationships.Count.ShouldBe(1);
        stored.Relationships.Single().Resource.ObjectId.ShouldBe(root.Id.ToString());
    }
}
