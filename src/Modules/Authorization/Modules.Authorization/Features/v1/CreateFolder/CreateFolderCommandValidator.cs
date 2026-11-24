using FluentValidation;
using FSH.Modules.Authorization.Contracts.v1.CreateFolder;

namespace FSH.Modules.Authorization.Features.v1.CreateFolder;

public sealed class CreateFolderCommandValidator : AbstractValidator<CreateFolderCommand>
{
    public CreateFolderCommandValidator()
    {
        RuleFor(x => x.ParentId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(256);
    }
}
