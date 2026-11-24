using FSH.Framework.Eventing.Abstractions;
using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Authorization.Contracts.Events;
using FSH.Modules.Authorization.Data;
using FSH.Modules.Authorization.Domain;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Authorization.IntegrationEventHandlers;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "IntegrationEventHandler is the established messaging contract convention.")]
public sealed class AuthorizationRelationshipsChangedIntegrationEventHandler(
    IRelationshipWriter writer,
    AuthorizationDbContext db)
    : IIntegrationEventHandler<AuthorizationRelationshipsChangedIntegrationEvent>
{
    public async Task HandleAsync(
        AuthorizationRelationshipsChangedIntegrationEvent @event,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        var sync = await db.RelationshipSyncs.SingleOrDefaultAsync(x => x.OperationId == @event.Id, ct).ConfigureAwait(false);
        if (sync is null)
        {
            if (string.IsNullOrWhiteSpace(@event.TenantId))
                throw new InvalidOperationException("An organization is required for relationship synchronization.");
            sync = RelationshipSynchronization.Pending(@event.Id, new OrganizationId(@event.TenantId));
            db.RelationshipSyncs.Add(sync);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        RelationshipConfirmation confirmation = await writer.WriteAsync(@event.Id, @event.Mutations, ct).ConfigureAwait(false);
        var grant = await db.ResourceGrants.SingleOrDefaultAsync(x => x.OperationId == @event.Id, ct).ConfigureAwait(false);
        sync.MarkConfirmed(confirmation.WrittenAt.Value);
        if (grant?.Status == ResourceGrantStatus.Pending)
            grant.MarkConfirmed(confirmation.WrittenAt.Value, confirmation.ConfirmedAt);
        if (grant?.Status == ResourceGrantStatus.RevocationPending)
            grant.MarkRevoked(confirmation.WrittenAt.Value, confirmation.ConfirmedAt);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
