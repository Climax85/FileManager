using FSH.Framework.Eventing.Abstractions;

namespace FSH.Modules.Identity.Contracts.Events;

/// <summary>Describes a confirmed organization-administrator membership change.</summary>
public enum OrganizationAdminChange
{
    /// <summary>No administrator membership change.</summary>
    None = 0,

    /// <summary>Administrator membership was added.</summary>
    Added = 1,

    /// <summary>Administrator membership was removed.</summary>
    Removed = 2
}

/// <summary>
/// Announces a committed organization-administrator membership change. The Authorization
/// module consumes this provider-neutral contract to synchronize root-manager access;
/// Identity remains independent of SpiceDB and no platform-operator access is implied.
/// </summary>
public sealed record OrganizationAdminChangedIntegrationEvent(
    Guid Id,
    DateTime OccurredOnUtc,
    string? TenantId,
    string CorrelationId,
    string Source,
    string OrganizationId,
    string UserId,
    OrganizationAdminChange Change)
    : IIntegrationEvent;
