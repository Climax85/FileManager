using Grpc.Core;
using Microsoft.Extensions.Options;

namespace FSH.Modules.Authorization.Infrastructure;

public sealed class AuthzedRetryPolicy(IOptions<SpiceDbOptions> options)
{
    private readonly SpiceDbOptions _options = options.Value;

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        int attempt = 0;
        while (true)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            try
            {
                return await operation(timeout.Token).ConfigureAwait(false);
            }
            catch (RpcException exception) when (
                attempt < _options.MaxRetries
                && IsTransient(exception.StatusCode)
                && !cancellationToken.IsCancellationRequested)
            {
                TimeSpan delay = TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                attempt++;
            }
        }
    }

    private static bool IsTransient(StatusCode statusCode) => statusCode is
        StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.ResourceExhausted or StatusCode.Aborted;
}
