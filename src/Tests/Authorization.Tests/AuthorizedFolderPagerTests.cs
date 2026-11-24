using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Authorization.Domain;
using FSH.Modules.Authorization.Services;
using Shouldly;
using Xunit;

namespace Authorization.Tests;

public sealed class AuthorizedFolderPagerTests
{
    private static readonly OrganizationId Organization = new("acme");
    private static readonly FolderId ParentId = FolderId.New();
    private static readonly UserId UserId = new(Guid.NewGuid().ToString("D"));
    private static readonly AuthorizationToken Token = new("zed-token");

    [Fact]
    public async Task PageIsFilledAcrossRepeatedBatches_BeforeAdvancingTheCursor()
    {
        Folder[] folders = Enumerable.Range(0, 5).Select(CreateFolder).ToArray();
        HashSet<Guid> denied = [folders[0].Id.Value, folders[1].Id.Value];
        var checker = new StubPermissionChecker(denied);
        var loader = new StubBatchLoader(folders);

        FolderPageResult first = await AuthorizedFolderPager.ReadAsync(
            ParentId, UserId, Token, 2, null, loader.LoadAsync, checker, CancellationToken.None);

        first.Items.Select(x => x.Id).ShouldBe([folders[2].Id, folders[3].Id]);
        first.NextCursor.ShouldNotBeNull();
        first.NextCursor!.Id.ShouldBe(folders[3].Id);
        loader.BatchCount.ShouldBe(2);
        checker.RequestedItems.Count.ShouldBe(4);

        FolderPageResult second = await AuthorizedFolderPager.ReadAsync(
            ParentId, UserId, Token, 2, first.NextCursor, loader.LoadAsync, checker, CancellationToken.None);

        second.Items.Select(x => x.Id).ShouldBe([folders[4].Id]);
        second.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task UnavailableAndDeniedChecksAreExcluded_WithoutReturningAShortAuthorizedPage()
    {
        Folder[] folders = Enumerable.Range(0, 4).Select(CreateFolder).ToArray();
        var checker = new StubPermissionChecker([folders[0].Id.Value], unavailable: [folders[1].Id.Value]);
        var loader = new StubBatchLoader(folders);

        FolderPageResult page = await AuthorizedFolderPager.ReadAsync(
            ParentId, UserId, Token, 2, null, loader.LoadAsync, checker, CancellationToken.None);

        page.Items.Select(x => x.Id).ShouldBe([folders[2].Id, folders[3].Id]);
        page.NextCursor.ShouldBeNull();
        loader.BatchCount.ShouldBe(2);
    }

    [Fact]
    public async Task CursorRoundTrip_PreservesTheLastCheckedDatabaseRow()
    {
        Folder folder = CreateFolder(0);

        string encoded = FolderPageCursor.Encode(new FolderPageCursor(folder.CreatedAt, folder.Id));
        FolderPageCursor decoded = FolderPageCursor.Decode(encoded);

        decoded.CreatedAt.ShouldBe(folder.CreatedAt);
        decoded.Id.ShouldBe(folder.Id);
        Action invalid = () => FolderPageCursor.Decode("not-a-cursor");
        invalid.ShouldThrow<ArgumentException>();
    }

    private static Folder CreateFolder(int index) => Folder.Create(
        FolderId.New(), Organization, ParentId, $"Folder {index}",
        new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero).AddMinutes(index));

    private sealed class StubBatchLoader(IReadOnlyList<Folder> folders)
    {
        public int BatchCount { get; private set; }

        public ValueTask<IReadOnlyList<Folder>> LoadAsync(
            FolderId parentId,
            FolderPageCursor? cursor,
            int limit,
            CancellationToken cancellationToken)
        {
            parentId.ShouldBe(ParentId);
            cancellationToken.ShouldBe(CancellationToken.None);
            BatchCount++;
            IEnumerable<Folder> candidates = folders;
            if (cursor is not null)
            {
                candidates = candidates.Where(x =>
                    x.CreatedAt > cursor.CreatedAt || x.CreatedAt == cursor.CreatedAt && x.Id.Value.CompareTo(cursor.Id.Value) > 0);
            }
            return ValueTask.FromResult<IReadOnlyList<Folder>>(candidates.Take(limit).ToArray());
        }
    }

    private sealed class StubPermissionChecker(HashSet<Guid> denied, HashSet<Guid>? unavailable = null) : IPermissionChecker
    {
        public List<PermissionCheck> RequestedItems { get; } = [];

        public ValueTask<PermissionCheckResult> CheckAsync(PermissionCheck request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result(request));

        public ValueTask<IReadOnlyList<PermissionCheckResult>> CheckBulkAsync(
            BulkPermissionCheck request, CancellationToken cancellationToken = default)
        {
            RequestedItems.AddRange(request.Items);
            return ValueTask.FromResult<IReadOnlyList<PermissionCheckResult>>(request.Items.Select(Result).ToArray());
        }

        private PermissionCheckResult Result(PermissionCheck request)
        {
            Guid id = Guid.Parse(request.Resource.ObjectId);
            if (unavailable?.Contains(id) == true)
            {
                return new PermissionCheckResult(request, false, null, AuthorizationFailure.Unavailable);
            }
            return new PermissionCheckResult(request, !denied.Contains(id), Token);
        }
    }
}
