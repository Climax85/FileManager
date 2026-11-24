using FluentValidation;
using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Authorization.Contracts.v1.MoveResource;

namespace FSH.Modules.Authorization.Features.v1.MoveResource;

public sealed class MoveResourceCommandValidator : AbstractValidator<MoveResourceCommand>
{
    public MoveResourceCommandValidator()
    {
        RuleFor(x => x.ResourceId).NotEmpty();
        RuleFor(x => x.DestinationFolderId).NotEmpty();
        RuleFor(x => x.ResourceType)
            .Must(x => x is AuthorizationObjectType.File or AuthorizationObjectType.Folder)
            .WithMessage("Only file and folder resources can be moved.");
    }
}
