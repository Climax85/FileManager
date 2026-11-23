using FSH.Framework.Eventing.Abstractions;
using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Identity.Contracts.Events;

namespace FSH.Modules.Authorization.IntegrationEventHandlers;

public sealed class OrganizationAdminChangedIntegrationEventHandler(IOrganizationRootProvisioner provisioner)
    : IIntegrationEventHandler<OrganizationAdminChangedIntegrationEvent>
{
    public async Task HandleAsync(OrganizationAdminChangedIntegrationEvent @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        if (@event.Change == OrganizationAdminChange.None) return;
        if (string.IsNullOrWhiteSpace(@event.TenantId)
            || !string.Equals(@event.TenantId, @event.OrganizationId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Administrator event organization and tenant boundaries do not match.");
        }

        await provisioner.SynchronizeAdministratorAsync(
            new OrganizationId(@event.OrganizationId),
            new UserId(@event.UserId),
            @event.Change == OrganizationAdminChange.Added,
            ct).ConfigureAwait(false);
    }
}
