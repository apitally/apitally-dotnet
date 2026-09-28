using Apitally.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Apitally.Tests.Support;

// SDK diagnostics routed to an in-memory log collector.
internal sealed class DiagnosticsCollector : IDisposable
{
    private readonly ILoggerFactory loggerFactory;

    public DiagnosticsCollector()
    {
        loggerFactory = LoggerFactory.Create(builder =>
            builder.SetMinimumLevel(LogLevel.Trace).AddProvider(new FakeLoggerProvider(Collector))
        );
        Diagnostics = new SdkDiagnostics(loggerFactory);
    }

    public FakeLogCollector Collector { get; } = new();
    public SdkDiagnostics Diagnostics { get; }

    public IReadOnlyList<FakeLogRecord> Records(LogLevel level) =>
        Collector.GetSnapshot().Where(record => record.Level == level).ToList();

    public void Dispose() => loggerFactory.Dispose();
}
