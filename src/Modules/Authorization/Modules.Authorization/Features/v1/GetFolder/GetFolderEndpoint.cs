using FSH.Modules.Authorization.Contracts.v1.GetFolder;
using FSH.Modules.Authorization.Contracts.v1.GetOrganizationRoot;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Authorization.Features.v1.GetFolder;

public static class GetFolderEndpoint
{
    internal static RouteHandlerBuilder MapGetFolderEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/folders/{id:guid}", async (
                Guid id,
                [FromHeader(Name = "X-Authz-Token")] string? authorizationToken,
                HttpContext httpContext,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                OrganizationRootResponse folder = await mediator.Send(
                    new GetFolderQuery(id, authorizationToken), cancellationToken);
                httpContext.Response.Headers["X-Authz-Token"] = folder.AuthorizationToken;
                return Results.Ok(folder);
            })
            .WithName("GetAuthorizationFolder")
            .WithSummary("Open an authorized organization folder")
            .Produces<OrganizationRootResponse>()
            .Produces(StatusCodes.Status404NotFound);
}
