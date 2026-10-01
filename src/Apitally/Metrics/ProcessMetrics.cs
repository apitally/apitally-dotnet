using System.Diagnostics;

namespace Apitally.Metrics;

// Process gauges observed at each collection.
internal sealed class ProcessMetrics
{
    private readonly Process process = Process.GetCurrentProcess();
    private readonly TimeProvider timeProvider;
    private readonly DateTimeOffset processStart;
    private long lastTimestamp;
    private TimeSpan lastProcessorTime;

    public ProcessMetrics(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider;
        processStart = ReadProcessStart(timeProvider);
        lastTimestamp = timeProvider.GetTimestamp();
        process.Refresh();
        lastProcessorTime = process.TotalProcessorTime;
    }

    public ProcessMetricValues Observe()
    {
        var timestamp = timeProvider.GetTimestamp();
        process.Refresh();
        var processorTime = process.TotalProcessorTime;
        var elapsed = timeProvider.GetElapsedTime(lastTimestamp, timestamp).TotalSeconds;
        var used = (processorTime - lastProcessorTime).TotalSeconds;
        lastTimestamp = timestamp;
        lastProcessorTime = processorTime;
        double? cpuUtilization =
            elapsed > 0 ? Math.Clamp(used / (elapsed * Environment.ProcessorCount), 0, 1) : null;
        return new(
            cpuUtilization,
            process.WorkingSet64,
            (timeProvider.GetUtcNow() - processStart).TotalSeconds
        );
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

// CPU utilization is null when no time has elapsed since the previous observation.
internal readonly record struct ProcessMetricValues(
    double? CpuUtilization,
    long MemoryUsage,
    double Uptime
);
