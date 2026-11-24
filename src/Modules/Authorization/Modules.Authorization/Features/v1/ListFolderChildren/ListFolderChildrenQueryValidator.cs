using FluentValidation;
using FSH.Modules.Authorization.Contracts.v1.ListFolderChildren;

namespace FSH.Modules.Authorization.Features.v1.ListFolderChildren;

public sealed class ListFolderChildrenQueryValidator : AbstractValidator<ListFolderChildrenQuery>
{
    public ListFolderChildrenQueryValidator()
    {
        RuleFor(x => x.FolderId).NotEmpty();
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
    }
}
