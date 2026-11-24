using FSH.Framework.Core.Exceptions;
using FSH.Modules.Authorization.Contracts.v1.MoveResource;
using Mediator;

namespace FSH.Modules.Authorization.Features.v1.MoveResource;

public sealed class MoveResourceCommandHandler(IEnumerable<IResourceMoveHandler> handlers)
    : ICommandHandler<MoveResourceCommand, MoveResourceResponse>
{
    public async ValueTask<MoveResourceResponse> Handle(
        MoveResourceCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        IResourceMoveHandler handler = handlers.SingleOrDefault(x => x.ResourceType == command.ResourceType)
            ?? throw new NotFoundException("resource not found");
        return await handler.MoveAsync(command, cancellationToken).ConfigureAwait(false);
    }
}
