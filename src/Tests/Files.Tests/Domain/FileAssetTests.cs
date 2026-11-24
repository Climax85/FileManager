using FSH.Framework.Core.Exceptions;
using FSH.Modules.Files.Contracts.v1.DTOs;
using FSH.Modules.Files.Domain;
using FSH.Modules.Authorization.Contracts;

namespace Files.Tests.Domain;

public class FileAssetTests
{
    private static FileAsset NewPending(Visibility visibility = Visibility.Public) =>
        FileAsset.CreatePending(
            id: Guid.NewGuid(),
            ownerType: "Product",
            ownerId: Guid.NewGuid(),
            originalFileName: "x.png",
            sanitizedFileName: "x.png",
            contentType: "image/png",
            declaredSizeBytes: 1024,
            storageKey: "tenants/t/product/2026/05/abc/x.png",
            visibility: visibility,
            createdByUserId: "user-1",
            uploadDeadline: DateTimeOffset.UtcNow.AddMinutes(15));

    [Fact]
    public void CreatePending_Should_StartInPendingUpload()
    {
        var f = NewPending();
        f.Status.ShouldBe(FileAssetStatus.PendingUpload);
        f.ScanStatus.ShouldBe(ScanStatus.NotScanned);
        f.UploadDeadline.ShouldNotBeNull();
        f.SizeBytes.ShouldBe(1024);
    }

    [Fact]
    public void CreatePending_Should_GenerateIdWhenEmpty()
    {
        var f = FileAsset.CreatePending(
            id: Guid.Empty,
            ownerType: "MyFiles",
            ownerId: null,
            originalFileName: "x.pdf",
            sanitizedFileName: "x.pdf",
            contentType: "application/pdf",
            declaredSizeBytes: 100,
            storageKey: "k",
            visibility: Visibility.Private,
            createdByUserId: "u",
            uploadDeadline: DateTimeOffset.UtcNow.AddMinutes(1));
        f.Id.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public void CreatePending_Should_RejectNegativeSize()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            FileAsset.CreatePending(Guid.NewGuid(), "Product", null, "x.png", "x.png",
                "image/png", -1, "k", Visibility.Public, "u", DateTimeOffset.UtcNow.AddMinutes(1)));
    }

    [Fact]
    public void MarkAvailable_Should_TransitionToAvailable_When_ScanClean()
    {
        var f = NewPending();
        f.MarkAvailable(2048, ScanStatus.Clean);
        f.Status.ShouldBe(FileAssetStatus.Available);
        f.SizeBytes.ShouldBe(2048);
        f.ScanStatus.ShouldBe(ScanStatus.Clean);
        f.UploadDeadline.ShouldBeNull();
        f.UpdatedAtUtc.ShouldNotBeNull();
        f.DomainEvents.ShouldHaveSingleItem();
    }

    [Fact]
    public void MarkAvailable_Should_TransitionToQuarantined_When_ScanInfected()
    {
        var f = NewPending();
        f.MarkAvailable(2048, ScanStatus.Infected);
        f.Status.ShouldBe(FileAssetStatus.Quarantined);
        f.ScanStatus.ShouldBe(ScanStatus.Infected);
    }

    [Theory]
    [InlineData(ScanStatus.NotScanned)]
    [InlineData(ScanStatus.ScanFailed)]
    public void MarkAvailable_Should_NotMakeFileAvailable_WhenScanDidNotSucceed(ScanStatus scanResult)
    {
        var f = NewPending();

        f.MarkAvailable(2048, scanResult);

        f.Status.ShouldNotBe(FileAssetStatus.Available);
        f.ScanStatus.ShouldBe(scanResult);
    }

    [Fact]
    public void MarkAvailable_Should_Reject_When_NotPendingUpload()
    {
        var f = NewPending();
        f.MarkAvailable(2048, ScanStatus.Clean);

        var ex = Should.Throw<CustomException>(() => f.MarkAvailable(4096, ScanStatus.Clean));
        ex.StatusCode.ShouldBe(System.Net.HttpStatusCode.Conflict);
    }

    [Fact]
    public void Restore_Should_BeIdempotent_When_NotDeleted()
    {
        var f = NewPending();
        f.Restore();
        f.IsDeleted.ShouldBeFalse();
    }

    [Fact]
    public void BeginMove_ReplacesParent_AndRequiresConfirmationBeforeAccess()
    {
        Guid oldParent = Guid.CreateVersion7();
        Guid newParent = Guid.CreateVersion7();
        Guid operationId = Guid.CreateVersion7();
        var file = FileAsset.CreatePending(
            Guid.CreateVersion7(), "MyFiles", null, "move.pdf", "move.pdf", "application/pdf",
            100, "key", Visibility.Private, "user", DateTimeOffset.UtcNow.AddMinutes(10),
            oldParent, Guid.CreateVersion7());
        file.MarkAuthorizationConfirmed("initial-token", DateTimeOffset.UtcNow);

        file.BeginMove(newParent, operationId);

        file.ParentFolderId.ShouldBe(newParent);
        file.AuthorizationOperationId.ShouldBe(operationId);
        file.AuthorizationSynchronizationStatus.ShouldBe(RelationshipSynchronizationStatus.Pending);
        file.AuthorizationConfirmed.ShouldBeFalse();
        file.AuthorizationToken.ShouldBeNull();
        file.MarkAuthorizationFailed("outage");
        file.AuthorizationSynchronizationError.ShouldBe("outage");
        file.MarkAuthorizationConfirmed("move-token", DateTimeOffset.UtcNow);
        file.AuthorizationConfirmed.ShouldBeTrue();
        file.AuthorizationToken.ShouldBe("move-token");
    }
}
