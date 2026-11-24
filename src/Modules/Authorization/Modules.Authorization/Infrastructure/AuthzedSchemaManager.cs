using Authzed.Api.V1;

namespace FSH.Modules.Authorization.Infrastructure;

public interface IAuthorizationSchemaManager
{
    ValueTask EnsureCurrentAsync(CancellationToken cancellationToken = default);
}

public sealed class AuthzedSchemaManager(SchemaService.SchemaServiceClient client) : IAuthorizationSchemaManager
{
    public async ValueTask EnsureCurrentAsync(CancellationToken cancellationToken = default)
    {
        using Stream stream = typeof(AuthzedSchemaManager).Assembly.GetManifestResourceStream(
            "FSH.Modules.Authorization.Schema.v1.schema.zed")
            ?? throw new InvalidOperationException("The versioned Authorization schema is missing.");
        using var reader = new StreamReader(stream);
        string schema = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await client.WriteSchemaAsync(new WriteSchemaRequest { Schema = schema }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }
}
