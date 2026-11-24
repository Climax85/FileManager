using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using FSH.Modules.Files.Contracts.v1.DTOs;
using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Integration.Tests.Tests.Files;

[Collection(FshCollectionDefinition.Name)]
public sealed class FileStorageQuotaTests
{
    private const string FilesBasePath = "/api/v1/files";
    private readonly FshWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    public FileStorageQuotaTests(FshWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    [Fact]
    public async Task Finalize_Should_EnforceActualStorageQuota_WhenUsageChangesAfterUploadUrl()
    {
        using var quotaFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["QuotaOptions:Enabled"] = "true",
                    ["QuotaOptions:Redis"] = "",
                    ["QuotaOptions:ExemptRootTenant"] = "false",
                    ["QuotaOptions:DefaultPlan"] = "free",
                    ["QuotaOptions:Plans:free:StorageBytes"] = "100",
                    ["QuotaOptions:Plans:free:ApiCalls"] = "10000"
                });
            });
        });
        var token = await _auth.GetRootAdminTokenAsync();
        using var client = quotaFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        client.DefaultRequestHeaders.Add("tenant", TestConstants.RootTenantId);

        await UploadAndFinalizeAsync(client, "quota-first.pdf", 60);
        using var reserved = await RequestUploadAsync(client, "quota-reserved.pdf", 40);
        reserved.StatusCode.ShouldBe(HttpStatusCode.OK);
        var reservedUpload = await reserved.DeserializeAsync<PresignedUploadResponse>();
        await UploadAndFinalizeAsync(client, "quota-consume-remainder.pdf", 40);
        await PutBytesAsync(reservedUpload, 40);

        using var response = await client.PostAsync(
            $"{FilesBasePath}/{reservedUpload.FileAssetId}/finalize", null);

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    private static async Task UploadAndFinalizeAsync(HttpClient client, string fileName, int size)
    {
        using var response = await RequestUploadAsync(client, fileName, size);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var upload = await response.DeserializeAsync<PresignedUploadResponse>();
        await PutBytesAsync(upload, size);
        using var finalize = await client.PostAsync($"{FilesBasePath}/{upload.FileAssetId}/finalize", null);
        finalize.EnsureSuccessStatusCode();
    }

    private static Task<HttpResponseMessage> RequestUploadAsync(
        HttpClient client, string fileName, int size) =>
        client.PostAsJsonAsync($"{FilesBasePath}/upload-url", new
        {
            ownerType = "MyFiles",
            ownerId = (Guid?)null,
            fileName,
            contentType = "application/pdf",
            sizeBytes = size,
            visibility = 1,
            category = "Document"
        });

    private static async Task PutBytesAsync(PresignedUploadResponse upload, int size)
    {
        byte[] bytes = new byte[size];
        RandomNumberGenerator.Fill(bytes);
        using var raw = new HttpClient();
        using var put = new HttpRequestMessage(HttpMethod.Put, upload.UploadUrl)
        {
            Content = new ByteArrayContent(bytes)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/pdf") }
            }
        };
        using var response = await raw.SendAsync(put);
        response.EnsureSuccessStatusCode();
    }
}
