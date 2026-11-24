using FSH.Modules.Authorization.Contracts.v1.Sharing;
using FSH.Modules.Authorization.Services;
using Mediator;

namespace FSH.Modules.Authorization.Features.v1.Sharing;

public sealed class CreateResourceGrantCommandHandler(ResourceSharingService sharing)
    : ICommandHandler<CreateResourceGrantCommand, ResourceGrantResponse>
{
    public async ValueTask<ResourceGrantResponse> Handle(CreateResourceGrantCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return await sharing.GrantAsync(command, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class RevokeResourceGrantCommandHandler(ResourceSharingService sharing)
    : ICommandHandler<RevokeResourceGrantCommand, ResourceGrantResponse>
{
    public async ValueTask<ResourceGrantResponse> Handle(RevokeResourceGrantCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return await sharing.RevokeAsync(command, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class GetResourceGrantsQueryHandler(ResourceSharingService sharing)
    : IQueryHandler<GetResourceGrantsQuery, ResourceGrantListResponse>
{
    public async ValueTask<ResourceGrantListResponse> Handle(GetResourceGrantsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await sharing.ListAsync(query, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class GetEffectiveAccessQueryHandler(ResourceSharingService sharing)
    : IQueryHandler<GetEffectiveAccessQuery, EffectiveAccessResponse>
{
    public async ValueTask<EffectiveAccessResponse> Handle(GetEffectiveAccessQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await sharing.EffectiveAccessAsync(query, cancellationToken).ConfigureAwait(false);
    }
}
