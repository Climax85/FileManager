using FSH.Modules.Auditing.Contracts;

using FSH.Modules.Files.Contracts.v1.Queries;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Files.Features.v1.GetFileDownloadUrl;

public static class GetFileDownloadUrlEndpoint
{
    internal static RouteHandlerBuilder MapGetFileDownloadUrlEndpoint(this IEndpointRouteBuilder endpoints)
        => endpoints.MapGet("/{id:guid}/url",
                async (Guid id, bool? inline, IMediator mediator, HttpContext httpContext, CancellationToken cancellationToken) =>
                {
                    httpContext.Response.Headers.CacheControl = "no-store";
                    return Results.Ok(await mediator.Send(new GetFileDownloadUrlQuery(id, inline ?? false), cancellationToken));
                })
            .WithName("GetFileDownloadUrl")
            .WithSummary("Mint a short-lived presigned download URL")
            .WithDescription("Default disposition is attachment (click-to-save). Pass ?inline=true to get an inline disposition for browser preview (PDF viewer, image render, etc.).")
            .NoAuditBody()
            .RequireAuthorization();
}
