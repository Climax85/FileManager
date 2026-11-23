using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Constants;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Domain;
using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Integration.Tests.Tests.Users;

[Collection(FshCollectionDefinition.Name)]
public sealed class UserManagementGuardTests
{
    private readonly FshWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    public UserManagementGuardTests(FshWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    [Fact]
    public async Task DeleteUser_Should_ReturnBadRequest_When_TargetIsSelf()
    {
        using var client = await _auth.CreateRootAdminClientAsync();
        var profile = await GetCurrentProfileAsync(client);

        var response = await client.DeleteAsync($"{TestConstants.IdentityBasePath}/users/{profile.Id}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DeleteUser_Should_ReturnBadRequest_When_TargetIsAdmin()
    {
        using var client = await _auth.CreateRootAdminClientAsync();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];

        // Register a fresh user
        var registerResponse = await client.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/register", new
        {
            firstName = "Other",
            lastName = "Admin",
            email = $"otheradmin-{uniqueId}@example.com",
            userName = $"otheradmin-{uniqueId}",
            password = "Test@1234!",
            confirmPassword = "Test@1234!"
        });
        registerResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var registered = await registerResponse.DeserializeAsync<RegisterResult>();

        // Seed the target's administrator membership directly: this test exercises the
        // delete guard, not the separately covered MFA-gated administrator assignment API.
        await AssignAdminRoleDirectlyAsync(registered.UserId);

        // Root admin tries to delete the second admin → must be rejected.
        var deleteResponse = await client.DeleteAsync($"{TestConstants.IdentityBasePath}/users/{registered.UserId}");
        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AssignUserRoles_Should_ReturnForbidden_When_AdministratorChangeLacksMfa()
    {
        using var client = await _auth.CreateRootAdminClientAsync();
        var profile = await GetCurrentProfileAsync(client);
        var (adminRole, _) = await GetSystemRolesAsync(client);

        var response = await client.PostAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/users/{profile.Id}/roles",
            new
            {
                userId = profile.Id,
                userRoles = new[]
                {
                    new { roleId = adminRole.Id, roleName = "Admin", enabled = false }
                }
            });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private async Task AssignAdminRoleDirectlyAsync(string userId)
    {
        using var scope = _factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider
            .GetRequiredService<IMultiTenantStore<AppTenantInfo>>()
            .GetAsync(TestConstants.RootTenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>()
            .MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<FshUser>>();
        var user = await userManager.FindByIdAsync(userId);
        user.ShouldNotBeNull();
        (await userManager.AddToRoleAsync(user!, RoleConstants.Admin)).Succeeded.ShouldBeTrue();
    }

    private static async Task<UserDto> GetCurrentProfileAsync(HttpClient client)
    {
        var response = await client.GetAsync($"{TestConstants.IdentityBasePath}/profile");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.DeserializeAsync<UserDto>();
    }

    private static async Task<(RoleDto Admin, RoleDto Basic)> GetSystemRolesAsync(HttpClient client)
    {
        var response = await client.GetAsync($"{TestConstants.IdentityBasePath}/roles");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var page = await response.DeserializeAsync<PagedResponse<RoleDto>>();
        return (page.Items.First(r => r.Name == "Admin"), page.Items.First(r => r.Name == "Basic"));
    }
}
