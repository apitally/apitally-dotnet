namespace Apitally;

using System;
using System.Diagnostics;
using Apitally.Models;

class ResourceMonitor
{
    private bool _isFirstInterval = true;
    private readonly Process _process = Process.GetCurrentProcess();
    private long _lastTimestamp = Stopwatch.GetTimestamp();
    private TimeSpan _lastTotalProcessorTime = TimeSpan.Zero;

    public ResourceUsage? GetCpuMemoryUsage()
    {
        try
        {
            var currentTimestamp = Stopwatch.GetTimestamp();
            var currentTotalProcessorTime = _process.TotalProcessorTime;
            var memoryRss = _process.WorkingSet64;

            if (_isFirstInterval)
            {
                _lastTimestamp = currentTimestamp;
                _lastTotalProcessorTime = currentTotalProcessorTime;
                _isFirstInterval = false;
                return null;
            }

            var elapsedTicks = currentTimestamp - _lastTimestamp;
            var elapsedSeconds = (double)elapsedTicks / Stopwatch.Frequency;
            var elapsedProcessorTime = currentTotalProcessorTime - _lastTotalProcessorTime;

            var cpuPercent = 0.0;
            if (elapsedSeconds > 0)
            {
                cpuPercent = 100.0 * elapsedProcessorTime.TotalSeconds / elapsedSeconds;
            }

            _lastTimestamp = currentTimestamp;
            _lastTotalProcessorTime = currentTotalProcessorTime;

            return new ResourceUsage { CpuPercent = cpuPercent, MemoryRss = memoryRss };
        }
        catch (Exception)
        {
            return null;
        }
    }
}
