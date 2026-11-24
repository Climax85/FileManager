using FSH.Modules.Authorization.Contracts.v1.CreateFolder;
using FSH.Modules.Authorization.Contracts.v1.GetOrganizationRoot;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Authorization.Features.v1.CreateFolder;

public static class CreateFolderEndpoint
{
    internal static RouteHandlerBuilder MapCreateFolderEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/folders", async (
                CreateFolderCommand command,
                [FromHeader(Name = "X-Authz-Token")] string? authorizationToken,
                HttpContext httpContext,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                OrganizationRootResponse folder = await mediator.Send(
                    command with { AuthorizationToken = authorizationToken ?? command.AuthorizationToken }, cancellationToken);
                httpContext.Response.Headers["X-Authz-Token"] = folder.AuthorizationToken;
                return Results.Created($"/api/v1/authorization/folders/{folder.Id}", folder);
            })
            .WithName("CreateAuthorizationFolder")
            .WithSummary("Create a child folder as an editor")
            .Produces<OrganizationRootResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status404NotFound);
}
