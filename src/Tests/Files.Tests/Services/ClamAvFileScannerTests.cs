using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FSH.Framework.Storage.DTOs;
using FSH.Framework.Storage.Services;
using FSH.Modules.Files;
using FSH.Modules.Files.Domain;
using FSH.Modules.Files.Services;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Files.Tests.Services;

public sealed class ClamAvFileScannerTests
{
    [Theory]
    [InlineData("stream: OK\0", ScanStatus.Clean)]
    [InlineData("stream: Eicar-Signature FOUND\0", ScanStatus.Infected)]
    [InlineData("stream: Unexpected failure ERROR\0", ScanStatus.ScanFailed)]
    public async Task ScanAsync_Should_MapClamAvVerdicts(string response, ScanStatus expected)
    {
        await using var server = new FakeClamAvServer(response);
        var scanner = CreateScanner(server.Start(), timeoutMilliseconds: 1_000);

        var result = await scanner.ScanAsync("tenant/file.pdf");

        result.ShouldBe(expected);
    }

    [Fact]
    public async Task ScanAsync_Should_ReturnScanFailed_WhenClamAvTimesOut()
    {
        await using var server = new FakeClamAvServer(response: null);
        var scanner = CreateScanner(server.Start(), timeoutMilliseconds: 25);

        var result = await scanner.ScanAsync("tenant/file.pdf");

        result.ShouldBe(ScanFailed);
    }

    [Fact]
    public async Task ScanAsync_Should_ReturnScanFailed_WhenClamAvIsUnavailable()
    {
        int unusedPort;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        unusedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var scanner = CreateScanner(unusedPort, timeoutMilliseconds: 100);

        var result = await scanner.ScanAsync("tenant/file.pdf");

        result.ShouldBe(ScanFailed);
    }

    private static ClamAvFileScanner CreateScanner(int port, int timeoutMilliseconds)
    {
        var storage = Substitute.For<IStorageService>();
        storage.DownloadAsync("tenant/file.pdf", Arg.Any<CancellationToken>())
            .Returns(new FileDownloadResponse
            {
                Stream = new MemoryStream("file contents"u8.ToArray()),
                ContentType = "application/pdf",
                FileName = "file.pdf"
            });

        return new ClamAvFileScanner(
            storage,
            Options.Create(new FilesOptions
            {
                Scanner = new ClamAvScannerOptions
                {
                    Host = "127.0.0.1",
                    Port = port,
                    TimeoutMilliseconds = timeoutMilliseconds
                }
            }));
    }

    private static ScanStatus ScanFailed => ScanStatus.ScanFailed;

    private sealed class FakeClamAvServer(string? response) : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stopping = new();
        private Task? _serve;

        public int Port { get; private set; }

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            _listener.Stop();
            if (_serve is not null)
            {
                try
                {
                    await _serve;
                }
                catch (OperationCanceledException)
                {
                }
                catch (SocketException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            }

            _stopping.Dispose();
        }

        public int Start()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _serve = ServeAsync();
            return Port;
        }

        private async Task ServeAsync()
        {
            using var client = await _listener.AcceptTcpClientAsync(_stopping.Token);
            await using var stream = client.GetStream();
            byte[] header = new byte[4];
            while (true)
            {
                await stream.ReadExactlyAsync(header, _stopping.Token);
                int length = BinaryPrimitives.ReadInt32BigEndian(header);
                if (length == 0) break;
                byte[] chunk = new byte[length];
                await stream.ReadExactlyAsync(chunk, _stopping.Token);
            }

            if (response is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), _stopping.Token);
                return;
            }

            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _stopping.Token);
        }
    }
}
