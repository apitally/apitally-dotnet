using Microsoft.Extensions.Hosting;

namespace Apitally.Hosting;

// StoppedAsync runs after the server and all other hosted services have stopped.
internal sealed class ApitallyHostedService(TelemetryRuntime runtime) : IHostedLifecycleService
{
    public Task StoppedAsync(CancellationToken cancellationToken) =>
        runtime.ShutdownAsync(cancellationToken);

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
