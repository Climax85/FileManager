using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Authorization.Contracts.v1.Sharing;
using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;

namespace Integration.Tests.Tests.Authorization;

[Collection(FshCollectionDefinition.Name)]
public sealed class GroupSharingTests
{
    private const string BasePath = "/api/v1/authorization";
    private readonly AuthHelper _auth;

    public GroupSharingTests(FshWebApplicationFactory factory) => _auth = new AuthHelper(factory);

    [Theory]
    [InlineData("Viewer", EffectiveAccessLevel.Viewer)]
    [InlineData("Editor", EffectiveAccessLevel.Editor)]
    [InlineData("Manager", EffectiveAccessLevel.Manager)]
    public async Task GroupGrant_BecomesEffectiveAfterConfirmedMembershipAdd(
        string relation,
        EffectiveAccessLevel expectedAccess)
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        RootResult root = await GetRootAsync(admin);
        RootResult resource = await CreateResourceAsync(admin, root);
        TestUser member = await RegisterUserAsync(admin, $"group-{relation}");
        Guid groupId = await CreateGroupAsync(admin, $"group-{relation}");
        GrantResult grant = await GrantAsync(admin, resource, groupId, relation);

        EffectiveAccessResult before = await GetEffectiveAccessAsync(admin, resource, member.Id, grant.AuthorizationToken);
        before.Access.ShouldBe(EffectiveAccessLevel.None);
        before.Sources.ShouldBeEmpty();

        await AddMemberAsync(admin, groupId, member.Id);

        EffectiveAccessResult after = await GetEffectiveAccessAsync(admin, resource, member.Id, token: null);
        after.Access.ShouldBe(expectedAccess);
        EffectiveAccessSourceResult source = after.Sources.ShouldHaveSingleItem();
        source.Source.ShouldBe(EffectiveAccessSourceKind.Group);
        source.GrantId.ShouldBe(grant.Id);
        source.SubjectType.ShouldBe(AuthorizationObjectType.Group);
        source.SubjectId.ShouldBe(groupId.ToString());
        source.Relation.ToString().ShouldBe(relation);
    }

    [Fact]
    public async Task MembershipRemovalAndGrantRevocation_RemoveGroupDerivedAccess()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        RootResult root = await GetRootAsync(admin);
        RootResult resource = await CreateResourceAsync(admin, root);
        TestUser member = await RegisterUserAsync(admin, "group-removal");
        Guid groupId = await CreateGroupAsync(admin, "group-removal");
        GrantResult grant = await GrantAsync(admin, resource, groupId, "Viewer");
        await AddMemberAsync(admin, groupId, member.Id);
        (await GetEffectiveAccessAsync(admin, resource, member.Id, token: null))
            .Access.ShouldBe(EffectiveAccessLevel.Viewer);

        using HttpResponseMessage removed = await admin.DeleteAsync(
            $"{TestConstants.IdentityBasePath}/groups/{groupId}/members/{member.Id}");
        removed.StatusCode.ShouldBe(HttpStatusCode.NoContent, await removed.Content.ReadAsStringAsync());
        EffectiveAccessResult afterRemoval = await GetEffectiveAccessAsync(admin, resource, member.Id, token: null);
        afterRemoval.Access.ShouldBe(EffectiveAccessLevel.None);
        afterRemoval.Sources.ShouldBeEmpty();

        await AddMemberAsync(admin, groupId, member.Id);
        (await GetEffectiveAccessAsync(admin, resource, member.Id, token: null))
            .Access.ShouldBe(EffectiveAccessLevel.Viewer);

        using HttpResponseMessage revoked = await admin.DeleteAsync($"{BasePath}/grants/{grant.Id}");
        revoked.StatusCode.ShouldBe(HttpStatusCode.OK, await revoked.Content.ReadAsStringAsync());
        GrantResult revocation = await revoked.DeserializeAsync<GrantResult>();
        revocation.Status.ShouldBe(ResourceGrantStatus.Revoked);
        EffectiveAccessResult afterRevocation = await GetEffectiveAccessAsync(
            admin, resource, member.Id, revocation.AuthorizationToken);
        afterRevocation.Access.ShouldBe(EffectiveAccessLevel.None);
        afterRevocation.Sources.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExistingIdentityGroup_ReconcilesCurrentMembershipWhenGranted()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        RootResult root = await GetRootAsync(admin);
        RootResult resource = await CreateResourceAsync(admin, root);
        TestUser member = await RegisterUserAsync(admin, "existing-group");
        using HttpResponseMessage groupsResponse = await admin.GetAsync($"{TestConstants.IdentityBasePath}/groups");
        groupsResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        List<GroupResult> groups = await groupsResponse.DeserializeAsync<List<GroupResult>>();
        GroupResult allUsers = groups.Single(group => group.Name == "All Users");

        GrantResult grant = await GrantAsync(admin, resource, allUsers.Id, "Viewer");

        EffectiveAccessResult effective = await GetEffectiveAccessAsync(admin, resource, member.Id, grant.AuthorizationToken);
        effective.Access.ShouldBe(EffectiveAccessLevel.Viewer);
        effective.Sources.ShouldContain(source =>
            source.Source == EffectiveAccessSourceKind.Group && source.GrantId == grant.Id);
    }

    [Fact]
    public async Task CrossOrganizationMembersAndGroups_AreDeniedWithoutCreatingRelationships()
    {
        using var rootAdmin = await _auth.CreateRootAdminClientAsync();
        RootResult root = await GetRootAsync(rootAdmin);
        RootResult resource = await CreateResourceAsync(rootAdmin, root);
        string otherTenant = $"group-org-{Guid.NewGuid():N}";
        using HttpClient otherAdmin = await ProvisionTenantClientAsync(rootAdmin, otherTenant);
        RootResult otherRoot = await GetRootAsync(otherAdmin);
        RootResult otherResource = await CreateResourceAsync(otherAdmin, otherRoot);
        TestUser otherMember = await RegisterUserAsync(otherAdmin, "other-group-member");
        Guid otherGroup = await CreateGroupAsync(otherAdmin, "other-group");
        Guid rootGroup = await CreateGroupAsync(rootAdmin, "root-group");

        using HttpResponseMessage memberDenied = await rootAdmin.PostAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/groups/{rootGroup}/members",
            new { userIds = new[] { otherMember.Id } });
        memberDenied.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using HttpResponseMessage grantDenied = await rootAdmin.PostAsJsonAsync(
            GrantsPath("Folder", resource.Id), GrantBody(otherGroup, "Viewer"));
        grantDenied.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using HttpResponseMessage reverseGrantDenied = await otherAdmin.PostAsJsonAsync(
            GrantsPath("Folder", otherResource.Id), GrantBody(rootGroup, "Viewer"));
        reverseGrantDenied.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static async Task<RootResult> GetRootAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync($"{BasePath}/organization/root");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.DeserializeAsync<RootResult>();
    }

    private static async Task<RootResult> CreateResourceAsync(HttpClient client, RootResult parent)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync($"{BasePath}/folders", new
        {
            parentId = parent.Id,
            name = $"Group sharing {Guid.NewGuid():N}",
            authorizationToken = parent.AuthorizationToken,
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await response.DeserializeAsync<RootResult>();
    }

    private static async Task<TestUser> RegisterUserAsync(HttpClient admin, string prefix)
    {
        string email = $"{prefix}-{Guid.NewGuid():N}@example.com";
        using HttpResponseMessage response = await admin.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/register", new
        {
            firstName = prefix,
            lastName = "Member",
            email,
            userName = email,
            password = TestConstants.DefaultPassword,
            confirmPassword = TestConstants.DefaultPassword,
            phoneNumber = (string?)null,
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        RegistrationResult registration = await response.DeserializeAsync<RegistrationResult>();
        return new TestUser(registration.UserId, email);
    }

    private static async Task<Guid> CreateGroupAsync(HttpClient admin, string prefix)
    {
        using HttpResponseMessage response = await admin.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/groups", new
        {
            name = $"{prefix}-{Guid.NewGuid():N}",
            description = "Group sharing integration test",
            isDefault = false,
            roleIds = Array.Empty<string>(),
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        GroupResult group = await response.DeserializeAsync<GroupResult>();
        return group.Id;
    }

    private static async Task AddMemberAsync(HttpClient admin, Guid groupId, string userId)
    {
        using HttpResponseMessage response = await admin.PostAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/groups/{groupId}/members",
            new { userIds = new[] { userId } });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task<GrantResult> GrantAsync(
        HttpClient client,
        RootResult root,
        Guid groupId,
        string relation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, GrantsPath("Folder", root.Id))
        {
            Content = JsonContent.Create(GrantBody(groupId, relation)),
        };
        request.Headers.Add("X-Authz-Token", root.AuthorizationToken);
        using HttpResponseMessage response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.DeserializeAsync<GrantResult>();
    }

    private static object GrantBody(Guid groupId, string relation) => new
    {
        subjectType = "Group",
        subjectId = groupId,
        relation,
        confirmOrganizationWide = false,
    };

    private static async Task<EffectiveAccessResult> GetEffectiveAccessAsync(
        HttpClient client,
        RootResult root,
        string subjectId,
        string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{BasePath}/resources/Folder/{root.Id}/effective-access?subjectId={Uri.EscapeDataString(subjectId)}");
        if (token is not null) request.Headers.Add("X-Authz-Token", token);
        using HttpResponseMessage response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.DeserializeAsync<EffectiveAccessResult>();
    }

    private static string GrantsPath(string resourceType, Guid resourceId) =>
        $"{BasePath}/resources/{resourceType}/{resourceId}/grants";

    private async Task<HttpClient> ProvisionTenantClientAsync(HttpClient rootClient, string tenantId)
    {
        string adminEmail = $"{tenantId}-admin@example.com";
        using (HttpResponseMessage response = await rootClient.PostAsJsonAsync(TestConstants.TenantsBasePath, new
        {
            id = tenantId,
            name = $"Tenant {tenantId}",
            connectionString = (string?)null,
            adminEmail,
            adminPassword = TestConstants.DefaultPassword,
            issuer = $"{tenantId}.issuer",
        }))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        }

        for (int attempt = 0; attempt < 60; attempt++)
        {
            using HttpResponseMessage response = await rootClient.GetAsync(
                $"{TestConstants.TenantsBasePath}/{tenantId}/provisioning");
            if (response.IsSuccessStatusCode)
            {
                string content = await response.Content.ReadAsStringAsync();
                if (content.Contains("Completed", StringComparison.OrdinalIgnoreCase)) break;
                content.Contains("Failed", StringComparison.OrdinalIgnoreCase).ShouldBeFalse(content);
            }

            if (attempt == 59) throw new TimeoutException($"Tenant {tenantId} provisioning did not complete.");
            await Task.Delay(1000);
        }

        for (int attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                return await _auth.CreateAuthenticatedClientAsync(adminEmail, TestConstants.DefaultPassword, tenantId);
            }
            catch (HttpRequestException) when (attempt < 29)
            {
                await Task.Delay(1000);
            }
        }

        throw new TimeoutException($"Tenant {tenantId} administrator login did not become ready.");
    }

    private sealed record RootResult(Guid Id, string AuthorizationToken);
    private sealed record RegistrationResult(string UserId);
    private sealed record TestUser(string Id, string Email);
    private sealed record GroupResult(Guid Id, string Name);
    private sealed record GrantResult(Guid Id, ResourceGrantStatus Status, string AuthorizationToken);
    private sealed record EffectiveAccessResult(
        EffectiveAccessLevel Access,
        IReadOnlyList<EffectiveAccessSourceResult> Sources);
    private sealed record EffectiveAccessSourceResult(
        Guid? GrantId,
        EffectiveAccessSourceKind Source,
        AuthorizationObjectType SubjectType,
        string SubjectId,
        AuthorizationRelation Relation);
}
