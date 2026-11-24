using FSH.Framework.Eventing.Abstractions;
using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Authorization.Contracts.Events;
using FSH.Modules.Files.Data;
using FSH.Modules.Files.Domain;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Files.IntegrationEventHandlers;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "IntegrationEventHandler is the established messaging contract convention.")]
public sealed class AuthorizationRelationshipsChangedIntegrationEventHandler(
    IRelationshipWriter writer,
    FilesDbContext db)
    : IIntegrationEventHandler<AuthorizationRelationshipsChangedIntegrationEvent>
{
    public async Task HandleAsync(
        AuthorizationRelationshipsChangedIntegrationEvent @event,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        FileAsset? asset = await db.FileAssets
            .SingleOrDefaultAsync(x => x.AuthorizationOperationId == @event.Id, ct)
            .ConfigureAwait(false);
        if (asset is null)
        {
            return;
        }

        RelationshipConfirmation confirmation = await writer.WriteAsync(@event.Id, @event.Mutations, ct).ConfigureAwait(false);
        asset.MarkAuthorizationConfirmed(confirmation.WrittenAt.Value, confirmation.ConfirmedAt);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
