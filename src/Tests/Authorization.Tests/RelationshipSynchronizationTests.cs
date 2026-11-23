using FSH.Modules.Authorization.Domain;
using Shouldly;
using Xunit;

namespace Authorization.Tests;

public sealed class RelationshipSynchronizationTests
{
    [Fact]
    public void Confirmation_PersistsWrittenAtToken_AndFailureKeepsSyncPending()
    {
        // Arrange
        var sync = RelationshipSynchronization.Pending(Guid.CreateVersion7(), new OrganizationId("acme"));

        // Act
        sync.MarkFailed("unavailable");

        // Assert
        sync.Status.ShouldBe(RelationshipSynchronizationStatus.Pending);
        sync.LastError.ShouldBe("unavailable");
        sync.ZedToken.ShouldBeNull();

        // Act
        sync.MarkConfirmed(" written-at-token ");

        // Assert
        sync.Status.ShouldBe(RelationshipSynchronizationStatus.Confirmed);
        sync.ZedToken.ShouldBe("written-at-token");
        sync.LastError.ShouldBeNull();
    }
}
