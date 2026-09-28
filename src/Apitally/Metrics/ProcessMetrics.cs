using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Apitally.Metrics;

// Process gauges observed at each collection. Uptime keeps idle collections nonempty.
internal sealed class ProcessMetrics
{
    private readonly object sync = new();
    private readonly Process process = Process.GetCurrentProcess();
    private readonly TimeProvider timeProvider;
    private readonly DateTimeOffset processStart;
    private long lastTimestamp;
    private TimeSpan lastProcessorTime;

    public ProcessMetrics(Meter meter, TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider;
        processStart = ReadProcessStart(timeProvider);
        lastTimestamp = timeProvider.GetTimestamp();
        lastProcessorTime = ReadProcessorTime();
        meter.CreateObservableGauge(
            "process.cpu.utilization",
            ObserveCpuUtilization,
            "1",
            "CPU utilization of the process, normalized across available CPUs"
        );
        meter.CreateObservableGauge(
            "process.memory.usage",
            ObserveMemoryUsage,
            "By",
            "Physical memory in use by the process"
        );
        meter.CreateObservableGauge(
            "process.uptime",
            () => (timeProvider.GetUtcNow() - processStart).TotalSeconds,
            "s",
            "Time since the process started"
        );
    }

    private IEnumerable<Measurement<double>> ObserveCpuUtilization()
    {
        lock (sync)
        {
            var timestamp = timeProvider.GetTimestamp();
            var processorTime = ReadProcessorTime();
            var elapsed = timeProvider.GetElapsedTime(lastTimestamp, timestamp).TotalSeconds;
            var used = (processorTime - lastProcessorTime).TotalSeconds;
            lastTimestamp = timestamp;
            lastProcessorTime = processorTime;
            if (elapsed <= 0)
                return [];
            var utilization = used / (elapsed * Environment.ProcessorCount);
            return [new Measurement<double>(Math.Clamp(utilization, 0, 1))];
        }
    }

    private IEnumerable<Measurement<long>> ObserveMemoryUsage()
    {
        lock (sync)
        {
            process.Refresh();
            return [new Measurement<long>(process.WorkingSet64)];
        }
    }

    private TimeSpan ReadProcessorTime()
    {
        process.Refresh();
        return process.TotalProcessorTime;
    }

    private DateTimeOffset ReadProcessStart(TimeProvider timeProvider)
    {
        try
        {
            return process.StartTime.ToUniversalTime();
        }
        catch
        {
            return timeProvider.GetUtcNow();
        }
    }
}
