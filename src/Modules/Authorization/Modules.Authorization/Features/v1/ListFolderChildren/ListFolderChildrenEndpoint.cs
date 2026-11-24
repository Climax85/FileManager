using FSH.Modules.Authorization.Contracts.v1.ListFolderChildren;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Authorization.Features.v1.ListFolderChildren;

public static class ListFolderChildrenEndpoint
{
    internal static RouteHandlerBuilder MapListFolderChildrenEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/folders/{id:guid}/children", async (
                Guid id,
                int? pageSize,
                string? cursor,
                [FromHeader(Name = "X-Authz-Token")] string? authorizationToken,
                HttpContext httpContext,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                FolderChildrenResponse page = await mediator.Send(
                    new ListFolderChildrenQuery(id, pageSize ?? 50, cursor, authorizationToken), cancellationToken);
                httpContext.Response.Headers["X-Authz-Token"] = page.AuthorizationToken;
                return Results.Ok(page);
            })
            .WithName("ListAuthorizationFolderChildren")
            .WithSummary("List an authorized cursor page of direct child folders")
            .Produces<FolderChildrenResponse>()
            .Produces(StatusCodes.Status404NotFound);
}
