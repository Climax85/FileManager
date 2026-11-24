using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Authorization.Domain;
using Shouldly;
using Xunit;

namespace Authorization.Tests;

public sealed class ResourceGrantTests
{
    private static readonly OrganizationId Organization = new("root");
    private static readonly AuthorizationObject Resource = AuthorizationObjects.For(FolderId.New());
    private static readonly AuthorizationSubject Subject = AuthorizationSubjects.For(new UserId(Guid.NewGuid().ToString("D")));

    [Fact]
    public void PendingGrant_IsNotEffective_UntilProviderConfirmation()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ResourceGrant grant = ResourceGrant.CreatePending(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Organization, Resource, Subject,
            AuthorizationRelation.Editor, now);

        grant.Status.ShouldBe(ResourceGrantStatus.Pending);
        grant.IsEffective.ShouldBeFalse();
        grant.ConfirmedAt.ShouldBeNull();

        grant.MarkConfirmed("zed-token", now.AddSeconds(1));

        grant.Status.ShouldBe(ResourceGrantStatus.Active);
        grant.IsEffective.ShouldBeTrue();
        grant.ConfirmedAt.ShouldBe(now.AddSeconds(1));
    }

    [Fact]
    public void PendingRevocation_IsImmediatelyIneffective_BeforeProviderConfirmation()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ResourceGrant grant = ResourceGrant.CreatePending(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Organization, Resource, Subject,
            AuthorizationRelation.Viewer, now);
        grant.MarkConfirmed("grant-token", now);

        grant.BeginRevocation(Guid.CreateVersion7(), now.AddSeconds(1));

        grant.Status.ShouldBe(ResourceGrantStatus.RevocationPending);
        grant.IsEffective.ShouldBeFalse();

        grant.MarkRevoked("revoke-token", now.AddSeconds(2));
        grant.Status.ShouldBe(ResourceGrantStatus.Revoked);
        grant.IsEffective.ShouldBeFalse();
    }

    [Fact]
    public void FailedProviderWrite_RestoresPendingFailClosedState()
    {
        ResourceGrant grant = ResourceGrant.CreatePending(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Organization, Resource, Subject,
            AuthorizationRelation.Manager, DateTimeOffset.UtcNow);

        grant.MarkSynchronizationFailed("unavailable");

        grant.Status.ShouldBe(ResourceGrantStatus.Pending);
        grant.IsEffective.ShouldBeFalse();
        grant.LastError.ShouldBe("unavailable");
    }
}
