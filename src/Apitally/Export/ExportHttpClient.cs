using System.Net;
using System.Net.Http.Headers;

namespace Apitally.Export;

internal enum ExportOutcome
{
    Accepted,
    Retryable,
    Rejected,
}

internal readonly record struct ExportResponse(
    ExportOutcome Outcome,
    int? StatusCode = null,
    int? ExportIntervalSeconds = null
);

// Posts stored spool files. A private HttpClient keeps application-wide client defaults,
// such as resilience handlers, from adding retries beneath the export worker.
internal sealed class ExportHttpClient : IDisposable
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private const string ExportIntervalHeader = "Apitally-Export-Interval";

    private readonly HttpClient client;
    private readonly Uri endpoint;
    private readonly string writeToken;
    private readonly string env;

    public ExportHttpClient(Uri endpoint, string writeToken, string env, IWebProxy? proxy)
    {
        this.endpoint = endpoint;
        this.writeToken = writeToken;
        this.env = env;
        client = new HttpClient(
            new SocketsHttpHandler
            {
                Proxy = proxy,
                UseProxy = proxy is not null,
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            }
        )
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public async Task<ExportResponse> PostAsync(
        TelemetrySignal signal,
        byte[] body,
        CancellationToken cancellationToken
    )
    {
        var url = new Uri(
            $"{endpoint.AbsoluteUri.TrimEnd('/')}/v1/{signal.ToString().ToLowerInvariant()}"
        );
        HttpResponseMessage response;
        try
        {
            try
            {
                response = await SendAsync(url, body, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                // The server may close an idle keep-alive connection mid-request; retry once.
                response = await SendAsync(url, body, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
            when (exception is HttpRequestException or OperationCanceledException)
        {
            return new(ExportOutcome.Retryable);
        }
        using (response)
        {
            var status = (int)response.StatusCode;
            var interval = ReadExportInterval(response);
            if (response.IsSuccessStatusCode)
                return new(ExportOutcome.Accepted, status, interval);
            if (status is 408 or 429 or >= 500)
                return new(ExportOutcome.Retryable, status, interval);
            return new(ExportOutcome.Rejected, status, interval);
        }
    }

    public void Dispose() => client.Dispose();

    private async Task<HttpResponseMessage> SendAsync(
        Uri url,
        byte[] body,
        CancellationToken cancellationToken
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        content.Headers.ContentEncoding.Add("gzip");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", writeToken);
        request.Headers.Add("Apitally-Env", env);
        request.Headers.UserAgent.ParseAdd($"{OtlpEncoder.DistroName}/{OtlpEncoder.DistroVersion}");
        var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
        return response;
    }

    private static int? ReadExportInterval(HttpResponseMessage response) =>
        response.Headers.TryGetValues(ExportIntervalHeader, out var values)
        && int.TryParse(values.FirstOrDefault(), out var seconds)
            ? seconds
            : null;
}
