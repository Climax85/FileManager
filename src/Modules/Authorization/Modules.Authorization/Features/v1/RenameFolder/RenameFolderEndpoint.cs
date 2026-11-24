using FSH.Modules.Authorization.Contracts.v1.GetOrganizationRoot;
using FSH.Modules.Authorization.Contracts.v1.RenameFolder;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Authorization.Features.v1.RenameFolder;

public static class UpdateFolderEndpoint
{
    internal static RouteHandlerBuilder MapRenameFolderEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPatch("/folders/{id:guid}", async (
                Guid id,
                RenameFolderCommand command,
                [FromHeader(Name = "X-Authz-Token")] string? authorizationToken,
                HttpContext httpContext,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                OrganizationRootResponse folder = await mediator.Send(
                    command with { FolderId = id, AuthorizationToken = authorizationToken ?? command.AuthorizationToken }, cancellationToken);
                httpContext.Response.Headers["X-Authz-Token"] = folder.AuthorizationToken;
                return Results.Ok(folder);
            })
            .WithName("RenameAuthorizationFolder")
            .WithSummary("Rename a folder as an editor")
            .Produces<OrganizationRootResponse>()
            .Produces(StatusCodes.Status404NotFound);
}
