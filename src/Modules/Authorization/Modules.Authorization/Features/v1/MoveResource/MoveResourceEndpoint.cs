using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Authorization.Contracts.v1.MoveResource;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Authorization.Features.v1.MoveResource;

public static class UpdateResourceParentEndpoint
{
    internal static RouteHandlerBuilder MapMoveResourceEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/resources/{resourceType}/{resourceId:guid}/move", async (
                AuthorizationObjectType resourceType,
                Guid resourceId,
                [FromBody] MoveResourceRequest request,
                [FromHeader(Name = "X-Authz-Token")] string? authorizationToken,
                HttpContext httpContext,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                MoveResourceResponse result = await mediator.Send(new MoveResourceCommand(
                    resourceType, resourceId, request.DestinationFolderId, authorizationToken), cancellationToken);
                httpContext.Response.Headers["X-Authz-Token"] = result.AuthorizationToken;
                return Results.Ok(result);
            })
            .WithName("MoveAuthorizationResource")
            .WithSummary("Move a file or folder while preserving direct grants and replacing inherited access")
            .Produces<MoveResourceResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status503ServiceUnavailable);
}
