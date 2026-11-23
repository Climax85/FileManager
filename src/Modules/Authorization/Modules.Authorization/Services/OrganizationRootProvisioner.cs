using FSH.Framework.Eventing.Outbox;
using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Authorization.Contracts.Events;
using FSH.Modules.Authorization.Data;
using FSH.Modules.Authorization.Domain;
using FSH.Modules.Identity.Contracts.Services;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Authorization.Services;

public sealed class OrganizationRootProvisioner(
    AuthorizationDbContext db,
    IOrganizationAdminReader administrators,
    IRelationshipWriter relationshipWriter,
    EfCoreOutboxStore<AuthorizationDbContext> outbox,
    TimeProvider timeProvider)
    : IOrganizationRootProvisioner
{
    public async ValueTask<OrganizationRoot> EnsureRootAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken = default)
    {
        Folder? root = await db.Folders
            .SingleOrDefaultAsync(folder => folder.OrganizationId == organizationId && folder.IsRoot, cancellationToken)
            .ConfigureAwait(false);
        if (root is not null && root.SynchronizationId is null)
        {
            throw new InvalidOperationException("The organization root is missing its synchronization state.");
        }
        if (root?.SynchronizationId is Guid existingOperationId)
        {
            RelationshipSynchronization existingSync = await db.RelationshipSyncs
                .SingleAsync(sync => sync.OperationId == existingOperationId, cancellationToken)
                .ConfigureAwait(false);
            if (existingSync.Status == RelationshipSynchronizationStatus.Confirmed)
            {
                return ToContract(root, existingSync);
            }

            RelationshipConfirmation retried = await ConfirmAsync(
                existingSync,
                await RootMutationsAsync(root, cancellationToken).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
            return ToContract(root, existingSync, retried);
        }

        Guid operationId = Guid.CreateVersion7();
        DateTimeOffset now = timeProvider.GetUtcNow();
        IReadOnlyList<UserId> adminIds = await administrators.GetAdministratorIdsAsync(cancellationToken).ConfigureAwait(false);
        if (adminIds.Count == 0)
        {
            throw new InvalidOperationException("An organization root requires at least one active administrator.");
        }

        root = Folder.CreateRoot(organizationId, now, operationId);
        var sync = RelationshipSynchronization.Pending(operationId, organizationId);
        IReadOnlyCollection<RelationshipMutation> mutations = RootMutations(root, adminIds);
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await db.Organizations.SingleOrDefaultAsync(x => x.Id == organizationId, cancellationToken).ConfigureAwait(false) is null)
            {
                db.Organizations.Add(new OrganizationProjection(organizationId, now));
            }
            db.Folders.Add(root);
            db.RelationshipSyncs.Add(sync);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await outbox.AddAsync(Event(operationId, organizationId, mutations), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        RelationshipConfirmation confirmation = await ConfirmAsync(sync, mutations, cancellationToken).ConfigureAwait(false);
        return ToContract(root, sync, confirmation);
    }

    public async ValueTask<RelationshipConfirmation> SynchronizeAdministratorAsync(
        OrganizationId organizationId,
        UserId userId,
        bool isAdministrator,
        CancellationToken cancellationToken = default)
    {
        OrganizationRoot root = await EnsureRootAsync(organizationId, cancellationToken).ConfigureAwait(false);
        AuthorizationObject organization = AuthorizationObjects.For(organizationId);
        AuthorizationObject folder = AuthorizationObjects.For(root.Id);
        AuthorizationSubject user = AuthorizationSubjects.For(userId);
        RelationshipMutation[] mutations = isAdministrator
            ?
            [
                RelationshipMutation.Touch(organization, AuthorizationRelation.Admin, user),
                RelationshipMutation.Touch(organization, AuthorizationRelation.Member, user),
                RelationshipMutation.Touch(folder, AuthorizationRelation.Manager, user),
            ]
            :
            [
                RelationshipMutation.Delete(organization, AuthorizationRelation.Admin, user),
                RelationshipMutation.Delete(folder, AuthorizationRelation.Manager, user),
            ];
        Guid operationId = Guid.CreateVersion7();
        var sync = RelationshipSynchronization.Pending(operationId, organizationId);
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            db.RelationshipSyncs.Add(sync);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await outbox.AddAsync(Event(operationId, organizationId, mutations), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        return await ConfirmAsync(sync, mutations, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<RelationshipConfirmation> ConfirmAsync(
        RelationshipSynchronization sync,
        IReadOnlyCollection<RelationshipMutation> mutations,
        CancellationToken cancellationToken)
    {
        try
        {
            RelationshipConfirmation confirmation = await relationshipWriter
                .WriteAsync(sync.OperationId, mutations, cancellationToken)
                .ConfigureAwait(false);
            sync.MarkConfirmed(confirmation.WrittenAt.Value);
            OutboxMessage? message = await db.OutboxMessages
                .SingleOrDefaultAsync(x => x.Id == sync.OperationId, cancellationToken)
                .ConfigureAwait(false);
            if (message is not null) message.ProcessedOnUtc = confirmation.ConfirmedAt.UtcDateTime;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return confirmation;
        }
        catch (RpcException exception)
        {
            sync.MarkFailed(exception.Status.Detail);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<IReadOnlyCollection<RelationshipMutation>> RootMutationsAsync(
        Folder root,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<UserId> adminIds = await administrators.GetAdministratorIdsAsync(cancellationToken).ConfigureAwait(false);
        if (adminIds.Count == 0)
        {
            throw new InvalidOperationException("An organization root requires at least one active administrator.");
        }
        return RootMutations(root, adminIds);
    }

    private static IReadOnlyCollection<RelationshipMutation> RootMutations(
        Folder root,
        IReadOnlyList<UserId> adminIds)
    {
        AuthorizationObject organization = AuthorizationObjects.For(root.OrganizationId);
        AuthorizationObject folder = AuthorizationObjects.For(root.Id);
        AuthorizationSubject organizationSubject = new(organization);
        var mutations = new List<RelationshipMutation>
        {
            RelationshipMutation.Touch(folder, AuthorizationRelation.Organization, organizationSubject),
        };
        foreach (UserId adminId in adminIds)
        {
            AuthorizationSubject admin = AuthorizationSubjects.For(adminId);
            mutations.Add(RelationshipMutation.Touch(organization, AuthorizationRelation.Admin, admin));
            mutations.Add(RelationshipMutation.Touch(organization, AuthorizationRelation.Member, admin));
            mutations.Add(RelationshipMutation.Touch(folder, AuthorizationRelation.Manager, admin));
        }
        return mutations;
    }

    private AuthorizationRelationshipsChangedIntegrationEvent Event(
        Guid operationId,
        OrganizationId organizationId,
        IReadOnlyCollection<RelationshipMutation> mutations) =>
        new(
            operationId,
            timeProvider.GetUtcNow().UtcDateTime,
            organizationId.Value,
            operationId.ToString("D"),
            "Authorization",
            mutations);

    private static OrganizationRoot ToContract(
        Folder root,
        RelationshipSynchronization sync,
        RelationshipConfirmation? confirmation = null) =>
        new(
            root.Id,
            root.OrganizationId,
            root.Name,
            root.CreatedAt,
            confirmation?.WrittenAt ?? (sync.ZedToken is null ? null : new AuthorizationToken(sync.ZedToken)));
}
