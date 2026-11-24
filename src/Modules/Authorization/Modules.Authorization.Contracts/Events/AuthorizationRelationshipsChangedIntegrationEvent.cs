using FSH.Framework.Eventing.Abstractions;

namespace FSH.Modules.Authorization.Contracts.Events;

public sealed record AuthorizationRelationshipsChangedIntegrationEvent(
    Guid Id,
    DateTime OccurredOnUtc,
    string? TenantId,
    string CorrelationId,
    string Source,
    IReadOnlyCollection<RelationshipMutation> Mutations)
    : IIntegrationEvent;
