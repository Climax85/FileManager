using System.Security.Claims;
using System.Threading.RateLimiting;
using Authzed.Api.V1;
using Asp.Versioning;
using Microsoft.Extensions.Configuration;
using FSH.Framework.Eventing;
using FSH.Framework.Persistence;
using FSH.Framework.Web.Modules;
using FSH.Modules.Authorization.Contracts;
using FSH.Modules.Authorization.Data;
using FSH.Modules.Authorization.Features.v1.CreateFolder;
using FSH.Modules.Authorization.Features.v1.ListFolderChildren;
using FSH.Modules.Authorization.Features.v1.RenameFolder;
using FSH.Modules.Authorization.Features.v1.GetFolder;
using FSH.Modules.Authorization.Features.v1.GetOrganizationRoot;
using FSH.Modules.Authorization.Features.v1.Sharing;
using FSH.Modules.Authorization.Infrastructure;
using FSH.Modules.Authorization.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.RateLimiting;

[assembly: FshModule(typeof(FSH.Modules.Authorization.AuthorizationModule), 150)]

namespace FSH.Modules.Authorization;

public sealed class AuthorizationModule : IModule
{
    public void ConfigureServices(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddOptions<SpiceDbOptions>()
            .Bind(builder.Configuration.GetSection(SpiceDbOptions.SectionName))
            .Validate(x => !string.IsNullOrWhiteSpace(x.Endpoint), "SpiceDB endpoint is required.")
            .Validate(x => !string.IsNullOrWhiteSpace(x.PresharedKey), "SpiceDB preshared key is required.")
            .Validate(x => x.MaxRetries >= 0 && x.TimeoutSeconds > 0, "SpiceDB retry and timeout settings must be positive.")
            .ValidateOnStart();
        builder.Services.AddOptions<SharingOptions>()
            .Bind(builder.Configuration.GetSection(SharingOptions.SectionName))
            .Validate(x => x.GrantRateLimitPermitLimit > 0 && x.GrantRateLimitWindowSeconds > 0,
                "Grant rate limits must be positive.")
            .ValidateOnStart();
        SharingOptions sharingOptions = builder.Configuration.GetSection(SharingOptions.SectionName).Get<SharingOptions>() ?? new SharingOptions();
        builder.Services.AddRateLimiter(options =>
        {
            options.AddPolicy(SharingRateLimits.GrantMutations, context =>
            {
                string user = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
                string tenant = context.Request.Headers["tenant"].ToString();
                return RateLimitPartition.GetFixedWindowLimiter($"{tenant}:{user}", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = sharingOptions.GrantRateLimitPermitLimit,
                    Window = TimeSpan.FromSeconds(sharingOptions.GrantRateLimitWindowSeconds),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
            });
        });
        builder.Services.AddSingleton(_ => AuthzedChannelFactory.Get(
            builder.Configuration.GetSection(SpiceDbOptions.SectionName).Get<SpiceDbOptions>() ?? new SpiceDbOptions()));
        builder.Services.AddSingleton(_ =>
        {
            var channel = _.GetRequiredService<Grpc.Net.Client.GrpcChannel>();
            return new PermissionsService.PermissionsServiceClient(channel);
        });
        builder.Services.AddSingleton(_ =>
        {
            var channel = _.GetRequiredService<Grpc.Net.Client.GrpcChannel>();
            return new SchemaService.SchemaServiceClient(channel);
        });
        builder.Services.AddSingleton<AuthzedRetryPolicy>();
        builder.Services.AddSingleton<AuthzedAuthorizationClient>();
        builder.Services.AddScoped<IPermissionChecker, FailClosedPermissionChecker>();
        builder.Services.AddSingleton<IRelationshipWriter>(sp => sp.GetRequiredService<AuthzedAuthorizationClient>());
        builder.Services.AddSingleton<IRelationshipReader>(sp => sp.GetRequiredService<AuthzedAuthorizationClient>());
        builder.Services.AddSingleton<IAuthorizationDiscovery>(sp => sp.GetRequiredService<AuthzedAuthorizationClient>());
        builder.Services.AddSingleton<IAuthorizationSchemaManager, AuthzedSchemaManager>();
        builder.Services.AddScoped<IOrganizationRootProvisioner, OrganizationRootProvisioner>();
        builder.Services.AddScoped<AuthorizedFolderReader>();
        builder.Services.AddScoped<ResourceLineageReader>();
        builder.Services.AddScoped<ResourceSharingService>();
        builder.Services.AddScoped<IFolderHierarchy, FolderHierarchy>();
        builder.Services.AddScoped<IFolderAuthorization, FolderAuthorization>();
        builder.Services.AddScoped<FolderCommandService>();
        builder.Services.AddHeroDbContext<AuthorizationDbContext>();
        builder.Services.AddEventingCore(builder.Configuration);
        builder.Services.AddEventingForDbContext<AuthorizationDbContext>();
        builder.Services.AddIntegrationEventHandlers(typeof(AuthorizationModule).Assembly);
        builder.Services.AddScoped<IDbInitializer, AuthorizationDbInitializer>();
        builder.Services.AddScoped<IDbInitializer, OrganizationRootDbInitializer>();
        builder.Services.AddScoped<FSH.Framework.Eventing.Outbox.EfCoreOutboxStore<AuthorizationDbContext>>();
        builder.Services.AddHealthChecks().AddDbContextCheck<AuthorizationDbContext>(
            name: "db:authorization",
            failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var versionSet = endpoints.NewApiVersionSet()
            .HasApiVersion(new ApiVersion(1))
            .ReportApiVersions()
            .Build();
        var group = endpoints.MapGroup("api/v{version:apiVersion}/authorization")
            .WithTags("Authorization")
            .WithApiVersionSet(versionSet)
            .RequireAuthorization();
        group.MapGetOrganizationRootEndpoint();
        group.MapGetFolderEndpoint();
        group.MapCreateResourceGrantEndpoint();
        group.MapRevokeResourceGrantEndpoint();
        group.MapGetResourceGrantsEndpoint();
        group.MapGetEffectiveAccessEndpoint();
        group.MapCreateFolderEndpoint();
        group.MapRenameFolderEndpoint();
        group.MapListFolderChildrenEndpoint();
    }
}
