using Authzed.Api.V1;
using FSH.Modules.Authorization.Contracts;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using System.Collections.Concurrent;

namespace FSH.Modules.Authorization.Infrastructure;

public static class AuthzedChannelFactory
{
    private static readonly ConcurrentDictionary<string, GrpcChannel> Channels = new(StringComparer.Ordinal);

    public static GrpcChannel Get(SpiceDbOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string key = $"{options.Endpoint}|{options.UseTls}|{options.PresharedKey}";
        return Channels.GetOrAdd(key, _ =>
        {
            CallCredentials callCredentials = CallCredentials.FromInterceptor((_, metadata) =>
            {
                metadata.Add("Authorization", $"Bearer {options.PresharedKey}");
                return Task.CompletedTask;
            });
            ChannelCredentials channelCredentials = options.UseTls
                ? ChannelCredentials.Create(new SslCredentials(), callCredentials)
                : ChannelCredentials.Create(ChannelCredentials.Insecure, callCredentials);
            return GrpcChannel.ForAddress(options.Endpoint, new GrpcChannelOptions
            {
                Credentials = channelCredentials,
                UnsafeUseInsecureChannelCallCredentials = !options.UseTls,
            });
        });
    }

    public static ObjectReference Object(AuthorizationObject value) => new()
    {
        ObjectType = value.Definition,
        ObjectId = value.ObjectId,
    };

    public static SubjectReference Subject(AuthorizationSubject value) => new()
    {
        Object = Object(value.Object),
        OptionalRelation = value.OptionalRelation is null
            ? string.Empty
            : AuthorizationSchema.Relation(value.OptionalRelation.Value),
    };

    public static Consistency Consistency(AuthorizationConsistency? value) =>
        value?.AtLeastAsFreshToken is AuthorizationToken token
            ? new Consistency { AtLeastAsFresh = new ZedToken { Token = token.Value } }
            : new Consistency { MinimizeLatency = true };

    public static RelationshipUpdate Update(RelationshipMutation mutation)
    {
        var relationship = new Relationship
        {
            Resource = Object(mutation.Resource),
            Relation = AuthorizationSchema.Relation(mutation.Relation),
            Subject = Subject(mutation.Subject),
        };
        if (mutation.ExpiresAt is DateTimeOffset expiresAt)
        {
            relationship.OptionalExpiresAt = Timestamp.FromDateTimeOffset(expiresAt);
        }

        return new RelationshipUpdate
        {
            Operation = mutation.Operation == RelationshipOperation.Touch
                ? RelationshipUpdate.Types.Operation.Touch
                : RelationshipUpdate.Types.Operation.Delete,
            Relationship = relationship,
        };
    }
}
