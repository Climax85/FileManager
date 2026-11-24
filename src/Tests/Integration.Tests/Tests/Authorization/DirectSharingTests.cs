using FSH.Modules.Auditing.Contracts;
using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Authorization.Data;
using FSH.Modules.Authorization.Domain;
using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;
using Integration.Tests.Tests.Auditing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Integration.Tests.Tests.Authorization;

[Collection(FshCollectionDefinition.Name)]
public sealed class DirectSharingTests
{
    private const string BasePath = "/api/v1/authorization";
    private readonly FshWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    public DirectSharingTests(FshWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    [Fact]
    public async Task ManagerGrantsViewer_WithReadYourWrites_AndViewerCannotGrant()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        RootResult root = await GetRootAsync(admin);
        TestUser viewer = await RegisterUserAsync(admin, "viewer");
        TestUser other = await RegisterUserAsync(admin, "other");

        GrantResult grant = await GrantAsync(admin, root, viewer.Id, "Viewer");
        grant.Status.ShouldBe(ResourceGrantStatus.Active);
        grant.AuthorizationToken.ShouldNotBeNullOrWhiteSpace();

        using var viewerClient = await _auth.CreateAuthenticatedClientAsync(viewer.Email, TestConstants.DefaultPassword);
        EffectiveAccessResult effective = await GetEffectiveAccessAsync(viewerClient, root, viewer.Id, grant.AuthorizationToken);
        effective.Access.ShouldBe(EffectiveAccessLevel.Viewer);
        effective.Sources.ShouldContain(x => x.Source == EffectiveAccessSourceKind.Direct && x.GrantId == grant.Id);

        DateTime beforeDenial = DateTime.UtcNow;
        using HttpResponseMessage denied = await viewerClient.PostAsJsonAsync(GrantsPath("Folder", root.Id), GrantBody(other.Id, "Editor"));
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await AssertSecurityAuditAsync(admin, beforeDenial, "PermissionDenied", viewer.Id);
    }

    [Fact]
    public async Task EditorCannotShare_ButManagerCanShareAndManagerGrantIsExplained()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        RootResult root = await GetRootAsync(admin);
        TestUser editor = await RegisterUserAsync(admin, "editor");
        TestUser recipient = await RegisterUserAsync(admin, "recipient");
        await GrantAsync(admin, root, editor.Id, "Editor");

        using var editorClient = await _auth.CreateAuthenticatedClientAsync(editor.Email, TestConstants.DefaultPassword);
        using HttpResponseMessage editorDenied = await editorClient.PostAsJsonAsync(
            GrantsPath("Folder", root.Id), GrantBody(recipient.Id, "Viewer"));
        editorDenied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await GrantAsync(admin, root, editor.Id, "Manager");
        GrantResult recipientGrant = await GrantAsync(editorClient, root, recipient.Id, "Viewer");
        recipientGrant.Status.ShouldBe(ResourceGrantStatus.Active);

        EffectiveAccessResult managerAccess = await GetEffectiveAccessAsync(admin, root, editor.Id, recipientGrant.AuthorizationToken);
        managerAccess.Access.ShouldBe(EffectiveAccessLevel.Manager);
        managerAccess.Sources.ShouldContain(x => x.Source == EffectiveAccessSourceKind.Direct && x.Relation == AuthorizationRelation.Manager);
    }

    [Fact]
    public async Task OrganizationWideSharing_RequiresConfirmation_AndNeverUsesAnonymousWildcard()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        RootResult root = await GetRootAsync(admin);
        TestUser member = await RegisterUserAsync(admin, "orgmember");

        using HttpResponseMessage unconfirmed = await admin.PostAsJsonAsync(
            GrantsPath("Folder", root.Id),
            new { subjectType = "Organization", subjectId = TestConstants.RootTenantId, relation = "Viewer", confirmOrganizationWide = false });
        unconfirmed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using HttpResponseMessage response = await admin.PostAsJsonAsync(
            GrantsPath("Folder", root.Id),
            new { subjectType = "Organization", subjectId = TestConstants.RootTenantId, relation = "Viewer", confirmOrganizationWide = true });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        GrantResult grant = await response.DeserializeAsync<GrantResult>();

        using var memberClient = await _auth.CreateAuthenticatedClientAsync(member.Email, TestConstants.DefaultPassword);
        EffectiveAccessResult effective = await GetEffectiveAccessAsync(memberClient, root, member.Id, grant.AuthorizationToken);
        effective.Access.ShouldBe(EffectiveAccessLevel.Viewer);
        effective.Sources.ShouldContain(x => x.Source == EffectiveAccessSourceKind.Organization && x.GrantId == grant.Id);

        using var scope = _factory.Services.CreateScope();
        await AuthorizationTestData.UseRootTenantAsync(scope);
        var reader = scope.ServiceProvider.GetRequiredService<IRelationshipReader>();
        AuthorizationRelationshipPage relationships = await reader.ReadAsync(new RelationshipQuery(
            AuthorizationObjectType.Folder,
            root.Id.ToString(),
            AuthorizationRelation.Viewer,
            AuthorizationSubjects.Members(AuthorizationObjects.For(new OrganizationId(TestConstants.RootTenantId))),
            Consistency: AuthorizationConsistency.AtLeastAsFresh(new AuthorizationToken(grant.AuthorizationToken))));
        relationships.Relationships.Count.ShouldBe(1);
        relationships.Relationships.Single().Subject.Object.Type.ShouldBe(AuthorizationObjectType.Organization);
        relationships.Relationships.Single().Subject.Object.ObjectId.ShouldNotBe("*");
    }

    [Fact]
    public async Task RevocationDeniesImmediately_AndViewerCannotRevoke()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        RootResult root = await GetRootAsync(admin);
        TestUser viewer = await RegisterUserAsync(admin, "revoke");
        GrantResult grant = await GrantAsync(admin, root, viewer.Id, "Viewer");
        using var viewerClient = await _auth.CreateAuthenticatedClientAsync(viewer.Email, TestConstants.DefaultPassword);

        using HttpResponseMessage denied = await viewerClient.DeleteAsync($"{BasePath}/grants/{grant.Id}");
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        DateTime beforeRevocation = DateTime.UtcNow;
        using HttpResponseMessage revoked = await admin.DeleteAsync($"{BasePath}/grants/{grant.Id}");
        revoked.StatusCode.ShouldBe(HttpStatusCode.OK);
        GrantResult revocation = await revoked.DeserializeAsync<GrantResult>();
        revocation.Status.ShouldBe(ResourceGrantStatus.Revoked);
        await AssertSecurityAuditAsync(admin, beforeRevocation, "ResourceRevoked");

        EffectiveAccessResult effective = await GetEffectiveAccessAsync(admin, root, viewer.Id, revocation.AuthorizationToken);
        effective.Access.ShouldBe(EffectiveAccessLevel.None);
        effective.Sources.ShouldBeEmpty();
    }

    [Fact]
    public async Task FileGrant_UsesSameContract_AndEditorCannotShareFile()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        RootResult root = await GetRootAsync(admin);
        TestUser editor = await RegisterUserAsync(admin, "fileeditor");
        TestUser recipient = await RegisterUserAsync(admin, "filerecipient");
        FileId fileId = FileId.New();

        using (var scope = _factory.Services.CreateScope())
        {
            await AuthorizationTestData.UseRootTenantAsync(scope);
            var writer = scope.ServiceProvider.GetRequiredService<IRelationshipWriter>();
            await writer.WriteAsync(Guid.CreateVersion7(), [
                RelationshipMutation.Touch(AuthorizationObjects.For(fileId), AuthorizationRelation.Organization,
                    new AuthorizationSubject(AuthorizationObjects.For(new OrganizationId(TestConstants.RootTenantId)))),
                RelationshipMutation.Touch(AuthorizationObjects.For(fileId), AuthorizationRelation.Folder,
                    new AuthorizationSubject(AuthorizationObjects.For(new FolderId(root.Id)))),
            ]);
        }

        GrantResult grant = await GrantAsync(admin, root.Id, "File", fileId.Value, editor.Id, "Editor");
        using var editorClient = await _auth.CreateAuthenticatedClientAsync(editor.Email, TestConstants.DefaultPassword);
        EffectiveAccessResult effective = await GetEffectiveAccessAsync(editorClient, root.Id, "File", fileId.Value, editor.Id, grant.AuthorizationToken);
        effective.Access.ShouldBe(EffectiveAccessLevel.Editor);

        using HttpResponseMessage denied = await editorClient.PostAsJsonAsync(
            GrantsPath("File", fileId.Value), GrantBody(recipient.Id, "Viewer"));
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PendingGrant_IsNotEffective_AndFailedMutationIsNotReportedSuccessful()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        RootResult root = await GetRootAsync(admin);
        TestUser member = await RegisterUserAsync(admin, "pendinggrant");
        TokenResult token = await _auth.GetRootAdminTokenAsync();

        using WebApplicationFactory<Program> outage = CreateSpiceDbOutageFactory();
        using var outageAdmin = CreateClient(outage, token.AccessToken);
        using HttpResponseMessage response = await outageAdmin.PostAsJsonAsync(
            GrantsPath("Folder", root.Id), GrantBody(member.Id, "Viewer"));
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        using var scope = _factory.Services.CreateScope();
        await AuthorizationTestData.UseRootTenantAsync(scope);
        var db = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        ResourceGrant pending = await db.ResourceGrants.SingleAsync(x => x.SubjectId == member.Id && x.ResourceId == root.Id);
        pending.Status.ShouldBe(ResourceGrantStatus.Pending);
        var checker = scope.ServiceProvider.GetRequiredService<IPermissionChecker>();
        PermissionCheckResult check = await checker.CheckAsync(new PermissionCheck(
            new UserId(member.Id), AuthorizationObjects.For(new FolderId(root.Id)), AuthorizationPermission.View));
        check.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task PendingRevocation_DeniesAccess_WhileStaleProviderRelationshipStillExists()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        RootResult root = await GetRootAsync(admin);
        TestUser member = await RegisterUserAsync(admin, "pendingrevoke");
        GrantResult grant = await GrantAsync(admin, root, member.Id, "Viewer");
        TokenResult token = await _auth.GetRootAdminTokenAsync();

        using WebApplicationFactory<Program> outage = CreateSpiceDbOutageFactory();
        using var outageAdmin = CreateClient(outage, token.AccessToken);
        using HttpResponseMessage response = await outageAdmin.DeleteAsync($"{BasePath}/grants/{grant.Id}");
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        using var scope = _factory.Services.CreateScope();
        await AuthorizationTestData.UseRootTenantAsync(scope);
        var db = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        ResourceGrant pending = await db.ResourceGrants.SingleAsync(x => x.Id == grant.Id);
        pending.Status.ShouldBe(ResourceGrantStatus.RevocationPending);

        // The healthy provider still contains Viewer; only the durable pending-revocation overlay can deny it.
        var reader = scope.ServiceProvider.GetRequiredService<IRelationshipReader>();
        AuthorizationRelationshipPage stale = await reader.ReadAsync(new RelationshipQuery(
            AuthorizationObjectType.Folder, root.Id.ToString(), AuthorizationRelation.Viewer,
            AuthorizationSubjects.For(new UserId(member.Id))));
        stale.Relationships.ShouldNotBeEmpty();
        var checker = scope.ServiceProvider.GetRequiredService<IPermissionChecker>();
        PermissionCheckResult check = await checker.CheckAsync(new PermissionCheck(
            new UserId(member.Id), AuthorizationObjects.For(new FolderId(root.Id)), AuthorizationPermission.View,
            AuthorizationConsistency.AtLeastAsFresh(new AuthorizationToken(grant.AuthorizationToken))));
        check.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task GrantMutations_AreRateLimited()
    {
        TokenResult token = await _auth.GetRootAdminTokenAsync();
        using WebApplicationFactory<Program> limited = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimitingOptions:Enabled"] = "true",
                ["Authorization:Sharing:GrantRateLimitPermitLimit"] = "2",
                ["Authorization:Sharing:GrantRateLimitWindowSeconds"] = "60",
            })));
        using var admin = CreateClient(limited, token.AccessToken);
        RootResult root = await GetRootAsync(admin);
        TestUser member = await RegisterUserAsync(admin, "ratelimit");

        var responses = new List<HttpResponseMessage>();
        try
        {
            for (int i = 0; i < 5; i++)
            {
                responses.Add(await admin.PostAsJsonAsync(GrantsPath("Folder", root.Id), GrantBody(member.Id, "Viewer")));
            }
            responses.ShouldContain(x => x.StatusCode == HttpStatusCode.TooManyRequests);
        }
        finally
        {
            foreach (HttpResponseMessage response in responses) response.Dispose();
        }
    }

    private WebApplicationFactory<Program> CreateSpiceDbOutageFactory() => _factory.WithWebHostBuilder(builder =>
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authorization:SpiceDB:Endpoint"] = "http://127.0.0.1:1",
            ["Authorization:SpiceDB:MaxRetries"] = "0",
            ["Authorization:SpiceDB:TimeoutSeconds"] = "1",
        })));

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory, string accessToken)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        client.DefaultRequestHeaders.Add("tenant", TestConstants.RootTenantId);
        return client;
    }

    private static async Task<RootResult> GetRootAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync($"{BasePath}/organization/root");
        return await response.DeserializeAsync<RootResult>();
    }

    private static async Task<TestUser> RegisterUserAsync(HttpClient admin, string prefix)
    {
        string email = $"{prefix}-{Guid.NewGuid():N}@root.com";
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
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        RegistrationResult registration = await response.DeserializeAsync<RegistrationResult>();
        return new TestUser(registration.UserId, email);
    }

    private static object GrantBody(string subjectId, string relation) => new
    {
        subjectType = "User",
        subjectId,
        relation,
        confirmOrganizationWide = false,
    };

    private static Task<GrantResult> GrantAsync(HttpClient client, RootResult root, string subjectId, string relation) =>
        GrantAsync(client, root.Id, "Folder", root.Id, subjectId, relation, root.AuthorizationToken);

    private static async Task<GrantResult> GrantAsync(
        HttpClient client, Guid rootId, string resourceType, Guid resourceId, string subjectId, string relation,
        string? authorizationToken = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, GrantsPath(resourceType, resourceId))
        {
            Content = JsonContent.Create(GrantBody(subjectId, relation)),
        };
        if (authorizationToken is not null) request.Headers.Add("X-Authz-Token", authorizationToken);
        using HttpResponseMessage response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.DeserializeAsync<GrantResult>();
    }

    private static Task<EffectiveAccessResult> GetEffectiveAccessAsync(
        HttpClient client, RootResult root, string subjectId, string token) =>
        GetEffectiveAccessAsync(client, root.Id, "Folder", root.Id, subjectId, token);

    private static async Task<EffectiveAccessResult> GetEffectiveAccessAsync(
        HttpClient client, Guid rootId, string resourceType, Guid resourceId, string subjectId, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{BasePath}/resources/{resourceType}/{resourceId}/effective-access?subjectId={Uri.EscapeDataString(subjectId)}");
        request.Headers.Add("X-Authz-Token", token);
        using HttpResponseMessage response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.DeserializeAsync<EffectiveAccessResult>();
    }

    private static string GrantsPath(string resourceType, Guid resourceId) =>
        $"{BasePath}/resources/{resourceType}/{resourceId}/grants";

    private static async Task AssertSecurityAuditAsync(
        HttpClient admin, DateTime after, string action, string? subjectId = null)
    {
        var audit = await AuditTestHelper.PollForAuditAsync(admin, x =>
            x.Source == "Authorization" && x.EventType == AuditEventType.Security && x.OccurredAtUtc >= after &&
            (subjectId is null || x.UserId == subjectId));
        var detail = await AuditTestHelper.GetByIdAsync(admin, audit.Id);
        detail.ShouldNotBeNull();
        string payload = detail.Payload.GetRawText();
        payload.ShouldContain(action);
        payload.ShouldNotContain("http", StringComparison.OrdinalIgnoreCase);
        payload.ShouldNotContain("url", StringComparison.OrdinalIgnoreCase);
        payload.ShouldNotContain("content", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record RootResult(Guid Id, string AuthorizationToken);
    private sealed record RegistrationResult(string UserId);
    private sealed record TestUser(string Id, string Email);
    private sealed record GrantResult(Guid Id, ResourceGrantStatus Status, string AuthorizationToken);
    private sealed record EffectiveAccessResult(
        EffectiveAccessLevel Access,
        IReadOnlyList<EffectiveAccessSourceResult> Sources);
    private sealed record EffectiveAccessSourceResult(
        Guid? GrantId,
        EffectiveAccessSourceKind Source,
        AuthorizationRelation Relation);
}
