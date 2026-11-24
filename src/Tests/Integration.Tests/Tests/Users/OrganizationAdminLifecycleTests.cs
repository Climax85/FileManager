using System.Security.Cryptography;
using System.Text.Json;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Eventing.Outbox;
using FSH.Framework.Shared.Constants;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Auditing.Contracts;
using FSH.Modules.Identity.Contracts.Authorization;
using FSH.Modules.Identity.Contracts.DTOs;
using FSH.Modules.Identity.Contracts.Events;
using FSH.Modules.Identity.Data;
using FSH.Modules.Identity.Domain;
using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;
using Integration.Tests.Tests.Auditing;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoleDto = FSH.Modules.Identity.Contracts.DTOs.RoleDto;
using UserDto = FSH.Modules.Identity.Contracts.DTOs.UserDto;

namespace Integration.Tests.Tests.Users;

[Collection(FshCollectionDefinition.Name)]
public sealed class OrganizationAdminLifecycleTests
{
    private const string DefaultPassword = "Test@1234!";

    private readonly FshWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    public OrganizationAdminLifecycleTests(FshWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    [Fact]
    public async Task AssignAdminRole_Should_DenyPasswordOnlyAdministrator_AndAuditWithoutSecrets()
    {
        OrganizationContext organization = await CreateOrganizationAsync();
        using var adminClient = await CreateOrganizationAdminClientAsync(organization);
        string actorId = await GetCurrentUserIdAsync(adminClient);
        RegisterUserResponse target = await RegisterUserAsync(adminClient, "password-only-target");
        RoleDto adminRole = await GetRoleAsync(adminClient, RoleConstants.Admin);

        var response = await SetAdminRoleAsync(adminClient, target.UserId, adminRole, enabled: true);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await response.Content.ReadAsStringAsync());
        (await IsAdminAsync(adminClient, target.UserId)).ShouldBeFalse();

        string audit = await PollSecurityAuditAsync(
            adminClient,
            SecurityAction.PolicyFailed,
            actorId,
            "MfaRequired");
        AssertSafeAdminAudit(audit, organization.TenantId, target.UserId);
    }

    [Fact]
    public async Task AssignAndRemoveAdminRole_Should_SucceedWithMfa_AndAuditBothChanges()
    {
        OrganizationContext organization = await CreateOrganizationAsync();
        using var passwordClient = await CreateOrganizationAdminClientAsync(organization);
        string actorId = await GetCurrentUserIdAsync(passwordClient);
        RegisterUserResponse target = await RegisterUserAsync(passwordClient, "mfa-target");
        RoleDto adminRole = await GetRoleAsync(passwordClient, RoleConstants.Admin);
        using var mfaClient = await EnableMfaAndCreateClientAsync(
            organization.AdminEmail,
            organization.TenantId);

        // Enrolling does not upgrade a token that was issued before MFA completion.
        var preMfaTokenResponse = await SetAdminRoleAsync(
            passwordClient,
            target.UserId,
            adminRole,
            enabled: true);
        preMfaTokenResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var assignResponse = await SetAdminRoleAsync(mfaClient, target.UserId, adminRole, enabled: true);

        assignResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await assignResponse.Content.ReadAsStringAsync());
        (await IsAdminAsync(mfaClient, target.UserId)).ShouldBeTrue();
        OrganizationAdminChangedIntegrationEvent addedEvent = await ReadAdminChangeEventAsync(
            organization.TenantId,
            target.UserId,
            OrganizationAdminChange.Added);
        addedEvent.OrganizationId.ShouldBe(organization.TenantId);
        addedEvent.TenantId.ShouldBe(organization.TenantId);
        addedEvent.UserId.ShouldBe(target.UserId);
        string assignedAudit = await PollSecurityAuditAsync(
            passwordClient,
            SecurityAction.RoleAssigned,
            actorId,
            "Succeeded");
        AssertSafeAdminAudit(assignedAudit, organization.TenantId, target.UserId);

        var removeResponse = await SetAdminRoleAsync(mfaClient, target.UserId, adminRole, enabled: false);

        removeResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await removeResponse.Content.ReadAsStringAsync());
        (await IsAdminAsync(mfaClient, target.UserId)).ShouldBeFalse();
        OrganizationAdminChangedIntegrationEvent removedEvent = await ReadAdminChangeEventAsync(
            organization.TenantId,
            target.UserId,
            OrganizationAdminChange.Removed);
        removedEvent.OrganizationId.ShouldBe(organization.TenantId);
        removedEvent.TenantId.ShouldBe(organization.TenantId);
        removedEvent.UserId.ShouldBe(target.UserId);
        string revokedAudit = await PollSecurityAuditAsync(
            passwordClient,
            SecurityAction.RoleRevoked,
            actorId,
            "Succeeded");
        AssertSafeAdminAudit(revokedAudit, organization.TenantId, target.UserId);
    }

    [Fact]
    public async Task RemoveAdminRole_Should_RejectLastAdministrator_AndAuditDeniedChange()
    {
        OrganizationContext organization = await CreateOrganizationAsync();
        using var adminClient = await CreateOrganizationAdminClientAsync(organization);
        string adminId = await GetCurrentUserIdAsync(adminClient);

        RoleDto adminRole = await GetRoleAsync(adminClient, RoleConstants.Admin);
        RoleDto roleManager = await CreateRoleManagerRoleAsync(adminClient);
        RegisterUserResponse manager = await RegisterUserAsync(adminClient, "role-manager");
        var managerRoleResponse = await SetRoleAsync(adminClient, manager.UserId, roleManager, enabled: true);
        managerRoleResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await managerRoleResponse.Content.ReadAsStringAsync());
        using var managerClient = await EnableMfaAndCreateClientAsync(
            manager.Email,
            organization.TenantId,
            DefaultPassword);
        string managerId = await GetCurrentUserIdAsync(managerClient);

        var response = await SetAdminRoleAsync(managerClient, adminId, adminRole, enabled: false);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await IsAdminAsync(adminClient, adminId)).ShouldBeTrue();
        string audit = await PollSecurityAuditAsync(
            adminClient,
            SecurityAction.PolicyFailed,
            managerId,
            "LastAdministrator");
        AssertSafeAdminAudit(audit, organization.TenantId, adminId);
    }

    private async Task<OrganizationContext> CreateOrganizationAsync()
    {
        string suffix = Guid.NewGuid().ToString("N")[..12];
        string tenantId = $"org-{suffix}";
        string adminEmail = $"admin-{suffix}@example.com";
        using var rootClient = await _auth.CreateRootAdminClientAsync();

        var response = await rootClient.PostAsJsonAsync(TestConstants.TenantsBasePath, new
        {
            id = tenantId,
            name = $"Organization {suffix}",
            connectionString = (string?)null,
            adminEmail,
            adminPassword = TestConstants.DefaultPassword,
            issuer = $"{tenantId}.issuer"
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        await WaitForProvisioningAsync(rootClient, tenantId);
        return new OrganizationContext(tenantId, adminEmail);
    }

    private async Task<HttpClient> CreateOrganizationAdminClientAsync(OrganizationContext organization)
    {
        for (int attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                return await _auth.CreateAuthenticatedClientAsync(
                    organization.AdminEmail,
                    TestConstants.DefaultPassword,
                    organization.TenantId);
            }
            catch (HttpRequestException) when (attempt < 29)
            {
                await Task.Delay(1000);
            }
        }

        return await _auth.CreateAuthenticatedClientAsync(
            organization.AdminEmail,
            TestConstants.DefaultPassword,
            organization.TenantId);
    }

    private static async Task WaitForProvisioningAsync(HttpClient rootClient, string tenantId)
    {
        for (int attempt = 0; attempt < 60; attempt++)
        {
            var response = await rootClient.GetAsync($"{TestConstants.TenantsBasePath}/{tenantId}/provisioning");
            if (response.IsSuccessStatusCode)
            {
                string content = await response.Content.ReadAsStringAsync();
                if (content.Contains("Completed", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (content.Contains("Failed", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Organization provisioning failed: {content}");
                }
            }

            await Task.Delay(1000);
        }

        throw new TimeoutException($"Organization {tenantId} did not finish provisioning.");
    }

    private static async Task<RegisterUserResponse> RegisterUserAsync(HttpClient client, string prefix)
    {
        string suffix = Guid.NewGuid().ToString("N")[..12];
        string email = $"{prefix}-{suffix}@example.com";
        var response = await client.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/register", new
        {
            firstName = "Lifecycle",
            lastName = "Test",
            email,
            userName = $"{prefix}-{suffix}",
            password = DefaultPassword,
            confirmPassword = DefaultPassword
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var registered = await response.DeserializeAsync<RegisterUserResponse>();
        return registered with { Email = email };
    }

    private static async Task<RoleDto> GetRoleAsync(HttpClient client, string name)
    {
        var response = await client.GetAsync($"{TestConstants.IdentityBasePath}/roles");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var roles = await response.DeserializeAsync<PagedResponse<RoleDto>>();
        return roles.Items.Single(role => role.Name == name);
    }

    private static async Task<RoleDto> CreateRoleManagerRoleAsync(HttpClient client)
    {
        string name = $"RoleManager-{Guid.NewGuid():N}";
        var createResponse = await client.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/roles", new
        {
            id = string.Empty,
            name,
            description = "May manage administrator lifecycle without being an administrator"
        });
        createResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await createResponse.Content.ReadAsStringAsync());
        var role = await createResponse.DeserializeAsync<RoleDto>();

        var permissionResponse = await client.PutAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/{role.Id}/permissions",
            new { roleId = role.Id, permissions = new[] { IdentityPermissions.Users.ManageRoles } });
        permissionResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await permissionResponse.Content.ReadAsStringAsync());
        return role;
    }

    private static Task<HttpResponseMessage> SetAdminRoleAsync(
        HttpClient client,
        string userId,
        RoleDto adminRole,
        bool enabled)
        => SetRoleAsync(client, userId, adminRole, enabled);

    private static Task<HttpResponseMessage> SetRoleAsync(
        HttpClient client,
        string userId,
        RoleDto role,
        bool enabled)
        => client.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/users/{userId}/roles", new
        {
            userId,
            userRoles = new[]
            {
                new { roleId = role.Id, roleName = role.Name, enabled }
            }
        });

    private static async Task<bool> IsAdminAsync(HttpClient client, string userId)
    {
        var response = await client.GetAsync($"{TestConstants.IdentityBasePath}/users/{userId}/roles");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var roles = await response.DeserializeAsync<List<UserRoleDto>>();
        return roles.Single(role => role.RoleName == RoleConstants.Admin).Enabled;
    }

    private static async Task<string> GetCurrentUserIdAsync(HttpClient client)
    {
        var response = await client.GetAsync($"{TestConstants.IdentityBasePath}/profile");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.DeserializeAsync<UserDto>()).Id
            ?? throw new InvalidOperationException("The current profile did not contain a user ID.");
    }

    private async Task<HttpClient> EnableMfaAndCreateClientAsync(
        string email,
        string tenantId,
        string password = TestConstants.DefaultPassword)
    {
        string? authenticatorKey = null;
        await WithTenantScopeAsync(tenantId, async userManager =>
        {
            var user = await userManager.FindByEmailAsync(email);
            user.ShouldNotBeNull();
            await userManager.ResetAuthenticatorKeyAsync(user!);
            (await userManager.SetTwoFactorEnabledAsync(user!, true)).Succeeded.ShouldBeTrue();
            authenticatorKey = await userManager.GetAuthenticatorKeyAsync(user!);
        });
        authenticatorKey.ShouldNotBeNullOrWhiteSpace();

        string code = GenerateAuthenticatorCode(authenticatorKey!);
        using var loginClient = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"{TestConstants.IdentityBasePath}/token/issue");
        request.Headers.Add("tenant", tenantId);
        request.Content = JsonContent.Create(new { email, password, twoFactorCode = code });
        var response = await loginClient.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var token = await response.DeserializeAsync<TokenResult>();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token.AccessToken);
        client.DefaultRequestHeaders.Add("tenant", tenantId);
        return client;
    }

    private async Task WithTenantScopeAsync(string tenantId, Func<UserManager<FshUser>, Task> action)
    {
        using var scope = _factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider
            .GetRequiredService<IMultiTenantStore<AppTenantInfo>>()
            .GetAsync(tenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>()
            .MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);
        await action(scope.ServiceProvider.GetRequiredService<UserManager<FshUser>>());
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "RFC 6238 authenticator interoperability requires HMAC-SHA1.")]
    private static string GenerateAuthenticatorCode(string base32Key)
    {
        byte[] key = DecodeBase32(base32Key);
        long counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        Span<byte> counterBytes = stackalloc byte[sizeof(long)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counterBytes, counter);
        Span<byte> hash = HMACSHA1.HashData(key, counterBytes);
        int offset = hash[^1] & 0x0f;
        int binary = ((hash[offset] & 0x7f) << 24)
            | (hash[offset + 1] << 16)
            | (hash[offset + 2] << 8)
            | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static byte[] DecodeBase32(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        string normalized = value.TrimEnd('=').ToUpperInvariant();
        var result = new byte[normalized.Length * 5 / 8];
        int buffer = 0;
        int bits = 0;
        int index = 0;
        foreach (char character in normalized)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(character, StringComparison.Ordinal);
            bits += 5;
            if (bits < 8)
            {
                continue;
            }

            result[index++] = (byte)(buffer >> (bits - 8));
            bits -= 8;
            buffer &= (1 << bits) - 1;
        }

        return result;
    }

    private static async Task<string> PollSecurityAuditAsync(
        HttpClient client,
        SecurityAction action,
        string actorId,
        string reasonCode)
    {
        for (int attempt = 0; attempt < AuditTestHelper.DefaultPollAttempts; attempt++)
        {
            var rows = await AuditTestHelper.GetListAsync(
                client,
                $"/security?action={action}&userId={Uri.EscapeDataString(actorId)}");
            foreach (var row in rows)
            {
                var detail = await AuditTestHelper.GetByIdAsync(client, row.Id);
                string payload = detail!.Payload.GetRawText();
                if (payload.Contains(reasonCode, StringComparison.Ordinal))
                {
                    return payload;
                }
            }

            await Task.Delay(AuditTestHelper.PollInterval);
        }

        throw new TimeoutException($"No {action} audit with reason {reasonCode} was recorded for {actorId}.");
    }

    private async Task<OrganizationAdminChangedIntegrationEvent> ReadAdminChangeEventAsync(
        string tenantId,
        string targetUserId,
        OrganizationAdminChange change)
    {
        using var scope = _factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider
            .GetRequiredService<IMultiTenantStore<AppTenantInfo>>()
            .GetAsync(tenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>()
            .MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var messages = await db.Set<OutboxMessage>()
            .Where(message => message.TenantId == tenantId)
            .ToListAsync();
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var events = messages
            .Where(message => message.Type.Contains(
                nameof(OrganizationAdminChangedIntegrationEvent),
                StringComparison.Ordinal))
            .Select(message => JsonSerializer.Deserialize<OrganizationAdminChangedIntegrationEvent>(
                message.Payload,
                options))
            .Where(@event => @event is not null)
            .Select(@event => @event!)
            .ToList();
        return events.Single(@event =>
            @event.TenantId == tenantId
            && @event.OrganizationId == tenantId
            && @event.UserId == targetUserId
            && @event.Change == change);
    }

    private static void AssertSafeAdminAudit(string payload, string tenantId, string targetUserId)
    {
        payload.ShouldContain(tenantId);
        payload.ShouldContain(targetUserId);
        payload.ShouldNotContain(DefaultPassword);
        payload.ShouldNotContain(TestConstants.DefaultPassword);
        foreach (string forbidden in new[]
        {
            "twoFactorCode", "sharedKey", "authenticatorUri", "accessToken",
            "refreshToken", "fileContent", "content", "secret", "url"
        })
        {
            payload.Contains(forbidden, StringComparison.OrdinalIgnoreCase).ShouldBeFalse(
                $"Administrator audit disclosed forbidden field '{forbidden}': {payload}");
        }
    }

    private sealed record OrganizationContext(string TenantId, string AdminEmail);

    private sealed record RegisterUserResponse(string UserId, string Email);
}
