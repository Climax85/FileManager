using FSH.Framework.Eventing.Abstractions;

namespace FSH.Modules.Identity.Contracts.Events;

public enum OrganizationAdminChange
{
    Added = 1,
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
