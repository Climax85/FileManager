using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Authorization.Data;
using FSH.Modules.Authorization.Services;
using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;
using System.Text.Json;

namespace Integration.Tests.Tests.Authorization;

[Collection(FshCollectionDefinition.Name)]
public sealed class OrganizationRootTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly FshWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    public OrganizationRootTests(FshWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    [Fact]
    public async Task RootProvisioning_IsIdempotent_AndAdministratorCanRetrieveAndOpenRoot()
    {
        // Arrange
        using var client = await _auth.CreateRootAdminClientAsync();

        // Act
        using (var scope = _factory.Services.CreateScope())
        {
            await AuthorizationTestData.UseRootTenantAsync(scope);
            var provisioner = scope.ServiceProvider.GetRequiredService<IOrganizationRootProvisioner>();
            await provisioner.EnsureRootAsync(new OrganizationId(TestConstants.RootTenantId), CancellationToken.None);
            await provisioner.EnsureRootAsync(new OrganizationId(TestConstants.RootTenantId), CancellationToken.None);
        }
        var first = await client.GetAsync("/api/v1/authorization/organization/root");
        var firstRoot = await first.DeserializeAsync<RootResult>();
        var opened = await client.GetAsync($"/api/v1/authorization/folders/{firstRoot.Id}");
        var openedRoot = await opened.DeserializeAsync<RootResult>();

        // Assert
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        opened.StatusCode.ShouldBe(HttpStatusCode.OK);
        firstRoot.Id.ShouldNotBe(Guid.Empty);
        firstRoot.IsRoot.ShouldBeTrue();
        firstRoot.AuthorizationToken.ShouldNotBeNullOrWhiteSpace();
        first.Headers.Contains("X-Authz-Token").ShouldBeTrue();
        openedRoot.Id.ShouldBe(firstRoot.Id);
        using (var scope = _factory.Services.CreateScope())
        {
            await AuthorizationTestData.UseRootTenantAsync(scope);
            var db = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
            db.Folders.Count(f => f.IsRoot).ShouldBe(1);
            db.OutboxMessages.Count().ShouldBeGreaterThan(0);
            db.RelationshipSyncs.Count(s => s.Status == RelationshipSynchronizationStatus.Confirmed).ShouldBeGreaterThan(0);
        }
    }

    [Fact]
    public async Task OrdinaryMember_WithoutGrant_CannotRetrieveRoot()
    {
        // Arrange
        string email = $"ordinary-{Guid.NewGuid():N}@root.com";
        using (var admin = await _auth.CreateRootAdminClientAsync())
        {
            var registration = await admin.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/register", new
            {
                firstName = "Ordinary",
                lastName = "Member",
                email,
                userName = email,
                password = TestConstants.DefaultPassword,
                confirmPassword = TestConstants.DefaultPassword,
                phoneNumber = (string?)null,
            });
            registration.StatusCode.ShouldBe(HttpStatusCode.Created);
        }
        using var member = await _auth.CreateAuthenticatedClientAsync(email, TestConstants.DefaultPassword);

        // Act
        var response = await member.GetAsync("/api/v1/authorization/organization/root");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task MemberOfAnotherOrganization_CannotOpenRoot()
    {
        // Arrange
        string tenantId = $"org-{Guid.NewGuid():N}"[..12];
        string adminEmail = $"admin-{Guid.NewGuid():N}@example.com";
        using var rootAdmin = await _auth.CreateRootAdminClientAsync();
        var created = await rootAdmin.PostAsJsonAsync(TestConstants.TenantsBasePath, new
        {
            id = tenantId,
            name = "Other organization",
            connectionString = (string?)null,
            adminEmail,
            adminPassword = TestConstants.DefaultPassword,
            issuer = "integration-tests",
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        await PollProvisioningAsync(rootAdmin, tenantId);
        using var otherAdmin = await _auth.CreateAuthenticatedClientAsync(adminEmail, TestConstants.DefaultPassword, tenantId);
        var otherRootResponse = await otherAdmin.GetAsync("/api/v1/authorization/organization/root");
        otherRootResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var otherRoot = await otherRootResponse.DeserializeAsync<RootResult>();

        // Act
        var crossOrganizationRead = await rootAdmin.GetAsync($"/api/v1/authorization/folders/{otherRoot.Id}");

        // Assert
        crossOrganizationRead.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static async Task PollProvisioningAsync(HttpClient client, string tenantId)
    {
        for (int attempt = 0; attempt < 60; attempt++)
        {
            var response = await client.GetAsync($"{TestConstants.TenantsBasePath}/{tenantId}/provisioning");
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var status = await response.Content.ReadFromJsonAsync<ProvisioningResult>(JsonOptions);
                if (status?.Status == "Completed") return;
                if (status?.Status == "Failed") throw new InvalidOperationException(status.Error);
            }
            await Task.Delay(250);
        }
        throw new TimeoutException("Organization provisioning did not complete.");
    }

    private sealed record RootResult(
        Guid Id,
        string OrganizationId,
        string Name,
        bool IsRoot,
        DateTimeOffset CreatedAt,
        string AuthorizationToken);

    private sealed record ProvisioningResult(string Status, string? Error);
}
