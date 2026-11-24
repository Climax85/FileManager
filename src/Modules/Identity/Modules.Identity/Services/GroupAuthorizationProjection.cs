using FSH.Framework.Core.Context;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Eventing.Outbox;
using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Authorization.Contracts.Events;
using FSH.Modules.Identity.Data;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Identity.Services;

public sealed class GroupAuthorizationProjection(
    IdentityDbContext db,
    ICurrentUser currentUser,
    IRelationshipWriter relationshipWriter,
    EfCoreOutboxStore<IdentityDbContext> outbox,
    TimeProvider timeProvider)
{
    public ValueTask SaveCreatedAsync(Guid groupId, CancellationToken cancellationToken) =>
        SaveAndConfirmAsync(groupId, [], [], includeOrganization: true, cancellationToken);

    public ValueTask SaveMembershipChangesAsync(
        Guid groupId,
        IReadOnlyCollection<string> addedUserIds,
        IReadOnlyCollection<string> removedUserIds,
        CancellationToken cancellationToken) =>
        SaveAndConfirmAsync(groupId, addedUserIds, removedUserIds, addedUserIds.Count > 0, cancellationToken);

    public ValueTask SaveDeletedAsync(
        Guid groupId,
        IReadOnlyCollection<string> memberIds,
        CancellationToken cancellationToken) =>
        SaveAndConfirmAsync(groupId, [], memberIds, includeOrganization: true, cancellationToken);

    private async ValueTask SaveAndConfirmAsync(
        Guid groupId,
        IReadOnlyCollection<string> addedUserIds,
        IReadOnlyCollection<string> removedUserIds,
        bool includeOrganization,
        CancellationToken cancellationToken)
    {
        string? tenant = currentUser.GetTenant();
        if (string.IsNullOrWhiteSpace(tenant)) throw new NotFoundException("Group not found.");

        var group = AuthorizationObjects.For(new GroupId(groupId));
        var organization = AuthorizationObjects.For(new OrganizationId(tenant));
        List<RelationshipMutation> mutations = [];
        if (includeOrganization)
        {
            mutations.Add(RelationshipMutation.Touch(
                group, AuthorizationRelation.Organization, new AuthorizationSubject(organization)));
        }

        foreach (string userId in addedUserIds)
        {
            mutations.Add(RelationshipMutation.Touch(
                group, AuthorizationRelation.Member, AuthorizationSubjects.For(new UserId(userId))));
        }

        foreach (string userId in removedUserIds)
        {
            mutations.Add(RelationshipMutation.Delete(
                group, AuthorizationRelation.Member, AuthorizationSubjects.For(new UserId(userId))));
        }

        if (mutations.Count == 0)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        Guid operationId = Guid.CreateVersion7();
        DateTime occurredOnUtc = timeProvider.GetUtcNow().UtcDateTime;
        var @event = new AuthorizationRelationshipsChangedIntegrationEvent(
            operationId, occurredOnUtc, tenant, operationId.ToString("D"), "Identity", mutations);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await outbox.AddAsync(@event, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        RelationshipConfirmation confirmation = await relationshipWriter.WriteAsync(
            operationId, mutations, cancellationToken).ConfigureAwait(false);
        OutboxMessage message = await db.OutboxMessages.SingleAsync(
            x => x.Id == operationId, cancellationToken).ConfigureAwait(false);
        message.ProcessedOnUtc = confirmation.ConfirmedAt.UtcDateTime;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
