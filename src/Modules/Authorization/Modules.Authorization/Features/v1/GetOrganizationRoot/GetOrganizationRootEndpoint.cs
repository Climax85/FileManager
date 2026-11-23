using FSH.Modules.Authorization.Contracts.v1.GetOrganizationRoot;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Authorization.Features.v1.GetOrganizationRoot;

public static class GetOrganizationRootEndpoint
{
    internal static RouteHandlerBuilder MapGetOrganizationRootEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/organization/root", async (
                [FromHeader(Name = "X-Authz-Token")] string? authorizationToken,
                HttpContext httpContext,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                OrganizationRootResponse root = await mediator.Send(
                    new GetOrganizationRootQuery(authorizationToken), cancellationToken);
                httpContext.Response.Headers["X-Authz-Token"] = root.AuthorizationToken;
                return Results.Ok(root);
            })
            .WithName("GetOrganizationRoot")
            .WithSummary("Retrieve the current organization's root folder")
            .Produces<OrganizationRootResponse>()
            .Produces(StatusCodes.Status404NotFound);
}
