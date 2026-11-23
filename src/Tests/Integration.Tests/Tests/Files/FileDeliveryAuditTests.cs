using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using FSH.Modules.Auditing.Contracts;
using FSH.Modules.Auditing.Contracts.Dtos;
using FSH.Modules.Files.Contracts.v1.DTOs;
using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;
using Integration.Tests.Tests.Auditing;

namespace Integration.Tests.Tests.Files;

[Collection(FshCollectionDefinition.Name)]
public sealed class FileDeliveryAuditTests
{
    private const string FilesBasePath = "/api/v1/files";
    private readonly AuthHelper _auth;

    public FileDeliveryAuditTests(FshWebApplicationFactory factory)
    {
        _auth = new AuthHelper(factory);
    }

    [Fact]
    public async Task DownloadUrl_Should_RetainSecurityAudit_ButSuppressSecretHttpBody()
    {
        using var client = await _auth.CreateRootAdminClientAsync();
        var id = await UploadAndFinalizeAsync(client, "audit-clean.pdf");
        string path = $"{FilesBasePath}/{id}/url";

        using var response = await client.GetAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var download = await response.DeserializeAsync<PresignedDownloadResponse>();

        var security = await PollSecurityAsync(client, SecurityAction.FileDownloadIssued);
        var securityDetail = await AuditTestHelper.GetByIdAsync(client, security.Id);
        securityDetail.ShouldNotBeNull();
        securityDetail!.Payload.GetProperty("action").GetString().ShouldBe(nameof(SecurityAction.FileDownloadIssued));
        securityDetail.Payload.GetRawText().ShouldNotContain(download.Url.ToString());

        securityDetail.Payload.GetRawText().ShouldNotContain("X-Amz-Signature");
        var activity = await PollActivityAsync(client, path);
        var activityDetail = await AuditTestHelper.GetByIdAsync(client, activity.Id);
        activityDetail.ShouldNotBeNull();
        AssertNoResponsePreview(activityDetail!);
        activityDetail.Payload.GetRawText().ShouldNotContain("X-Amz-Signature");
        activityDetail.Payload.GetRawText().ShouldNotContain(download.Url.ToString());
    }


    [Fact]
    public async Task UploadUrl_Should_SuppressSignedResponseBodyFromHttpAudit()
    {
        using var client = await _auth.CreateRootAdminClientAsync();
        const string path = FilesBasePath + "/upload-url";
        using var response = await client.PostAsJsonAsync(path, new
        {
            ownerType = "MyFiles",
            ownerId = (Guid?)null,
            fileName = "audit-upload.pdf",
            contentType = "application/pdf",
            sizeBytes = 256,
            visibility = 1,
            category = "Document"
        });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var upload = await response.DeserializeAsync<PresignedUploadResponse>();

        var activity = await PollActivityAsync(client, path);
        var detail = await AuditTestHelper.GetByIdAsync(client, activity.Id);
        detail.ShouldNotBeNull();
        AssertNoResponsePreview(detail!);
        detail.Payload.GetRawText().ShouldNotContain("X-Amz-Signature");
        detail.Payload.GetRawText().ShouldNotContain(upload.UploadUrl.ToString());
    }
    [Fact]
    public async Task DeniedDownload_Should_WriteExplicitSecurityAudit_WithoutUrl()
    {
        using var client = await _auth.CreateRootAdminClientAsync();
        using var uploadResponse = await client.PostAsJsonAsync($"{FilesBasePath}/upload-url", new
        {
            ownerType = "MyFiles",
            ownerId = (Guid?)null,
            fileName = "audit-pending.pdf",
            contentType = "application/pdf",
            sizeBytes = 256,
            visibility = 1,
            category = "Document"
        });
        var upload = await uploadResponse.DeserializeAsync<PresignedUploadResponse>();

        using var response = await client.GetAsync($"{FilesBasePath}/{upload.FileAssetId}/url");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var audit = await PollSecurityAsync(client, SecurityAction.FileDownloadDenied);
        var detail = await AuditTestHelper.GetByIdAsync(client, audit.Id);
        detail.ShouldNotBeNull();
        detail!.Payload.GetProperty("action").GetString().ShouldBe(nameof(SecurityAction.FileDownloadDenied));
        detail.Payload.GetRawText().ShouldNotContain("X-Amz-Signature");
    }


    private static void AssertNoResponsePreview(AuditDetailDto detail)
    {
        if (detail.Payload.TryGetProperty("responsePreview", out var preview))
        {
            preview.ValueKind.ShouldBe(JsonValueKind.Null);
        }
    }
    private static async Task<AuditSummaryDto> PollSecurityAsync(
        HttpClient client, SecurityAction action)
    {
        for (int attempt = 0; attempt < AuditTestHelper.DefaultPollAttempts; attempt++)
        {
            var rows = await AuditTestHelper.GetListAsync(client, $"/security?action={action}");
            var match = rows.FirstOrDefault();
            if (match is not null) return match;
            await Task.Delay(AuditTestHelper.PollInterval);
        }

        throw new TimeoutException($"No {action} security audit was persisted.");
    }

    private static async Task<AuditSummaryDto> PollActivityAsync(HttpClient client, string path)
    {
        string query = $"eventType={AuditEventType.Activity}&search={Uri.EscapeDataString(path)}";
        for (int attempt = 0; attempt < AuditTestHelper.DefaultPollAttempts; attempt++)
        {
            var page = await AuditTestHelper.GetAuditsPageAsync(client, pageSize: 100, extraQuery: query);
            var match = page.Items.FirstOrDefault();
            if (match is not null) return match;
            await Task.Delay(AuditTestHelper.PollInterval);
        }

        throw new TimeoutException($"No HTTP activity audit was persisted for {path}.");
    }

    private static async Task<Guid> UploadAndFinalizeAsync(HttpClient client, string fileName)
    {
        byte[] bytes = new byte[256];
        RandomNumberGenerator.Fill(bytes);
        using var uploadResponse = await client.PostAsJsonAsync($"{FilesBasePath}/upload-url", new
        {
            ownerType = "MyFiles",
            ownerId = (Guid?)null,
            fileName,
            contentType = "application/pdf",
            sizeBytes = bytes.Length,
            visibility = 1,
            category = "Document"
        });
        var upload = await uploadResponse.DeserializeAsync<PresignedUploadResponse>();

        using var raw = new HttpClient();
        using var put = new HttpRequestMessage(HttpMethod.Put, upload.UploadUrl)
        {
            Content = new ByteArrayContent(bytes)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/pdf") }
            }
        };
        using var putResponse = await raw.SendAsync(put);
        putResponse.EnsureSuccessStatusCode();

        using var finalize = await client.PostAsync($"{FilesBasePath}/{upload.FileAssetId}/finalize", null);
        finalize.EnsureSuccessStatusCode();
        return upload.FileAssetId;
    }
}
