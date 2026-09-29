using Apitally.AspNetCore;
using Apitally.Export;
using Apitally.Logging;
using Apitally.Metrics;
using Apitally.Requests;
using Apitally.Tracing;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Resources;

namespace Apitally.Hosting;

// Owns everything Apitally runs within one host: preparation before the request pipeline is
// built, activation before the server starts and the final shutdown.
internal sealed class TelemetryRuntime : IAsyncDisposable
{
    private const int FlushTimeoutMilliseconds = 5_000;

    private readonly object sync = new();
    private readonly ErrorAggregates errorAggregates = new();
    private RuntimeState state = RuntimeState.Created;
    private RuntimeConfiguration configuration = null!;
    private TimeProvider timeProvider = TimeProvider.System;
    private Resource resource = Resource.Empty;
    private TracingIntegration? tracing;
    private TelemetrySpool? spool;
    private ExportHttpClient? httpClient;
    private ApitallyBatchProcessor<SpanExportEntry>? spanProcessor;
    private ApitallyBatchProcessor<LogSnapshot>? logProcessor;
    private ApitallyMetrics? metrics;
    private InternalEvents? events;
    private ApitallyLoggerProvider? loggerProvider;
    private ExportWorker? worker;

    private enum RuntimeState
    {
        Created,
        Prepared,
        Active,
        Stopped,
        Disabled,
    }

    public SdkDiagnostics Diagnostics { get; private set; } = SdkDiagnostics.None;
    public RequestRegistry? Registry { get; private set; }

    // Resolves the finalized configuration once. Suppressed and disabled hosts construct no
    // providers, spool or workers. Returns whether the host is prepared for activation.
    public bool Prepare(IServiceProvider services)
    {
        lock (sync)
        {
            if (state != RuntimeState.Created)
                return false;
            state = RuntimeState.Disabled;
            try
            {
                Diagnostics = new SdkDiagnostics(services.GetRequiredService<ILoggerFactory>());
                // Test hosts usually have no write token, so they return before it is validated.
                if (IsTestServer(services.GetRequiredService<IServer>()))
                    return false;
                var options = services.GetRequiredService<IOptions<ApitallyOptions>>().Value;
                if (RuntimeConfiguration.Resolve(options, Diagnostics) is not { } resolved)
                    return false;
                configuration = resolved;
                timeProvider = services.GetService<TimeProvider>() ?? TimeProvider.System;
                resource = OtlpEncoder.CreateResource(configuration.Env);
                tracing = services.GetRequiredService<TracingIntegration>();
                // A request sampling callback can raise the rate, so it needs every request recorded.
                tracing.Prepare(
                    services,
                    resource,
                    configuration.SampleOnRequest is null ? configuration.SampleRate : 1
                );
                state = RuntimeState.Prepared;
                return true;
            }
            catch (Exception exception)
            {
                Diagnostics.PreparationFailed(exception);
                tracing?.DisposeOwnedProvider();
                return false;
            }
        }
    }

    // Runs after the application pipeline is built and before the server accepts requests.
    public void Activate(IServiceProvider services)
    {
        lock (sync)
        {
            if (state != RuntimeState.Prepared)
                return;
            try
            {
                using (ExecutionContext.SuppressFlow())
                {
                    spool = new TelemetrySpool(Diagnostics, timeProvider);
                    httpClient = new ExportHttpClient(
                        configuration.OtlpEndpoint,
                        configuration.WriteToken,
                        configuration.Env,
                        configuration.Proxy
                    );
                    var redaction = new SpanRedaction(configuration, Diagnostics);
                    spanProcessor = new ApitallyBatchProcessor<SpanExportEntry>(entries =>
                        ExportSpans(entries, redaction)
                    );
                    logProcessor = new ApitallyBatchProcessor<LogSnapshot>(ExportLogs);
                    metrics = new ApitallyMetrics(resource, spool, timeProvider, Diagnostics);
                    worker = new ExportWorker(
                        spool,
                        httpClient,
                        timeProvider,
                        Diagnostics,
                        FlushIntake
                    );
                }
                events = new InternalEvents(logProcessor, timeProvider);
                Registry = new RequestRegistry(
                    configuration,
                    new RequestSampling(configuration, Diagnostics),
                    new ConsumerUpdates(events),
                    metrics,
                    errorAggregates,
                    spanProcessor,
                    logProcessor,
                    Diagnostics
                );
                tracing!.Attach(
                    new ApitallySpanProcessor(
                        Registry,
                        services.GetRequiredService<IHttpContextAccessor>(),
                        configuration.Env,
                        Diagnostics
                    )
                );
                if (configuration.CaptureLogs)
                {
                    loggerProvider = services.GetRequiredService<ApitallyLoggerProvider>();
                    loggerProvider.Attach(Registry, configuration.MaskLogRecord);
                }
                events.EmitStartup(configuration, GetPaths(services));
                worker.Start();
                state = RuntimeState.Active;
            }
            catch (Exception exception)
            {
                Diagnostics.ActivationFailed(exception);
                state = RuntimeState.Disabled;
                DisposeOwnedResources();
            }
        }
    }

    // Final shutdown after the host has stopped its server and services. The host token only
    // ends the host's wait and prevents further POSTs.
    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        Task cleanup;
        lock (sync)
        {
            if (state != RuntimeState.Active)
                return;
            state = RuntimeState.Stopped;
            // Request detail still awaiting completion is discarded; finalized requests and
            // recorded metrics remain eligible for the final cycle.
            Registry!.Cutoff();
            tracing!.Detach();
            loggerProvider?.Detach();
            cleanup = Task.Run(() => CleanupAsync(cancellationToken), CancellationToken.None);
        }
        try
        {
            await cleanup.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    // Covers hosts that fail after activation, for example when the server cannot bind.
    public async ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (state != RuntimeState.Active)
                return;
            state = RuntimeState.Stopped;
            Registry!.Cutoff();
            tracing!.Detach();
            loggerProvider?.Detach();
        }
        await worker!.StopAsync().ConfigureAwait(false);
        DisposeOwnedResources();
    }

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        try
        {
            await worker!.StopAsync().ConfigureAwait(false);
            events!.EmitErrorAggregates(errorAggregates);
            // Terminal shutdown joins the batch workers; the reader performs the final collection.
            spanProcessor!.Shutdown(Timeout.Infinite);
            logProcessor!.Shutdown(Timeout.Infinite);
            metrics!.Shutdown();
            spool!.CloseCurrentFiles();
            if (!cancellationToken.IsCancellationRequested)
                await worker.SendRemainingFilesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Diagnostics.ShutdownFailed(exception);
        }
        finally
        {
            DisposeOwnedResources();
        }
    }

    // Error aggregates are drained immediately before the log flush.
    private void FlushIntake()
    {
        events!.EmitErrorAggregates(errorAggregates);
        spanProcessor!.ForceFlush(FlushTimeoutMilliseconds);
        logProcessor!.ForceFlush(FlushTimeoutMilliseconds);
        metrics!.Collect(FlushTimeoutMilliseconds);
    }

    private void ExportSpans(IReadOnlyList<SpanExportEntry> entries, SpanRedaction redaction)
    {
        var spans = entries.Where(redaction.TryRedact).Select(entry => entry.Span).ToList();
        OtlpEncoder.EncodeRequests(
            spans,
            OtlpTraceMapper.BuildRequest,
            payload => spool!.Append(TelemetrySignal.Traces, payload),
            "traces",
            Diagnostics
        );
    }

    private void ExportLogs(IReadOnlyList<LogSnapshot> logs) =>
        OtlpEncoder.EncodeRequests(
            logs,
            chunk => OtlpLogMapper.BuildRequest(chunk, resource),
            payload => spool!.Append(TelemetrySignal.Logs, payload),
            "logs",
            Diagnostics
        );

    private List<EndpointPath> GetPaths(IServiceProvider services)
    {
        try
        {
            return EndpointMetadata.GetPaths(services);
        }
        catch (Exception exception)
        {
            Diagnostics.RouteEnumerationFailed(exception);
            return [];
        }
    }

    private void DisposeOwnedResources()
    {
        loggerProvider?.Detach();
        tracing?.Detach();
        tracing?.DisposeOwnedProvider();
        spanProcessor?.Dispose();
        logProcessor?.Dispose();
        metrics?.Dispose();
        httpClient?.Dispose();
        spool?.Dispose();
    }

    // In-memory TestServer hosts are application integration tests, not serving processes.
    private static bool IsTestServer(IServer server)
    {
        var type = server.GetType();
        return type.FullName == "Microsoft.AspNetCore.TestHost.TestServer"
            && type.Assembly.GetName().Name == "Microsoft.AspNetCore.TestHost";
    }
}
