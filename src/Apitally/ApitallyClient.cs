namespace Apitally;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Apitally.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Extensions.Http;

class ApitallyClient(
    IOptions<ApitallyOptions> options,
    RequestLogger requestLogger,
    ILogger<ApitallyClient> logger
) : BackgroundService, IDisposable
{
    public enum HubRequestStatus
    {
        OK,
        ValidationError,
        InvalidClientId,
        PaymentRequired,
        RetryableError,
    }

    private const int SyncIntervalSeconds = 60;
    private const int InitialSyncIntervalSeconds = 10;
    private const int InitialPeriodSeconds = 3600;
    private const int MaxQueueTimeSeconds = 3600;
    private const int RequestTimeoutSeconds = 10;
    private static readonly string HubBaseUrl =
        Environment.GetEnvironmentVariable("APITALLY_HUB_BASE_URL") ?? "https://hub.apitally.io";

    private readonly Guid _instanceUuid = Guid.NewGuid();
    private readonly HttpClient _httpClient = CreateHttpClient();
    private readonly IAsyncPolicy<HttpResponseMessage> _retryPolicy = CreateRetryPolicy();
    private readonly ConcurrentQueue<SyncData> _syncDataQueue = new();
    private readonly Random _random = new();

    private StartupData? _startupData;
    private bool _startupDataSent = false;
    private bool _initialSyncPeriod = true;
    private readonly DateTime _initialSyncEndTime = DateTime.UtcNow.AddSeconds(
        InitialPeriodSeconds
    );

    public bool Enabled { get; private set; } = true;
    public readonly RequestCounter RequestCounter = new();
    public readonly ValidationErrorCounter ValidationErrorCounter = new();
    public readonly ServerErrorCounter ServerErrorCounter = new();
    public readonly ConsumerRegistry ConsumerRegistry = new();
    public readonly RequestLogger RequestLogger = requestLogger;

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient
        {
            BaseAddress = new Uri(HubBaseUrl),
            Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds),
        };
    }

    private static IAsyncPolicy<HttpResponseMessage> CreateRetryPolicy()
    {
        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(msg => msg.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            .WaitAndRetryAsync(
                3,
                retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt - 1))
            );
    }

    private string GetHubUrlPath(string endpoint, string query = "")
    {
        var path = $"/v2/{options.Value.ClientId}/{options.Value.Env}/{endpoint}";
        if (!string.IsNullOrEmpty(query))
        {
            path += "?" + query.TrimStart('?');
        }
        return path;
    }

    public void SetStartupData(List<Path> paths, Dictionary<string, string> versions, string client)
    {
        _startupData = new StartupData
        {
            InstanceUuid = _instanceUuid,
            Paths = paths,
            Versions = versions,
            Client = client,
        };
    }

    private async Task SendStartupDataAsync(CancellationToken cancellationToken)
    {
        if (_startupData == null)
        {
            return;
        }

        logger.LogDebug("Sending startup data to Apitally hub");
        var status = await SendHubRequestAsync(
            _httpClient.PostAsJsonAsync(GetHubUrlPath("startup"), _startupData, cancellationToken)
        );
        if (status == HubRequestStatus.OK)
        {
            _startupDataSent = true;
            _startupData = null;
        }
        else if (status == HubRequestStatus.ValidationError)
        {
            _startupDataSent = false;
            _startupData = null;
        }
        else
        {
            _startupDataSent = false;
        }
    }

    private async Task SendSyncDataAsync(CancellationToken cancellationToken)
    {
        var data = new SyncData
        {
            InstanceUuid = _instanceUuid,
            Requests = RequestCounter.GetAndResetRequests(),
            ValidationErrors = ValidationErrorCounter.GetAndResetValidationErrors(),
            ServerErrors = ServerErrorCounter.GetAndResetServerErrors(),
            Consumers = ConsumerRegistry.GetAndResetConsumers(),
        };
        _syncDataQueue.Enqueue(data);

        int i = 0;
        while (_syncDataQueue.TryDequeue(out var payload))
        {
            if (payload.AgeInSeconds > MaxQueueTimeSeconds)
            {
                continue;
            }

            if (i > 0)
            {
                // Add random delay between retries
                await Task.Delay(100 + _random.Next(400), cancellationToken);
            }

            logger.LogDebug("Synchronizing data with Apitally hub");
            var status = await SendHubRequestAsync(
                _httpClient.PostAsJsonAsync(GetHubUrlPath("sync"), payload, cancellationToken)
            );
            if (status == HubRequestStatus.RetryableError)
            {
                _syncDataQueue.Enqueue(payload);
                break;
            }

            i++;
        }
    }

    private async Task SendLogDataAsync(CancellationToken cancellationToken)
    {
        RequestLogger.RotateFile();
        TempGzipFile? logFile;
        int i = 0;
        while ((logFile = RequestLogger.GetFile()) != null)
        {
            if (i > 0)
            {
                await Task.Delay(100 + _random.Next(400), cancellationToken);
            }

            logger.LogDebug("Sending request log data to Apitally hub");
            using var stream = logFile.GetInputStream();
            var status = await SendHubRequestAsync(
                _httpClient.PostAsync(
                    GetHubUrlPath("log", $"uuid={logFile.Uuid}"),
                    new StreamContent(stream),
                    cancellationToken
                )
            );

            if (status == HubRequestStatus.RetryableError)
            {
                RequestLogger.RetryFileLater(logFile);
                break;
            }
            else if (status == HubRequestStatus.PaymentRequired)
            {
                RequestLogger.Clear();
                RequestLogger.SuspendFor(TimeSpan.FromHours(1));
                break;
            }
            else
            {
                logFile.Delete();
            }

            i++;
            if (i >= 10)
            {
                break;
            }
        }
    }

    private async Task<HubRequestStatus> SendHubRequestAsync(Task<HttpResponseMessage> requestTask)
    {
        try
        {
            var response = await _retryPolicy.ExecuteAsync(() => requestTask);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                logger.LogError("Invalid Apitally client ID: {ClientId}", options.Value.ClientId);
                Enabled = false;
                await StopAsync(CancellationToken.None);
                return HubRequestStatus.InvalidClientId;
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
            {
                string content = await response.Content.ReadAsStringAsync();
                logger.LogError("Received validation error from Apitally hub: {Content}", content);
                return HubRequestStatus.ValidationError;
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.PaymentRequired)
            {
                return HubRequestStatus.PaymentRequired;
            }
            else
            {
                response.EnsureSuccessStatusCode();
                return HubRequestStatus.OK;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error sending hub request");
            return HubRequestStatus.RetryableError;
        }
    }

    private async Task SyncAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!_startupDataSent)
            {
                await SendStartupDataAsync(cancellationToken);
            }
            await SendSyncDataAsync(cancellationToken);
            await SendLogDataAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during sync with Apitally hub");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SyncAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            int syncInterval = _initialSyncPeriod
                ? InitialSyncIntervalSeconds
                : SyncIntervalSeconds;
            if (_initialSyncPeriod && DateTime.UtcNow >= _initialSyncEndTime)
            {
                _initialSyncPeriod = false;
            }
            await Task.Delay(TimeSpan.FromSeconds(syncInterval), stoppingToken);
            await SyncAsync(stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Enabled)
        {
            await SyncAsync(cancellationToken);
        }
        await base.StopAsync(cancellationToken);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            base.Dispose();
            _httpClient.Dispose();
        }
    }

    public override void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    ~ApitallyClient()
    {
        Dispose(false);
    }
}
