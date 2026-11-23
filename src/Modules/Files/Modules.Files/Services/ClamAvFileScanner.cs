using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using FSH.Framework.Storage.Services;
using FSH.Modules.Files.Domain;
using Microsoft.Extensions.Options;

namespace FSH.Modules.Files.Services;

/// <summary>
/// Streams an uploaded object to ClamAV using the INSTREAM protocol. Only an explicit OK verdict
/// is clean; unavailable sockets, timeouts, malformed responses, and scanner errors fail closed.
/// </summary>
internal sealed class ClamAvFileScanner(
    IStorageService storage,
    IOptions<FilesOptions> options) : IFileScanner
{
    private static readonly byte[] InstreamCommand = "zINSTREAM\0"u8.ToArray();
    private static readonly byte[] EndOfStream = new byte[4];
    private const int ChunkSize = 64 * 1024;
    private const int MaxResponseBytes = 1024;

    public async ValueTask<ScanStatus> ScanAsync(
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        var scanner = options.Value.Scanner;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, scanner.TimeoutMilliseconds)));

        try
        {
            var download = await storage.DownloadAsync(storageKey, timeout.Token).ConfigureAwait(false);
            if (download is null) return ScanStatus.ScanFailed;
            await using var content = download.Stream;

            using var client = new TcpClient();
            await client.ConnectAsync(scanner.Host, scanner.Port, timeout.Token).ConfigureAwait(false);
            await using var stream = client.GetStream();
            await stream.WriteAsync(InstreamCommand, timeout.Token).ConfigureAwait(false);

            byte[] buffer = new byte[ChunkSize + 4];
            int bytesRead;
            while ((bytesRead = await content.ReadAsync(buffer.AsMemory(4, ChunkSize), timeout.Token).ConfigureAwait(false)) > 0)
            {
                BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(0, 4), bytesRead);
                await stream.WriteAsync(buffer.AsMemory(0, bytesRead + 4), timeout.Token).ConfigureAwait(false);
            }

            await stream.WriteAsync(EndOfStream, timeout.Token).ConfigureAwait(false);
            string response = await ReadResponseAsync(stream, timeout.Token).ConfigureAwait(false);
            if (response.Equals("stream: OK", StringComparison.Ordinal))
            {
                return ScanStatus.Clean;
            }

            return response.StartsWith("stream: ", StringComparison.Ordinal)
                   && response.EndsWith(" FOUND", StringComparison.Ordinal)
                ? ScanStatus.Infected
                : ScanStatus.ScanFailed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return ScanStatus.ScanFailed;
        }
    }

    private static async Task<string> ReadResponseAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        byte[] response = new byte[MaxResponseBytes];
        byte[] next = new byte[1];
        int length = 0;
        while (length < response.Length)
        {
            await stream.ReadExactlyAsync(next, cancellationToken).ConfigureAwait(false);
            if (next[0] == 0)
            {
                return Encoding.ASCII.GetString(response, 0, length);
            }

            response[length++] = next[0];
        }

        throw new InvalidDataException("ClamAV response exceeded the protocol limit.");
    }
}
