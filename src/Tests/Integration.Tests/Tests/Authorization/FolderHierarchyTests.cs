using System.Net.Http.Json;
using System.Text.Json;
using FSH.Modules.Authorization.Contracts;
using Integration.Tests.Infrastructure;
using Integration.Tests.Tests.Sessions;

namespace Integration.Tests.Tests.Authorization;

[Collection(FshCollectionDefinition.Name)]
public sealed class FolderHierarchyTests
{
    private readonly FshWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    public FolderHierarchyTests(FshWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    [Fact]
    public async Task EditorCanCreateAndRenameAtArbitraryDepth_WhileViewerIsReadOnly()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        FolderResult root = await GetRootAsync(admin);
        FolderResult level1 = await CreateFolderAsync(admin, root.Id, "Level 1", root.AuthorizationToken);
        FolderResult level2 = await CreateFolderAsync(admin, level1.Id, "Level 2", level1.AuthorizationToken);
        FolderResult level3 = await CreateFolderAsync(admin, level2.Id, "Level 3", level2.AuthorizationToken);
        FolderResult level4 = await CreateFolderAsync(admin, level3.Id, "Level 4", level3.AuthorizationToken);

        SeededUser viewer = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, admin, "folder-viewer");
        RelationshipConfirmation viewerGrant = await GrantAsync(new UserId(viewer.UserId), root.Id, AuthorizationRelation.Viewer);
        using var viewerClient = await _auth.CreateAuthenticatedClientAsync(viewer.Email, viewer.Password);
        using var viewerGet = await GetFolderAsync(viewerClient, level4.Id, viewerGrant.WrittenAt.Value);
        viewerGet.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await viewerClient.PostAsJsonAsync("/api/v1/authorization/folders", new
        {
            parentId = level4.Id,
            name = "Viewer child",
            authorizationToken = viewerGrant.WrittenAt.Value.Value,
        })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await viewerClient.PatchAsJsonAsync($"/api/v1/authorization/folders/{level4.Id}", new
        {
            name = "Viewer rename",
            authorizationToken = viewerGrant.WrittenAt.Value.Value,
        })).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        SeededUser editor = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, admin, "folder-editor");
        RelationshipConfirmation editorGrant = await GrantAsync(new UserId(editor.UserId), root.Id, AuthorizationRelation.Editor);
        using var editorClient = await _auth.CreateAuthenticatedClientAsync(editor.Email, editor.Password);
        FolderResult created = await CreateFolderAsync(
            editorClient, level4.Id, "Editor child", editorGrant.WrittenAt.Value.Value);
        using var renamed = await RenameFolderAsync(editorClient, created.Id, "Renamed by editor", created.AuthorizationToken);
        renamed.StatusCode.ShouldBe(HttpStatusCode.OK);
        FolderResult renamedFolder = (await renamed.Content.ReadFromJsonAsync<FolderResult>())!;
        renamedFolder.Name.ShouldBe("Renamed by editor");
        renamedFolder.ParentId.ShouldBe(level4.Id);
        RelationshipConfirmation managerGrant = await GrantAsync(new UserId(manager.UserId), level4.Id, AuthorizationRelation.Manager);
        using var scope = _factory.Services.CreateScope();
        await AuthorizationTestData.UseRootTenantAsync(scope);
        var checker = scope.ServiceProvider.GetRequiredService<IPermissionChecker>();
        PermissionCheckResult inheritedManage = await checker.CheckAsync(new PermissionCheck(
            new UserId(manager.UserId), AuthorizationObjects.For(new FolderId(created.Id)), AuthorizationPermission.Manage,
            AuthorizationConsistency.AtLeastAsFresh(managerGrant.WrittenAt)));
        inheritedManage.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task FolderCursorPagesContainOnlyDirectChildren_AndDoNotExposeExactTotals()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        FolderResult root = await GetRootAsync(admin);
        FolderResult parent = await CreateFolderAsync(admin, root.Id, "Paginated", root.AuthorizationToken);
        string token = parent.AuthorizationToken;
        var expected = new HashSet<Guid>();
        for (int index = 0; index < 5; index++)
        {
            FolderResult child = await CreateFolderAsync(admin, parent.Id, $"Child {index}", token);
            expected.Add(child.Id);
            token = child.AuthorizationToken;
        }
        _ = await CreateFolderAsync(admin, expected.Select(x => x).First(), "Nested, not a direct child", token);

        using var ordinary = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, admin, "folder-list-denied");
        using var deniedClient = await _auth.CreateAuthenticatedClientAsync(ordinary.Email, ordinary.Password);
        using var denied = await ListChildrenAsync(deniedClient, parent.Id, 2, null, token);
        denied.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var actual = new List<Guid>();
        string? cursor = null;
        int pages = 0;
        do
        {
            using var response = await ListChildrenAsync(admin, parent.Id, 2, cursor, token);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            JsonElement rootElement = json.RootElement;
            rootElement.TryGetProperty("total", out _).ShouldBeFalse();
            rootElement.TryGetProperty("totalCount", out _).ShouldBeFalse();
            JsonElement items = rootElement.GetProperty("items");
            items.GetArrayLength().ShouldBeLessThanOrEqualTo(2);
            actual.AddRange(items.EnumerateArray().Select(x => x.GetProperty("id").GetGuid()));
            cursor = rootElement.GetProperty("nextCursor").GetString();
            pages++;
        } while (cursor is not null);

        pages.ShouldBe(3);
        actual.Count.ShouldBe(5);
        actual.Distinct().Count().ShouldBe(5);
        actual.ToHashSet().ShouldBe(expected);
    }

    [Fact]
    public async Task HierarchyRejectsSelfAndDescendantCycles_AndMissingOrCrossOrganizationParents()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        FolderResult root = await GetRootAsync(admin);
        FolderResult parent = await CreateFolderAsync(admin, root.Id, "Cycle parent", root.AuthorizationToken);
        FolderResult child = await CreateFolderAsync(admin, parent.Id, "Cycle child", parent.AuthorizationToken);
        using var scope = _factory.Services.CreateScope();
        await AuthorizationTestData.UseRootTenantAsync(scope);
        var hierarchy = scope.ServiceProvider.GetRequiredService<IFolderHierarchy>();
        OrganizationId organization = new(TestConstants.RootTenantId);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await hierarchy.EnsureValidParentAsync(organization, new FolderId(parent.Id), new FolderId(parent.Id)));
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await hierarchy.EnsureValidParentAsync(organization, new FolderId(child.Id), new FolderId(parent.Id)));
        await Should.ThrowAsync<FSH.Framework.Core.Exceptions.NotFoundException>(async () =>
            await hierarchy.EnsureValidParentAsync(new OrganizationId("other"), new FolderId(parent.Id), null));
    }

    private static async Task<FolderResult> GetRootAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/authorization/organization/root");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<FolderResult>())!;
    }

    private static async Task<FolderResult> CreateFolderAsync(
        HttpClient client, Guid parentId, string name, string token)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/authorization/folders", new { parentId, name, authorizationToken = token });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<FolderResult>())!;
    }

    private static Task<HttpResponseMessage> RenameFolderAsync(HttpClient client, Guid id, string name, string token) =>
        client.PatchAsJsonAsync($"/api/v1/authorization/folders/{id}", new { name, authorizationToken = token });

    private static Task<HttpResponseMessage> GetFolderAsync(HttpClient client, Guid id, AuthorizationToken token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/authorization/folders/{id}");
        request.Headers.Add("X-Authz-Token", token.Value);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> ListChildrenAsync(
        HttpClient client, Guid id, int pageSize, string? cursor, string token)
    {
        string suffix = cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}";
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/authorization/folders/{id}/children?pageSize={pageSize}{suffix}");
        request.Headers.Add("X-Authz-Token", token);
        return client.SendAsync(request);
    }

    private async Task<RelationshipConfirmation> GrantAsync(
        UserId userId, FolderId folderId, AuthorizationRelation relation)
    {
        using var scope = _factory.Services.CreateScope();
        await AuthorizationTestData.UseRootTenantAsync(scope);
        var writer = scope.ServiceProvider.GetRequiredService<IRelationshipWriter>();
        return await writer.WriteAsync(Guid.CreateVersion7(),
        [
            RelationshipMutation.Touch(
                AuthorizationObjects.For(new OrganizationId(TestConstants.RootTenantId)),
                AuthorizationRelation.Member, AuthorizationSubjects.For(userId)),
            RelationshipMutation.Touch(AuthorizationObjects.For(folderId), relation, AuthorizationSubjects.For(userId)),
        ]);
    }

    private sealed record FolderResult(
        Guid Id, string OrganizationId, string Name, bool IsRoot, Guid? ParentId,
        DateTimeOffset CreatedAt, string AuthorizationToken, bool CanEdit);
}
