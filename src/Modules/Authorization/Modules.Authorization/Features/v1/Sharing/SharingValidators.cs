using FluentValidation;
using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Authorization.Contracts.v1.Sharing;

namespace FSH.Modules.Authorization.Features.v1.Sharing;

public sealed class CreateResourceGrantCommandValidator : AbstractValidator<CreateResourceGrantCommand>
{
    public CreateResourceGrantCommandValidator()
    {
        RuleFor(x => x.ResourceType).Must(x => x is AuthorizationObjectType.File or AuthorizationObjectType.Folder)
            .WithMessage("Only file and folder resources can be shared.");
        RuleFor(x => x.ResourceId).NotEmpty();
        RuleFor(x => x.SubjectType).Must(x => x is AuthorizationObjectType.User or AuthorizationObjectType.Organization)
            .WithMessage("Sharing subjects must be users or the explicit organization member set.");
        RuleFor(x => x.SubjectId).NotEmpty().MaximumLength(128);
        RuleFor(x => x.Relation).Must(x => x is AuthorizationRelation.Viewer or AuthorizationRelation.Editor or AuthorizationRelation.Manager)
            .WithMessage("The grant relation must be viewer, editor, or manager.");
        RuleFor(x => x.ConfirmOrganizationWide).Equal(true)
            .When(x => x.SubjectType == AuthorizationObjectType.Organization)
            .WithMessage("Organization-wide sharing requires explicit confirmation.");
        RuleFor(x => x.Relation).Must(x => x is AuthorizationRelation.Viewer or AuthorizationRelation.Editor)
            .When(x => x.SubjectType == AuthorizationObjectType.Organization)
            .WithMessage("Organization-wide grants support viewer or editor access.");
    }
}

public sealed class RevokeResourceGrantCommandValidator : AbstractValidator<RevokeResourceGrantCommand>
{
    public RevokeResourceGrantCommandValidator() => RuleFor(x => x.GrantId).NotEmpty();
}
