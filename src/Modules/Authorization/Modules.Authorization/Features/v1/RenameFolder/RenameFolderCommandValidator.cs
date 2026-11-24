using FluentValidation;
using FSH.Modules.Authorization.Contracts.v1.RenameFolder;

namespace FSH.Modules.Authorization.Features.v1.RenameFolder;

public sealed class RenameFolderCommandValidator : AbstractValidator<RenameFolderCommand>
{
    public RenameFolderCommandValidator()
    {
        RuleFor(x => x.FolderId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(256);
    }
}
