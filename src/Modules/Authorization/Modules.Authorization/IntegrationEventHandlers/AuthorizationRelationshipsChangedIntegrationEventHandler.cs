using FSH.Framework.Eventing.Abstractions;
using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Authorization.Contracts.Events;
using FSH.Modules.Authorization.Data;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Authorization.IntegrationEventHandlers;

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
        RelationshipConfirmation confirmation = await writer.WriteAsync(@event.Id, @event.Mutations, ct).ConfigureAwait(false);
        var sync = await db.RelationshipSyncs.SingleOrDefaultAsync(x => x.OperationId == @event.Id, ct).ConfigureAwait(false);
        var grant = await db.ResourceGrants.SingleOrDefaultAsync(x => x.OperationId == @event.Id, ct).ConfigureAwait(false);
        if (sync is not null)
        {
            sync.MarkConfirmed(confirmation.WrittenAt.Value);
            if (grant?.Status == ResourceGrantStatus.Pending)
                grant.MarkConfirmed(confirmation.WrittenAt.Value, confirmation.ConfirmedAt);
            if (grant?.Status == ResourceGrantStatus.RevocationPending)
                grant.MarkRevoked(confirmation.WrittenAt.Value, confirmation.ConfirmedAt);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }
}
