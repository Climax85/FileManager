using System.Security.Cryptography;
using System.Net.Http.Json;
using FSH.Modules.Auditing.Contracts;
using FSH.Modules.Authorization.Contracts;
using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;
using Integration.Tests.Tests.Auditing;
using FSH.Modules.Files.Contracts.v1.DTOs;
using FSH.Modules.Authorization.Data;
using FSH.Modules.Authorization.Domain;
using Grpc.Core;
using Integration.Tests.Tests.Sessions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Integration.Tests.Tests.Authorization;

[Collection(FshCollectionDefinition.Name)]
public sealed class SafeMoveTests
{
    private const string BasePath = "/api/v1/authorization";
    private readonly FshWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    public SafeMoveTests(FshWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    [Fact]
    public async Task FolderMove_PreservesDirectGrants_RecalculatesSubtreeInheritance_AndAuditsSuccess()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        FolderResult root = await GetRootAsync(admin);
        FolderResult oldParent = await CreateFolderAsync(admin, root.Id, "Old parent", root.AuthorizationToken);
        FolderResult newParent = await CreateFolderAsync(admin, root.Id, "New parent", oldParent.AuthorizationToken);
        FolderResult moved = await CreateFolderAsync(admin, oldParent.Id, "Moved subtree", newParent.AuthorizationToken);
        FolderResult child = await CreateFolderAsync(admin, moved.Id, "Nested child", moved.AuthorizationToken);

        TestUser oldInherited = await RegisterUserAsync(admin, "old-inherited");
        TestUser newInherited = await RegisterUserAsync(admin, "new-inherited");
        TestUser direct = await RegisterUserAsync(admin, "direct-grant");
        GrantResult oldGrant = await GrantAsync(admin, "Folder", oldParent.Id, oldInherited.Id, "Viewer", child.AuthorizationToken);
        GrantResult newGrant = await GrantAsync(admin, "Folder", newParent.Id, newInherited.Id, "Editor", oldGrant.AuthorizationToken);
        GrantResult directGrant = await GrantAsync(admin, "Folder", moved.Id, direct.Id, "Viewer", newGrant.AuthorizationToken);

        using var oldClient = await _auth.CreateAuthenticatedClientAsync(oldInherited.Email, TestConstants.DefaultPassword);
        using var newClient = await _auth.CreateAuthenticatedClientAsync(newInherited.Email, TestConstants.DefaultPassword);
        using var directClient = await _auth.CreateAuthenticatedClientAsync(direct.Email, TestConstants.DefaultPassword);
        (await GetFolderAsync(oldClient, moved.Id, directGrant.AuthorizationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetFolderAsync(newClient, child.Id, directGrant.AuthorizationToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        DateTime beforeMove = DateTime.UtcNow;
        using HttpResponseMessage response = await MoveAsync(
            admin, "Folder", moved.Id, newParent.Id, directGrant.AuthorizationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        MoveResult result = await response.DeserializeAsync<MoveResult>();
        result.ParentFolderId.ShouldBe(newParent.Id);
        result.SynchronizationStatus.ShouldBe(RelationshipSynchronizationStatus.Confirmed);
        result.AuthorizationToken.ShouldNotBeNullOrWhiteSpace();

        FolderResult after = await GetFolderResultAsync(admin, moved.Id, result.AuthorizationToken);
        after.ParentId.ShouldBe(newParent.Id);
        (await GetFolderAsync(oldClient, moved.Id, result.AuthorizationToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await GetFolderAsync(newClient, child.Id, result.AuthorizationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetFolderAsync(directClient, moved.Id, result.AuthorizationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        using HttpResponseMessage grantsResponse = await GetGrantsAsync(admin, "Folder", moved.Id, result.AuthorizationToken);
        grantsResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        GrantListResult grants = await grantsResponse.DeserializeAsync<GrantListResult>();
        GrantResult preserved = grants.Grants.Single(x => x.Id == directGrant.Id);
        preserved.Status.ShouldBe(ResourceGrantStatus.Active);
        preserved.Relation.ShouldBe(AuthorizationRelation.Viewer);
        await AssertSecurityAuditAsync(admin, beforeMove, "ResourceMoved");
    }

    [Theory]
    [InlineData(null, "Editor", HttpStatusCode.Forbidden)]
    [InlineData("Viewer", "Manager", HttpStatusCode.Forbidden)]
    [InlineData("Editor", "Editor", HttpStatusCode.Forbidden)]
    [InlineData("Manager", "Viewer", HttpStatusCode.NotFound)]
    [InlineData("Manager", null, HttpStatusCode.NotFound)]
    public async Task Move_RequiresManagerOnSourceAndEditorOnDestination_AndAuditsEveryRoleDenial(
        string? sourceRelation, string? destinationRelation, HttpStatusCode expectedStatus)
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        FolderResult root = await GetRootAsync(admin);
        FolderResult source = await CreateFolderAsync(admin, root.Id, "Denied source", root.AuthorizationToken);
        FolderResult destination = await CreateFolderAsync(admin, root.Id, "Denied destination", source.AuthorizationToken);
        TestUser actor = await RegisterUserAsync(admin, "move-denied");
        string token = destination.AuthorizationToken;
        if (sourceRelation is not null)
        {
            token = (await GrantAsync(admin, "Folder", source.Id, actor.Id, sourceRelation, token)).AuthorizationToken;
        }
        if (destinationRelation is not null)
        {
            token = (await GrantAsync(admin, "Folder", destination.Id, actor.Id, destinationRelation, token)).AuthorizationToken;
        }

        using var actorClient = await _auth.CreateAuthenticatedClientAsync(actor.Email, TestConstants.DefaultPassword);
        DateTime beforeMove = DateTime.UtcNow;
        using HttpResponseMessage response = await MoveAsync(actorClient, "Folder", source.Id, destination.Id, token);
        response.StatusCode.ShouldBe(expectedStatus, await response.Content.ReadAsStringAsync());
        (await GetFolderResultAsync(admin, source.Id, token)).ParentId.ShouldBe(root.Id);
        await AssertSecurityAuditAsync(admin, beforeMove, "PermissionDenied", actor.Id);
    }

    [Fact]
    public async Task FolderMove_RejectsSelfAndDescendantCycles_AndAuditsDenial()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        FolderResult root = await GetRootAsync(admin);
        FolderResult parent = await CreateFolderAsync(admin, root.Id, "Cycle source", root.AuthorizationToken);
        FolderResult child = await CreateFolderAsync(admin, parent.Id, "Cycle destination", parent.AuthorizationToken);

        DateTime beforeMove = DateTime.UtcNow;
        using HttpResponseMessage descendant = await MoveAsync(admin, "Folder", parent.Id, child.Id, child.AuthorizationToken);
        descendant.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await descendant.Content.ReadAsStringAsync());
        using HttpResponseMessage self = await MoveAsync(admin, "Folder", parent.Id, parent.Id, child.AuthorizationToken);
        self.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await self.Content.ReadAsStringAsync());
        (await GetFolderResultAsync(admin, parent.Id, child.AuthorizationToken)).ParentId.ShouldBe(root.Id);
        await AssertSecurityAuditAsync(admin, beforeMove, "PermissionDenied");
    }

    [Fact]
    public async Task FailedMoveSynchronization_IsNotReportedSuccessful_AndPendingMoveDeniesAccess()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        FolderResult root = await GetRootAsync(admin);
        FolderResult source = await CreateFolderAsync(admin, root.Id, "Pending source", root.AuthorizationToken);
        FolderResult destination = await CreateFolderAsync(admin, root.Id, "Pending destination", source.AuthorizationToken);
        var token = await _auth.GetRootAdminTokenAsync();
        FolderResult child = await CreateFolderAsync(admin, source.Id, "Pending child", destination.AuthorizationToken);
        using WebApplicationFactory<Program> outage = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IRelationshipWriter>();
                services.AddSingleton<IRelationshipWriter, FailingRelationshipWriter>();
            }));
        using var outageAdmin = outage.CreateClient();
        outageAdmin.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.AccessToken);
        outageAdmin.DefaultRequestHeaders.Add("tenant", TestConstants.RootTenantId);

        DateTime beforeMove = DateTime.UtcNow;
        using HttpResponseMessage response = await MoveAsync(
            outageAdmin, "Folder", source.Id, destination.Id, child.AuthorizationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable, await response.Content.ReadAsStringAsync());

        using var scope = _factory.Services.CreateScope();
        await AuthorizationTestData.UseRootTenantAsync(scope);
        var db = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        Folder pending = await db.Folders.SingleAsync(x => x.Id == new FolderId(source.Id));
        pending.ParentId.ShouldBe(new FolderId(destination.Id));
        RelationshipSynchronization sync = await db.RelationshipSyncs.SingleAsync(x => x.OperationId == pending.SynchronizationId);
        sync.Status.ShouldBe(RelationshipSynchronizationStatus.Pending);
        (await GetFolderAsync(admin, child.Id, child.AuthorizationToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await db.OutboxMessages.SingleAsync(x => x.Id == sync.OperationId)).ProcessedOnUtc.ShouldBeNull();
        (await GetFolderAsync(admin, source.Id, destination.AuthorizationToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await AssertSecurityAuditAsync(admin, beforeMove, "PolicyFailed");
    }

    [Fact]
    public async Task FileMove_ReplacesParent_PreservesDirectGrant_AndRecalculatesInheritedAccess()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        FolderResult root = await GetRootAsync(admin);
        FolderResult source = await CreateFolderAsync(admin, root.Id, "File source", root.AuthorizationToken);
        FolderResult destination = await CreateFolderAsync(admin, root.Id, "File destination", source.AuthorizationToken);
        Guid fileId = await UploadFinalizeAsync(admin, source.Id, "safe-move.pdf");
        TestUser actor = await RegisterUserAsync(admin, "file-mover");
        TestUser oldInherited = await RegisterUserAsync(admin, "file-old");
        TestUser newInherited = await RegisterUserAsync(admin, "file-new");
        TestUser direct = await RegisterUserAsync(admin, "file-direct");
        string token = (await GrantAsync(admin, "File", fileId, actor.Id, "Manager", destination.AuthorizationToken)).AuthorizationToken;
        token = (await GrantAsync(admin, "Folder", destination.Id, actor.Id, "Editor", token)).AuthorizationToken;
        token = (await GrantAsync(admin, "Folder", source.Id, oldInherited.Id, "Viewer", token)).AuthorizationToken;
        token = (await GrantAsync(admin, "Folder", destination.Id, newInherited.Id, "Viewer", token)).AuthorizationToken;
        GrantResult directGrant = await GrantAsync(admin, "File", fileId, direct.Id, "Viewer", token);

        using var actorClient = await _auth.CreateAuthenticatedClientAsync(actor.Email, TestConstants.DefaultPassword);
        using var oldClient = await _auth.CreateAuthenticatedClientAsync(oldInherited.Email, TestConstants.DefaultPassword);
        using var newClient = await _auth.CreateAuthenticatedClientAsync(newInherited.Email, TestConstants.DefaultPassword);
        using var directClient = await _auth.CreateAuthenticatedClientAsync(direct.Email, TestConstants.DefaultPassword);
        (await oldClient.GetAsync($"/api/v1/files/{fileId}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await newClient.GetAsync($"/api/v1/files/{fileId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        DateTime beforeMove = DateTime.UtcNow;
        using HttpResponseMessage response = await MoveAsync(actorClient, "File", fileId, destination.Id, directGrant.AuthorizationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        MoveResult result = await response.DeserializeAsync<MoveResult>();
        result.ParentFolderId.ShouldBe(destination.Id);
        result.SynchronizationStatus.ShouldBe(RelationshipSynchronizationStatus.Confirmed);
        using HttpResponseMessage metadata = await admin.GetAsync($"/api/v1/files/{fileId}");
        FileAssetDto file = await metadata.DeserializeAsync<FileAssetDto>();
        file.ParentFolderId.ShouldBe(destination.Id);
        (await oldClient.GetAsync($"/api/v1/files/{fileId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await newClient.GetAsync($"/api/v1/files/{fileId}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await directClient.GetAsync($"/api/v1/files/{fileId}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        using HttpResponseMessage grantsResponse = await GetGrantsAsync(admin, "File", fileId, result.AuthorizationToken);
        GrantListResult grants = await grantsResponse.DeserializeAsync<GrantListResult>();
        grants.Grants.Single(x => x.Id == directGrant.Id).Status.ShouldBe(ResourceGrantStatus.Active);
        await AssertSecurityAuditAsync(admin, beforeMove, "ResourceMoved", actor.Id);
    }

    [Fact]
    public async Task Move_RejectsDestinationOwnedByAnotherOrganization_AndAuditsDenial()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        FolderResult root = await GetRootAsync(admin);
        FolderResult source = await CreateFolderAsync(admin, root.Id, "Organization source", root.AuthorizationToken);
        using var otherAdmin = await CreateOtherTenantAdminClientAsync();
        FolderResult otherRoot = await GetRootAsync(otherAdmin);

        DateTime beforeMove = DateTime.UtcNow;
        using HttpResponseMessage response = await MoveAsync(admin, "Folder", source.Id, otherRoot.Id, source.AuthorizationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync());
        (await GetFolderResultAsync(admin, source.Id, source.AuthorizationToken)).ParentId.ShouldBe(root.Id);
        await AssertSecurityAuditAsync(admin, beforeMove, "PermissionDenied");
    }

    [Fact]
    public async Task FailedFileMoveSynchronization_LeavesDurablePendingState_AndDeniesMetadata()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        FolderResult root = await GetRootAsync(admin);
        FolderResult source = await CreateFolderAsync(admin, root.Id, "Pending file source", root.AuthorizationToken);
        FolderResult destination = await CreateFolderAsync(admin, root.Id, "Pending file destination", source.AuthorizationToken);
        Guid fileId = await UploadFinalizeAsync(admin, source.Id, "pending-move.pdf");
        var token = await _auth.GetRootAdminTokenAsync();
        using WebApplicationFactory<Program> outage = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IRelationshipWriter>();
                services.AddSingleton<IRelationshipWriter, FailingRelationshipWriter>();
            }));
        using var outageAdmin = outage.CreateClient();
        outageAdmin.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.AccessToken);
        outageAdmin.DefaultRequestHeaders.Add("tenant", TestConstants.RootTenantId);

        using HttpResponseMessage response = await MoveAsync(
            outageAdmin, "File", fileId, destination.Id, destination.AuthorizationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable, await response.Content.ReadAsStringAsync());
        using var scope = _factory.Services.CreateScope();
        await AuthorizationTestData.UseRootTenantAsync(scope);
        var db = scope.ServiceProvider.GetRequiredService<FSH.Modules.Files.Data.FilesDbContext>();
        FSH.Modules.Files.Domain.FileAsset pending = await db.FileAssets.SingleAsync(x => x.Id == fileId);
        pending.ParentFolderId.ShouldBe(destination.Id);
        pending.AuthorizationSynchronizationStatus.ShouldBe(RelationshipSynchronizationStatus.Pending);
        (await admin.GetAsync($"/api/v1/files/{fileId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static async Task<FolderResult> GetRootAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync($"{BasePath}/organization/root");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.DeserializeAsync<FolderResult>();
    }

    private static async Task<FolderResult> CreateFolderAsync(
        HttpClient client, Guid parentId, string name, string token)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync($"{BasePath}/folders", new
        {
            parentId,
            name = $"{name} {Guid.NewGuid():N}",
            authorizationToken = token,
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await response.DeserializeAsync<FolderResult>();
    }

    private static async Task<GrantResult> GrantAsync(
        HttpClient client, string resourceType, Guid resourceId, string subjectId, string relation, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BasePath}/resources/{resourceType}/{resourceId}/grants")
        {
            Content = JsonContent.Create(new
            {
                subjectType = "User",
                subjectId,
                relation,
                confirmOrganizationWide = false,
            }),
        };
        request.Headers.Add("X-Authz-Token", token);
        using HttpResponseMessage response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.DeserializeAsync<GrantResult>();
    }

    private static Task<HttpResponseMessage> GetGrantsAsync(
        HttpClient client, string resourceType, Guid resourceId, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{BasePath}/resources/{resourceType}/{resourceId}/grants");
        request.Headers.Add("X-Authz-Token", token);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> MoveAsync(
        HttpClient client, string resourceType, Guid resourceId, Guid destinationFolderId, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{BasePath}/resources/{resourceType}/{resourceId}/move")
        {
            Content = JsonContent.Create(new { destinationFolderId }),
        };
        request.Headers.Add("X-Authz-Token", token);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> GetFolderAsync(HttpClient client, Guid folderId, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{BasePath}/folders/{folderId}");
        request.Headers.Add("X-Authz-Token", token);
        return client.SendAsync(request);
    }

    private static async Task<FolderResult> GetFolderResultAsync(HttpClient client, Guid folderId, string token)
    {
        using HttpResponseMessage response = await GetFolderAsync(client, folderId, token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.DeserializeAsync<FolderResult>();
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

    private static async Task AssertSecurityAuditAsync(
        HttpClient admin, DateTime after, string action, string? subjectId = null)
    {
        var audit = await AuditTestHelper.PollForAuditAsync(admin, x =>
            x.Source == "Authorization" && x.EventType == AuditEventType.Security && x.OccurredAtUtc >= after &&
            (subjectId is null || x.UserId == subjectId));
        var detail = await AuditTestHelper.GetByIdAsync(admin, audit.Id);
        detail.ShouldNotBeNull();
        detail.Payload.GetRawText().ShouldContain(action);
    }

    private async Task<HttpClient> CreateOtherTenantAdminClientAsync()
    {
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        string uniqueId = Guid.NewGuid().ToString("N")[..8];
        string tenantId = $"move-iso-{uniqueId}";
        string adminEmail = $"move-admin-{uniqueId}@tenant.com";
        using HttpResponseMessage created = await rootClient.PostAsJsonAsync(TestConstants.TenantsBasePath, new
        {
            id = tenantId,
            name = $"Tenant {tenantId}",
            connectionString = (string?)null,
            adminEmail,
            adminPassword = TestConstants.DefaultPassword,
            issuer = $"{tenantId}.issuer",
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        await WaitForProvisioningAsync(rootClient, tenantId);
        for (int attempt = 0; attempt < 29; attempt++)
        {
            try
            {
                return await _auth.CreateAuthenticatedClientAsync(adminEmail, TestConstants.DefaultPassword, tenantId);
            }
            catch (HttpRequestException)
            {
                await Task.Delay(1000);
            }
        }
        return await _auth.CreateAuthenticatedClientAsync(adminEmail, TestConstants.DefaultPassword, tenantId);
    }

    private static async Task WaitForProvisioningAsync(HttpClient client, string tenantId)
    {
        for (int attempt = 0; attempt < 60; attempt++)
        {
            using HttpResponseMessage response = await client.GetAsync($"{TestConstants.TenantsBasePath}/{tenantId}/provisioning");
            if (response.IsSuccessStatusCode)
            {
                string content = await response.Content.ReadAsStringAsync();
                if (content.Contains("Completed", StringComparison.OrdinalIgnoreCase)) return;
                if (content.Contains("Failed", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Tenant {tenantId} provisioning failed: {content}");
                }
            }
            await Task.Delay(1000);
        }
        throw new TimeoutException($"Tenant {tenantId} provisioning did not complete.");
    }

    private static async Task<Guid> UploadFinalizeAsync(HttpClient client, Guid parentFolderId, string fileName)
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(128);
        using HttpResponseMessage uploadResponse = await client.PostAsJsonAsync("/api/v1/files/upload-url", new
        {
            ownerType = "MyFiles",
            ownerId = (Guid?)null,
            parentFolderId,
            fileName,
            contentType = "application/pdf",
            sizeBytes = bytes.Length,
            visibility = Visibility.Private,
            category = "Document",
        });
        uploadResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await uploadResponse.Content.ReadAsStringAsync());
        PresignedUploadResponse upload = await uploadResponse.DeserializeAsync<PresignedUploadResponse>();
        using var request = new HttpRequestMessage(HttpMethod.Put, upload.UploadUrl) { Content = new ByteArrayContent(bytes) };
        foreach (KeyValuePair<string, string> header in upload.RequiredHeaders)
        {
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
        using var storageClient = new HttpClient();
        using HttpResponseMessage putResponse = await storageClient.SendAsync(request);
        putResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await putResponse.Content.ReadAsStringAsync());
        using HttpResponseMessage finalized = await client.PostAsync($"/api/v1/files/{upload.FileAssetId}/finalize", null);
        finalized.StatusCode.ShouldBe(HttpStatusCode.OK, await finalized.Content.ReadAsStringAsync());
        return upload.FileAssetId;
    }

    private sealed record TestUser(string Id, string Email);
    private sealed record RegistrationResult(string UserId);
    private sealed record FolderResult(
        Guid Id, string OrganizationId, string Name, bool IsRoot, Guid? ParentId,
        DateTimeOffset CreatedAt, string AuthorizationToken, bool CanEdit);
    private sealed record MoveResult(
        string ResourceType, Guid ResourceId, Guid ParentFolderId, Guid OperationId,
        RelationshipSynchronizationStatus SynchronizationStatus, string AuthorizationToken);
    private sealed record GrantListResult(IReadOnlyList<GrantResult> Grants, string AuthorizationToken);
    private sealed record GrantResult(
        Guid Id, AuthorizationObjectType ResourceType, Guid ResourceId, AuthorizationObjectType SubjectType,
        string SubjectId, AuthorizationRelation Relation, ResourceGrantStatus Status, string AuthorizationToken,
        DateTimeOffset CreatedAt, DateTimeOffset? ConfirmedAt);
    private sealed class FailingRelationshipWriter : IRelationshipWriter
    {
        public ValueTask<RelationshipConfirmation> WriteAsync(
            Guid operationId, IReadOnlyCollection<RelationshipMutation> mutations, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<RelationshipConfirmation>(new RpcException(new Status(StatusCode.Unavailable, "Injected relationship write outage.")));
    }
}
