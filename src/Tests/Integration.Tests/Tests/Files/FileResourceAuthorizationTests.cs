using System.Security.Cryptography;
using FSH.Framework.Shared.Constants;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Files.Contracts.v1.DTOs;
using FSH.Modules.Files.Data;
using FSH.Modules.Files.Domain;
using FSH.Modules.Identity.Domain;
using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;
using Integration.Tests.Tests.Authorization;
using Integration.Tests.Tests.Sessions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using FileAssetStatus = FSH.Modules.Files.Contracts.v1.DTOs.FileAssetStatus;

namespace Integration.Tests.Tests.Files;

[Collection(FshCollectionDefinition.Name)]
public sealed class FileResourceAuthorizationTests
{
    private const string FilesBasePath = "/api/v1/files";
    private readonly FshWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    public FileResourceAuthorizationTests(FshWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    [Fact]
    public async Task RootEditor_CanCompletePrivateFilePath_WithConfirmedCreatorManagerAndReadYourWrites()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        SeededUser editor = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, admin, "file-editor");
        OrganizationRoot root = await GrantRootAccessAsync(editor.UserId, AuthorizationRelation.Editor);
        using var client = await _auth.CreateAuthenticatedClientAsync(editor.Email, editor.Password);
        byte[] bytes = RandomNumberGenerator.GetBytes(2048);

        (Guid fileId, PresignedUploadResponse firstUpload) = await RequestUploadWithReplayAsync(
            client, root.Id.Value, Visibility.Private, bytes.Length);

        using (var scope = _factory.Services.CreateScope())
        {
            await AuthorizationTestData.UseRootTenantAsync(scope);
            var db = scope.ServiceProvider.GetRequiredService<FilesDbContext>();
            (await db.FileAssets.CountAsync(x => x.Id == fileId)).ShouldBe(1);
            FileAsset pending = await db.FileAssets.SingleAsync(x => x.Id == fileId);
            pending.ParentFolderId.ShouldBe(root.Id.Value);
            pending.AuthorizationSynchronizationStatus.ShouldBe(RelationshipSynchronizationStatus.Confirmed);
            pending.AuthorizationToken.ShouldNotBeNullOrWhiteSpace();
        }

        await PutBytesAsync(firstUpload, bytes);
        var finalizedResponse = await client.PostAsync($"{FilesBasePath}/{fileId}/finalize", null);
        finalizedResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await finalizedResponse.Content.ReadAsStringAsync());
        FileAssetDto finalized = await finalizedResponse.DeserializeAsync<FileAssetDto>();
        finalized.ParentFolderId.ShouldBe(root.Id.Value);
        finalized.Status.ShouldBe(FileAssetStatus.Available);
        finalized.PublicUrl.ShouldBeNull();

        // No consistency token is supplied by the browser: the API must route the persisted
        // WrittenAt token so an immediate read cannot observe a stale SpiceDB replica.
        var metadataResponse = await client.GetAsync($"{FilesBasePath}/{fileId}");
        metadataResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        FileAssetDto metadata = await metadataResponse.DeserializeAsync<FileAssetDto>();
        metadata.OriginalFileName.ShouldBe("organization-document.pdf");
        metadata.PublicUrl.ShouldBeNull();

        var downloadResponse = await client.GetAsync($"{FilesBasePath}/{fileId}/url");
        downloadResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        downloadResponse.Headers.CacheControl?.NoStore.ShouldBeTrue();
        PresignedDownloadResponse download = await downloadResponse.DeserializeAsync<PresignedDownloadResponse>();
        using var storageClient = new HttpClient();
        byte[] downloaded = await storageClient.GetByteArrayAsync(download.Url);
        downloaded.ShouldBe(bytes);

        using (var scope = _factory.Services.CreateScope())
        {
            await AuthorizationTestData.UseRootTenantAsync(scope);
            var db = scope.ServiceProvider.GetRequiredService<FilesDbContext>();
            FileAsset asset = await db.FileAssets.SingleAsync(x => x.Id == fileId);
            var checker = scope.ServiceProvider.GetRequiredService<IPermissionChecker>();
            PermissionCheckResult manager = await checker.CheckAsync(new PermissionCheck(
                new UserId(editor.UserId),
                AuthorizationObjects.For(new FileId(fileId)),
                AuthorizationPermission.Manage,
                AuthorizationConsistency.AtLeastAsFresh(new AuthorizationToken(asset.AuthorizationToken!))));
            manager.IsAllowed.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task OrganizationMember_WithoutGrant_HasNoMetadataListOrDownloadAccess_EvenForPublicVisibility()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        OrganizationRoot root = await EnsureRootAsync();
        Guid fileId = await UploadFinalizeAsync(admin, root.Id.Value, Visibility.Public, "must-stay-private.pdf");
        SeededUser member = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, admin, "ungranted-member");
        await GrantOrganizationMembershipAsync(member.UserId);
        using var client = await _auth.CreateAuthenticatedClientAsync(member.Email, member.Password);

        var metadata = await client.GetAsync($"{FilesBasePath}/{fileId}");
        var download = await client.GetAsync($"{FilesBasePath}/{fileId}/url");
        var mine = await client.GetAsync($"{FilesBasePath}/mine");
        var shared = await client.GetAsync($"{FilesBasePath}/shared");

        metadata.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        download.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        mine.StatusCode.ShouldBe(HttpStatusCode.OK);
        shared.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await mine.DeserializeAsync<List<FileAssetDto>>()).ShouldNotContain(x => x.Id == fileId);
        (await shared.DeserializeAsync<List<FileAssetDto>>()).ShouldNotContain(x => x.Id == fileId);
    }

    [Fact]
    public async Task RootEditor_WithoutApplicationUploadPermission_CannotCreateFile()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        SeededUser editor = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, admin, "resource-only-editor");
        await RemoveBasicRoleAsync(editor.UserId);
        OrganizationRoot root = await GrantRootAccessAsync(editor.UserId, AuthorizationRelation.Editor);
        using var client = await _auth.CreateAuthenticatedClientAsync(editor.Email, editor.Password);

        var response = await RequestUploadAsync(client, root.Id.Value, Visibility.Private, 128);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ApplicationUploader_WithoutParentEditorPermission_CannotCreateFile()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        SeededUser member = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, admin, "application-only-uploader");
        OrganizationRoot root = await EnsureRootAsync();
        await GrantOrganizationMembershipAsync(member.UserId);
        using var client = await _auth.CreateAuthenticatedClientAsync(member.Email, member.Password);

        var response = await RequestUploadAsync(client, root.Id.Value, Visibility.Private, 128);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private async Task<OrganizationRoot> EnsureRootAsync()
    {
        using var scope = _factory.Services.CreateScope();
        await AuthorizationTestData.UseRootTenantAsync(scope);
        return await scope.ServiceProvider.GetRequiredService<IOrganizationRootProvisioner>()
            .EnsureRootAsync(new OrganizationId(TestConstants.RootTenantId));
    }

    private async Task<OrganizationRoot> GrantRootAccessAsync(string userId, AuthorizationRelation relation)
    {
        OrganizationRoot root = await EnsureRootAsync();
        using var scope = _factory.Services.CreateScope();
        await AuthorizationTestData.UseRootTenantAsync(scope);
        var writer = scope.ServiceProvider.GetRequiredService<IRelationshipWriter>();
        AuthorizationObject organization = AuthorizationObjects.For(new OrganizationId(TestConstants.RootTenantId));
        AuthorizationSubject subject = AuthorizationSubjects.For(new UserId(userId));
        RelationshipConfirmation confirmation = await writer.WriteAsync(Guid.CreateVersion7(),
        [
            RelationshipMutation.Touch(organization, AuthorizationRelation.Member, subject),
            RelationshipMutation.Touch(AuthorizationObjects.For(root.Id), relation, subject),
        ]);
        return root with { AuthorizationToken = confirmation.WrittenAt };
    }

    private async Task GrantOrganizationMembershipAsync(string userId)
    {
        using var scope = _factory.Services.CreateScope();
        await AuthorizationTestData.UseRootTenantAsync(scope);
        var writer = scope.ServiceProvider.GetRequiredService<IRelationshipWriter>();
        await writer.WriteAsync(Guid.CreateVersion7(),
        [
            RelationshipMutation.Touch(
                AuthorizationObjects.For(new OrganizationId(TestConstants.RootTenantId)),
                AuthorizationRelation.Member,
                AuthorizationSubjects.For(new UserId(userId))),
        ]);
    }

    private async Task RemoveBasicRoleAsync(string userId)
    {
        using var scope = _factory.Services.CreateScope();
        await AuthorizationTestData.UseRootTenantAsync(scope);
        var users = scope.ServiceProvider.GetRequiredService<UserManager<FshUser>>();
        FshUser user = await users.Users.SingleAsync(x => x.Id == userId);
        IdentityResult result = await users.RemoveFromRoleAsync(user, RoleConstants.Basic);
        result.Succeeded.ShouldBeTrue(string.Join("; ", result.Errors.Select(x => x.Description)));
    }

    private static async Task<(Guid FileId, PresignedUploadResponse Response)> RequestUploadWithReplayAsync(
        HttpClient client,
        Guid parentFolderId,
        Visibility visibility,
        long sizeBytes)
    {
        string key = Guid.NewGuid().ToString("N");
        using var first = UploadRequest(parentFolderId, visibility, sizeBytes, key);
        using var firstResponse = await client.SendAsync(first);
        firstResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await firstResponse.Content.ReadAsStringAsync());
        PresignedUploadResponse upload = await firstResponse.DeserializeAsync<PresignedUploadResponse>();

        using var replay = UploadRequest(parentFolderId, visibility, sizeBytes, key);
        using var replayResponse = await client.SendAsync(replay);
        replayResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        PresignedUploadResponse replayed = await replayResponse.DeserializeAsync<PresignedUploadResponse>();
        replayed.FileAssetId.ShouldBe(upload.FileAssetId);
        replayed.UploadUrl.ShouldBe(upload.UploadUrl);
        return (upload.FileAssetId, upload);
    }

    private static Task<HttpResponseMessage> RequestUploadAsync(
        HttpClient client,
        Guid parentFolderId,
        Visibility visibility,
        long sizeBytes) =>
        client.SendAsync(UploadRequest(parentFolderId, visibility, sizeBytes, Guid.NewGuid().ToString("N")));

    private static HttpRequestMessage UploadRequest(
        Guid parentFolderId,
        Visibility visibility,
        long sizeBytes,
        string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{FilesBasePath}/upload-url")
        {
            Content = JsonContent.Create(new
            {
                ownerType = "MyFiles",
                ownerId = (Guid?)null,
                parentFolderId,
                fileName = "organization-document.pdf",
                contentType = "application/pdf",
                sizeBytes,
                visibility,
                category = "Document",
            }),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return request;
    }

    private static async Task PutBytesAsync(PresignedUploadResponse upload, byte[] bytes)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, upload.UploadUrl)
        {
            Content = new ByteArrayContent(bytes),
        };
        foreach (KeyValuePair<string, string> header in upload.RequiredHeaders)
        {
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
        using var client = new HttpClient();
        using var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task<Guid> UploadFinalizeAsync(
        HttpClient client,
        Guid parentFolderId,
        Visibility visibility,
        string fileName)
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(512);
        var uploadResponse = await client.PostAsJsonAsync($"{FilesBasePath}/upload-url", new
        {
            ownerType = "MyFiles",
            ownerId = (Guid?)null,
            parentFolderId,
            fileName,
            contentType = "application/pdf",
            sizeBytes = bytes.Length,
            visibility,
            category = "Document",
        });
        uploadResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await uploadResponse.Content.ReadAsStringAsync());
        PresignedUploadResponse upload = await uploadResponse.DeserializeAsync<PresignedUploadResponse>();
        await PutBytesAsync(upload, bytes);
        var finalized = await client.PostAsync($"{FilesBasePath}/{upload.FileAssetId}/finalize", null);
        finalized.StatusCode.ShouldBe(HttpStatusCode.OK, await finalized.Content.ReadAsStringAsync());
        return upload.FileAssetId;
    }
}
