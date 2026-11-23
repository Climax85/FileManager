using System.Globalization;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using FSH.Modules.Files.Contracts.v1.DTOs;
using FSH.Modules.Files.Domain;
using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;
using Microsoft.AspNetCore.WebUtilities;

namespace Integration.Tests.Tests.Files;

[Collection(FshCollectionDefinition.Name)]
public sealed class PrivateFileDeliveryTests
{
    private const string FilesBasePath = "/api/v1/files";
    private readonly FshWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    public PrivateFileDeliveryTests(FshWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    [Fact]
    public async Task DownloadUrl_Should_ReturnNoStoreShortLivedAuthorizedUrl_ForCleanAvailableFile()
    {
        using var client = await _auth.CreateRootAdminClientAsync();
        var id = await UploadAndFinalizeAsync(client, "private-clean.pdf", RandomBytes(256));
        var before = DateTimeOffset.UtcNow;

        using var response = await client.GetAsync($"{FilesBasePath}/{id}/url");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl.ShouldNotBeNull();
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var download = await response.DeserializeAsync<PresignedDownloadResponse>();
        download.Url.IsAbsoluteUri.ShouldBeTrue();
        download.Url.Query.ShouldContain("X-Amz-Signature=");
        download.ExpiresAt.ShouldBeGreaterThan(before);
        download.ExpiresAt.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow.AddMinutes(5).AddSeconds(5));
        long.Parse(
            QueryHelpers.ParseQuery(download.Url.Query)["X-Amz-Expires"].ToString(),
            CultureInfo.InvariantCulture).ShouldBeLessThanOrEqualTo(300);
    }

    [Fact]
    public async Task UploadUrl_Should_ReturnNoStoreResponse()
    {
        using var client = await _auth.CreateRootAdminClientAsync();

        using var response = await RequestUploadAsync(client, "private-upload.pdf", 256);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl.ShouldNotBeNull();
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    [Fact]
    public async Task UploadUrl_Should_ReturnNoStoreOnIdempotentReplay()
    {
        using var client = await _auth.CreateRootAdminClientAsync();
        string key = Guid.NewGuid().ToString("N");
        using var first = new HttpRequestMessage(HttpMethod.Post, $"{FilesBasePath}/upload-url")
        {
            Content = JsonContent.Create(new
            {
                ownerType = "MyFiles",
                ownerId = (Guid?)null,
                fileName = "idempotent-upload.pdf",
                contentType = "application/pdf",
                sizeBytes = 256,
                visibility = 1,
                category = "Document"
            })
        };
        first.Headers.Add("Idempotency-Key", key);
        using var firstResponse = await client.SendAsync(first);
        firstResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var replay = new HttpRequestMessage(HttpMethod.Post, $"{FilesBasePath}/upload-url")
        {
            Content = JsonContent.Create(new
            {
                ownerType = "MyFiles",
                ownerId = (Guid?)null,
                fileName = "idempotent-upload.pdf",
                contentType = "application/pdf",
                sizeBytes = 256,
                visibility = 1,
                category = "Document"
            })
        };
        replay.Headers.Add("Idempotency-Key", key);
        using var response = await client.SendAsync(replay);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl.ShouldNotBeNull();
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        response.Headers.GetValues("Idempotency-Replayed").ShouldContain("true");
    }

    [Fact]
    public async Task ObjectStorage_ShouldRejectAnonymousGet_WhenSignatureIsRemoved()
    {
        using var client = await _auth.CreateRootAdminClientAsync();
        var id = await UploadAndFinalizeAsync(client, "not-public.pdf", RandomBytes(256));
        using var urlResponse = await client.GetAsync($"{FilesBasePath}/{id}/url");
        var download = await urlResponse.DeserializeAsync<PresignedDownloadResponse>();
        var unsignedUrl = download.Url.GetLeftPart(UriPartial.Path);

        using var anonymous = new HttpClient();
        using var response = await anonymous.GetAsync(unsignedUrl);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }


    [Fact]
    public async Task DownloadUrl_Should_Return401_ForAnonymousApiRequest()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("tenant", TestConstants.RootTenantId);

        using var response = await client.GetAsync($"{FilesBasePath}/{Guid.NewGuid()}/url");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
    [Fact]
    public async Task DownloadUrl_Should_Return404_WhenUploadIsStillPending()
    {
        using var client = await _auth.CreateRootAdminClientAsync();
        using var uploadResponse = await RequestUploadAsync(client, "pending.pdf", 256);
        var upload = await uploadResponse.DeserializeAsync<PresignedUploadResponse>();

        using var response = await client.GetAsync($"{FilesBasePath}/{upload.FileAssetId}/url");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DownloadUrl_Should_Return404_WhenClamAvFindsMalware()
    {
        using var client = await _auth.CreateRootAdminClientAsync();
        byte[] eicar = Encoding.ASCII.GetBytes(
            "X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");
        var (id, dto) = await UploadAndFinalizeWithResultAsync(client, "eicar.pdf", eicar);
        dto.Status.ShouldBe(FileAssetStatus.Quarantined);
        dto.ScanStatus.ShouldBe((int)ScanStatus.Infected);

        using var response = await client.GetAsync($"{FilesBasePath}/{id}/url");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DownloadUrl_Should_Return404_WhenFileWasSoftDeleted()
    {
        using var client = await _auth.CreateRootAdminClientAsync();
        var id = await UploadAndFinalizeAsync(client, "deleted.pdf", RandomBytes(256));
        using var delete = await client.DeleteAsync($"{FilesBasePath}/{id}");
        delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var response = await client.GetAsync($"{FilesBasePath}/{id}/url");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Finalize_Should_QuarantineAndDenyDownload_WhenScannerIsUnavailable()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int unusedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var unavailableFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Files:Scanner:Host"] = "127.0.0.1",
                    ["Files:Scanner:Port"] = unusedPort.ToString(CultureInfo.InvariantCulture),
                    ["Files:Scanner:TimeoutMilliseconds"] = "100"
                }));
        });
        var token = await _auth.GetRootAdminTokenAsync();
        using var client = unavailableFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        client.DefaultRequestHeaders.Add("tenant", TestConstants.RootTenantId);

        var (id, dto) = await UploadAndFinalizeWithResultAsync(client, "scanner-outage.pdf", RandomBytes(256));
        dto.Status.ShouldBe(FileAssetStatus.Quarantined);
        dto.ScanStatus.ShouldBe((int)ScanStatus.ScanFailed);

        using var response = await client.GetAsync($"{FilesBasePath}/{id}/url");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static Task<HttpResponseMessage> RequestUploadAsync(
        HttpClient client, string fileName, long sizeBytes) =>
        client.PostAsJsonAsync($"{FilesBasePath}/upload-url", new
        {
            ownerType = "MyFiles",
            ownerId = (Guid?)null,
            fileName,
            contentType = "application/pdf",
            sizeBytes,
            visibility = 1,
            category = "Document"
        });

    private static async Task<Guid> UploadAndFinalizeAsync(
        HttpClient client, string fileName, byte[] bytes)
    {
        var (id, _) = await UploadAndFinalizeWithResultAsync(client, fileName, bytes);
        return id;
    }

    private static async Task<(Guid Id, FileAssetDto Dto)> UploadAndFinalizeWithResultAsync(
        HttpClient client, string fileName, byte[] bytes)
    {
        using var uploadResponse = await RequestUploadAsync(client, fileName, bytes.LongLength);
        uploadResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
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
        finalize.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (upload.FileAssetId, await finalize.DeserializeAsync<FileAssetDto>());
    }

    private static byte[] RandomBytes(int size)
    {
        byte[] bytes = new byte[size];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }
}
