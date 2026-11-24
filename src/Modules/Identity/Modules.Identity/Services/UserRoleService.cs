using System.Data;
using System.Net;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Context;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Eventing.Outbox;
using FSH.Framework.Shared.Constants;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Auditing.Contracts;
using FSH.Modules.Identity.Contracts.DTOs;
using FSH.Modules.Identity.Contracts.Events;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.Identity.Data;
using FSH.Modules.Identity.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Identity.Services;

internal sealed class UserRoleService(
    UserManager<FshUser> userManager,
    RoleManager<FshRole> roleManager,
    IdentityDbContext db,
    IMultiTenantContextAccessor<AppTenantInfo> multiTenantContextAccessor,
    ICurrentUser currentUser,
    IUserPermissionService userPermissionService,
    IAuditClient auditClient,
    IOutboxStore outboxStore,
    TimeProvider timeProvider) : IUserRoleService
{
    public async Task<string> AssignRolesAsync(string userId, List<UserRoleDto> userRoles, CancellationToken cancellationToken)
    {
        var user = await userManager.Users
            .Where(u => u.Id == userId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("user not found");

        string organizationId = multiTenantContextAccessor.MultiTenantContext?.TenantInfo?.Id
            ?? throw new UnauthorizedException("invalid organization");
        bool wasAdministrator = await userManager.IsInRoleAsync(user, RoleConstants.Admin);
        UserRoleDto? finalAdminAssignment = userRoles.LastOrDefault(
            assignment => string.Equals(assignment.RoleName, RoleConstants.Admin, StringComparison.OrdinalIgnoreCase));
        OrganizationAdminChange? adminChange = finalAdminAssignment switch
        {
            { Enabled: true } when !wasAdministrator => OrganizationAdminChange.Added,
            { Enabled: false } when wasAdministrator => OrganizationAdminChange.Removed,
            _ => null
        };

        // Serializable role mutations prevent two concurrent demotions from both observing
        // another administrator and leaving the organization with none.
        await using var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);

        if (adminChange.HasValue)
        {
            await ValidateAdminRoleChangeAsync(user, adminChange.Value, organizationId, cancellationToken)
                .ConfigureAwait(false);
        }

        var assignedRoles = await ProcessRoleAssignmentsAsync(user, userRoles).ConfigureAwait(false);
        if (assignedRoles.Count > 0)
        {
            user.RecordRolesAssigned(assignedRoles, organizationId);
        }

        if (adminChange.HasValue)
        {
            var integrationEvent = new OrganizationAdminChangedIntegrationEvent(
                Id: Guid.NewGuid(),
                OccurredOnUtc: timeProvider.GetUtcNow().UtcDateTime,
                TenantId: organizationId,
                CorrelationId: System.Diagnostics.Activity.Current?.Id ?? Guid.NewGuid().ToString(),
                Source: "Identity",
                OrganizationId: organizationId,
                UserId: user.Id,
                Change: adminChange.Value);
            await outboxStore.AddAsync(integrationEvent, cancellationToken).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        if (adminChange.HasValue)
        {
            // Record the committed security change even if subsequent cache invalidation fails.
            await AuditAdminChangeAsync(
                adminChange.Value == OrganizationAdminChange.Added
                    ? SecurityAction.RoleAssigned
                    : SecurityAction.RoleRevoked,
                user.Id,
                organizationId,
                adminChange.Value,
                "Succeeded",
                AuditSeverity.Information,
                cancellationToken).ConfigureAwait(false);
        }

        // Any role mutation (add or remove) invalidates the cached permission set.
        await userPermissionService.InvalidatePermissionCacheAsync(userId, cancellationToken).ConfigureAwait(false);

        return "User Roles Updated Successfully.";
    }

    public async Task<List<UserRoleDto>> GetUserRolesAsync(string userId, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId)
            ?? throw new NotFoundException("user not found");

        var roles = await roleManager.Roles.AsNoTracking().ToListAsync(cancellationToken)
            ?? throw new NotFoundException("roles not found");

        // Single membership query instead of one IsInRoleAsync round-trip per role.
        var memberships = await userManager.GetRolesAsync(user);
        var membershipSet = new HashSet<string>(memberships, StringComparer.OrdinalIgnoreCase);

        var userRoles = new List<UserRoleDto>();
        foreach (var role in roles)
        {
            userRoles.Add(new UserRoleDto
            {
                RoleId = role.Id,
                RoleName = role.Name,
                Description = role.Description,
                Enabled = membershipSet.Contains(role.Name!)
            });
        }

        return userRoles;
    }

    private async Task ValidateAdminRoleChangeAsync(
        FshUser user,
        OrganizationAdminChange change,
        string organizationId,
        CancellationToken cancellationToken)
    {
        Guid actorId = currentUser.GetUserId();
        var actor = await userManager.FindByIdAsync(actorId.ToString());
        if (actor?.TwoFactorEnabled != true || !HasCompletedMfa())
        {
            await AuditAdminChangeAsync(
                SecurityAction.PolicyFailed,
                user.Id,
                organizationId,
                change,
                "MfaRequired",
                AuditSeverity.Warning,
                cancellationToken).ConfigureAwait(false);
            throw new ForbiddenException(
                "Multi-factor authentication is required to change organization administrators.");
        }

        if (change == OrganizationAdminChange.Added)
        {
            return;
        }

        if (string.Equals(actorId.ToString(), user.Id, StringComparison.Ordinal))
        {
            await AuditAdminChangeAsync(
                SecurityAction.PolicyFailed,
                user.Id,
                organizationId,
                change,
                "SelfDemotionBlocked",
                AuditSeverity.Warning,
                cancellationToken).ConfigureAwait(false);
            throw new CustomException(
                "Administrators cannot remove their own administrator role.",
                Array.Empty<string>(),
                HttpStatusCode.BadRequest);
        }

        // This protects the framework recovery account; it does not grant the root
        // operator access to another organization or bypass its MFA requirement.
        if (IsRootTenantAdmin(user))
        {
            await AuditAdminChangeAsync(
                SecurityAction.PolicyFailed,
                user.Id,
                organizationId,
                change,
                "RootAdministratorProtected",
                AuditSeverity.Warning,
                cancellationToken).ConfigureAwait(false);
            throw new ForbiddenException("The root tenant administrator cannot be demoted.");
        }

        int adminCount = (await userManager.GetUsersInRoleAsync(RoleConstants.Admin)).Count;
        if (adminCount <= 1)
        {
            await AuditAdminChangeAsync(
                SecurityAction.PolicyFailed,
                user.Id,
                organizationId,
                change,
                "LastAdministrator",
                AuditSeverity.Warning,
                cancellationToken).ConfigureAwait(false);
            throw new CustomException(
                "Organization must retain at least one administrator.",
                Array.Empty<string>(),
                HttpStatusCode.BadRequest);
        }
    }

    private bool HasCompletedMfa()
        => currentUser.GetUserClaims()?.Any(
            claim => claim.Type == "amr" && claim.Value == "mfa") is true;

    private bool IsRootTenantAdmin(FshUser user)
    {
        return user.Email == MultitenancyConstants.Root.EmailAddress
            && multiTenantContextAccessor.MultiTenantContext?.TenantInfo?.Id == MultitenancyConstants.Root.Id;
    }

    private async Task<List<string>> ProcessRoleAssignmentsAsync(FshUser user, List<UserRoleDto> userRoles)
    {
        var assignedRoles = new List<string>();

        foreach (var userRole in userRoles)
        {
            if (await roleManager.FindByNameAsync(userRole.RoleName!) is null)
            {
                continue;
            }

            bool isAssigned = await userManager.IsInRoleAsync(user, userRole.RoleName!);
            if (userRole.Enabled && !isAssigned)
            {
                IdentityResult result = await userManager.AddToRoleAsync(user, userRole.RoleName!);
                EnsureSucceeded(result, "Assign role failed");
                assignedRoles.Add(userRole.RoleName!);
            }
            else if (!userRole.Enabled && isAssigned)
            {
                IdentityResult result = await userManager.RemoveFromRoleAsync(user, userRole.RoleName!);
                EnsureSucceeded(result, "Remove role failed");
            }
        }

        return assignedRoles;
    }

    private static void EnsureSucceeded(IdentityResult result, string message)
    {
        if (!result.Succeeded)
        {
            throw new CustomException(
                message,
                result.Errors.Select(error => error.Description).ToList(),
                HttpStatusCode.BadRequest);
        }
    }

    private async Task AuditAdminChangeAsync(
        SecurityAction action,
        string targetUserId,
        string organizationId,
        OrganizationAdminChange change,
        string reasonCode,
        AuditSeverity severity,
        CancellationToken cancellationToken)
    {
        string actorId = currentUser.GetUserId().ToString();
        var claims = new Dictionary<string, object?>
        {
            ["actorId"] = actorId,
            ["targetUserId"] = targetUserId,
            ["organizationId"] = organizationId,
            ["change"] = change.ToString()
        };

        await auditClient.WriteSecurityAsync(
            action,
            subjectId: actorId,
            authMethod: HasCompletedMfa() ? "MFA" : "Password",
            reasonCode: reasonCode,
            claims: claims,
            severity: severity,
            source: "Identity",
            ct: cancellationToken).ConfigureAwait(false);
    }
}