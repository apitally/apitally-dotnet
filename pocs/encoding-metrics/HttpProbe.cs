using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace EncodingMetrics;

internal static class HttpProbe
{
    internal static async Task Run(string persistedPath)
    {
        ProxyBinding();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = new Uri(
            $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1/traces"
        );
        var received = new List<byte[]>();
        var server = Serve(listener, received, timeout.Token);
        using var handler = new SocketsHttpHandler { UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var exporter = new ActivityCounter();
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddHttpClientInstrumentation()
            .AddProcessor(new SimpleActivityExportProcessor(exporter))
            .Build();
        try
        {
            await Send(client, endpoint, persistedPath, timeout.Token);
            Program.Check(exporter.Count == 1, "Stock HTTP instrumentation baseline");
            using (SuppressInstrumentationScope.Begin())
            {
                await Send(client, endpoint, persistedPath, timeout.Token);
                await Send(client, endpoint, persistedPath, timeout.Token);
            }
            Program.Check(
                exporter.Count == 1,
                "Suppressed local exports must not reach user exporter"
            );
            await Send(client, endpoint, persistedPath, timeout.Token);
            Program.Check(exporter.Count == 2, "Suppression scope restored");
            await server;
            var persisted = File.ReadAllBytes(persistedPath);
            Program.Check(
                received.Count == 4 && received.All(bytes => bytes.SequenceEqual(persisted)),
                "Physical repeated POST bodies match persisted gzip bytes exactly"
            );
            Console.WriteLine(
                $"PASS local HTTP: baseline/suppressed/suppressed/restored spans=1/0/0/1; all 4 POST bodies byte-identical to disk; SHA256={Convert.ToHexString(SHA256.HashData(persisted))}"
            );
        }
        finally
        {
            timeout.Cancel();
            listener.Stop();
            try
            {
                await server;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
            catch (SocketException) when (timeout.IsCancellationRequested) { }
        }
    }

    private static void ProxyBinding()
    {
        var original = Environment.GetEnvironmentVariable("HTTP_PROXY");
        try
        {
            Environment.SetEnvironmentVariable("HTTP_PROXY", "http://127.0.0.1:18080");
            var boundProxy = new WebProxy(
                new Uri(Environment.GetEnvironmentVariable("HTTP_PROXY")!)
            );
            using var handler = new SocketsHttpHandler { UseProxy = true, Proxy = boundProxy };
            Environment.SetEnvironmentVariable("HTTP_PROXY", "http://127.0.0.1:18081");
            Program.Check(
                handler.Proxy!.GetProxy(new Uri("http://example.invalid/"))!.Port == 18080,
                "Explicit proxy object freezes resolved environment URI"
            );
            Console.WriteLine(
                "PASS proxy configuration only: explicit HTTP_PROXY URI snapshot stays bound after environment change; no physical proxy request"
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable("HTTP_PROXY", original);
        }
    }

    private static async Task Send(
        HttpClient client,
        Uri endpoint,
        string path,
        CancellationToken token
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            "apt_000000000000000000000000"
        );
        request.Headers.Add("Apitally-Env", "poc");
        request.Content = new ByteArrayContent(await File.ReadAllBytesAsync(path, token));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        request.Content.Headers.ContentEncoding.Add("gzip");
        using var response = await client.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        await response.Content.ReadAsByteArrayAsync(token);
    }

    private static async Task Serve(
        TcpListener listener,
        List<byte[]> received,
        CancellationToken token
    )
    {
        for (var request = 0; request < 4; request++)
        {
            using var connection = await listener.AcceptTcpClientAsync(token);
            await using var stream = connection.GetStream();
            var header = new List<byte>();
            var single = new byte[1];
            while (true)
            {
                await stream.ReadExactlyAsync(single, token);
                header.Add(single[0]);
                Program.Check(header.Count <= 65_536, "Bounded synthetic HTTP headers");
                if (
                    header.Count >= 4
                    && header[^4] == 13
                    && header[^3] == 10
                    && header[^2] == 13
                    && header[^1] == 10
                )
                    break;
            }
            var lines = Encoding
                .ASCII.GetString(header.ToArray())
                .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            Program.Check(lines[0] == "POST /v1/traces HTTP/1.1", "Local endpoint/path only");
            var headers = lines
                .Skip(1)
                .Select(line => line.Split(':', 2))
                .ToDictionary(
                    parts => parts[0],
                    parts => parts[1].Trim(),
                    StringComparer.OrdinalIgnoreCase
                );
            Program.Check(
                headers["Authorization"] == "Bearer apt_000000000000000000000000"
                    && headers["Apitally-Env"] == "poc",
                "Synthetic export headers"
            );
            Program.Check(
                headers["Content-Type"] == "application/x-protobuf"
                    && headers["Content-Encoding"] == "gzip",
                "OTLP media headers"
            );
            var length = int.Parse(headers["Content-Length"], CultureInfo.InvariantCulture);
            Program.Check(length is > 0 and <= 4 * 1024 * 1024, "Bounded local POST");
            var body = new byte[length];
            await stream.ReadExactlyAsync(body, token);
            received.Add(body);
            await stream.WriteAsync(
                Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                ),
                token
            );
        }
    }

    private sealed class ActivityCounter : BaseExporter<Activity>
    {
        internal int Count;

        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
            {
                Program.Check(
                    activity.Kind == ActivityKind.Client,
                    "Only HTTP client activities expected"
                );
                Interlocked.Increment(ref Count);
            }
            return ExportResult.Success;
        }
    }
}
