using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Apitally.Tests.Support;

// A raw HTTP/1.1 listener for connection-level behavior, such as proxying and dropped connections.
internal sealed class LoopbackHttpServer : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stopping = new();
    private int connections;

    // Returns false to close the connection without a response.
    public LoopbackHttpServer(Func<int, bool> shouldRespond)
    {
        listener.Start();
        _ = AcceptAsync(shouldRespond);
    }

    public Uri Uri => new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
    public ConcurrentQueue<string> RequestLines { get; } = new();
    public int Connections => connections;

    public void Dispose()
    {
        stopping.Cancel();
        listener.Stop();
    }

    private async Task AcceptAsync(Func<int, bool> shouldRespond)
    {
        while (!stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stopping.Token);
            }
            catch
            {
                return;
            }
            var connection = Interlocked.Increment(ref connections);
            _ = HandleAsync(client, shouldRespond(connection));
        }
    }

    private async Task HandleAsync(TcpClient client, bool respond)
    {
        using (client)
        {
            var stream = client.GetStream();
            var header = await ReadHeaderAsync(stream);
            RequestLines.Enqueue(header.Split("\r\n")[0]);
            if (!respond)
                return;
            var length = header
                .Split("\r\n")
                .Where(line =>
                    line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                )
                .Select(line => int.Parse(line["Content-Length:".Length..].Trim()))
                .FirstOrDefault();
            var body = new byte[length];
            await stream.ReadExactlyAsync(body);
            await stream.WriteAsync(
                Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                )
            );
        }
    }

    private static async Task<string> ReadHeaderAsync(NetworkStream stream)
    {
        var bytes = new List<byte>();
        var buffer = new byte[1];
        while (await stream.ReadAsync(buffer) == 1)
        {
            bytes.Add(buffer[0]);
            if (
                bytes.Count >= 4
                && Encoding.ASCII.GetString(bytes.TakeLast(4).ToArray()) == "\r\n\r\n"
            )
                break;
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }
}
