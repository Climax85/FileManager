using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Authorization.Contracts.v1.Sharing;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Authorization.Features.v1.Sharing;

public static class SharingEndpoint
{
    internal static void MapSharingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/resources/{resourceType}/{resourceId:guid}/grants", async (
                AuthorizationObjectType resourceType,
                Guid resourceId,
                [FromBody] CreateResourceGrantRequest request,
                [FromHeader(Name = "X-Authz-Token")] string? authorizationToken,
                HttpContext httpContext,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                ResourceGrantResponse grant = await mediator.Send(new CreateResourceGrantCommand(
                    resourceType, resourceId, request.SubjectType, request.SubjectId, request.Relation,
                    request.ConfirmOrganizationWide, authorizationToken), cancellationToken);
                SetToken(httpContext, grant.AuthorizationToken);
                return Results.Ok(grant);
            })
            .WithName("CreateResourceGrant")
            .WithSummary("Grant viewer, editor, or manager access to an organization member")
            .RequireRateLimiting(SharingRateLimits.GrantMutations)
            .Produces<ResourceGrantResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapDelete("/grants/{grantId:guid}", async (
                Guid grantId,
                [FromHeader(Name = "X-Authz-Token")] string? authorizationToken,
                HttpContext httpContext,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                ResourceGrantResponse grant = await mediator.Send(
                    new RevokeResourceGrantCommand(grantId, authorizationToken), cancellationToken);
                SetToken(httpContext, grant.AuthorizationToken);
                return Results.Ok(grant);
            })
            .WithName("RevokeResourceGrant")
            .WithSummary("Revoke a direct or organization-wide resource grant")
            .RequireRateLimiting(SharingRateLimits.GrantMutations)
            .Produces<ResourceGrantResponse>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapGet("/resources/{resourceType}/{resourceId:guid}/grants", async (
                AuthorizationObjectType resourceType,
                Guid resourceId,
                [FromHeader(Name = "X-Authz-Token")] string? authorizationToken,
                HttpContext httpContext,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                ResourceGrantListResponse grants = await mediator.Send(
                    new GetResourceGrantsQuery(resourceType, resourceId, authorizationToken), cancellationToken);
                SetToken(httpContext, grants.AuthorizationToken);
                return Results.Ok(grants);
            })
            .WithName("GetResourceGrants")
            .WithSummary("List direct and organization-wide grants and synchronization state")
            .Produces<ResourceGrantListResponse>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapGet("/resources/{resourceType}/{resourceId:guid}/effective-access", async (
                AuthorizationObjectType resourceType,
                Guid resourceId,
                [FromQuery] string? subjectId,
                [FromHeader(Name = "X-Authz-Token")] string? authorizationToken,
                HttpContext httpContext,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                EffectiveAccessResponse access = await mediator.Send(
                    new GetEffectiveAccessQuery(resourceType, resourceId, subjectId, authorizationToken), cancellationToken);
                SetToken(httpContext, access.AuthorizationToken);
                return Results.Ok(access);
            })
            .WithName("GetEffectiveAccess")
            .WithSummary("Explain direct, inherited, and organization-derived effective access")
            .Produces<EffectiveAccessResponse>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status503ServiceUnavailable);
    }

    private static void SetToken(HttpContext context, string token)
    {
        if (!string.IsNullOrWhiteSpace(token)) context.Response.Headers["X-Authz-Token"] = token;
    }
}
