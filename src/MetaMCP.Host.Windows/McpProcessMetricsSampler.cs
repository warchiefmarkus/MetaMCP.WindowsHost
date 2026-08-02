using System.Diagnostics;

namespace MetaMCP.Host;

internal sealed record McpProcessMetrics(
    int ProcessCount,
    double CpuPercent,
    long WorkingSetBytes,
    DateTimeOffset SampledAt);

internal sealed class McpProcessMetricsSampler
{
    private sealed record ProcessSample(
        TimeSpan TotalProcessorTime,
        long StopwatchTimestamp);

    private readonly Dictionary<int, ProcessSample> _previousSamples = [];

    public McpProcessMetrics Sample(IEnumerable<int> processIds)
    {
        var nowTimestamp = Stopwatch.GetTimestamp();
        var nextSamples = new Dictionary<int, ProcessSample>();
        var uniqueProcessIds = processIds
            .Where(processId => processId > 0)
            .Distinct()
            .ToArray();

        var processCount = 0;
        var workingSetBytes = 0L;
        var cpuPercent = 0d;

        foreach (var processId in uniqueProcessIds)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                process.Refresh();

                var totalProcessorTime = process.TotalProcessorTime;
                workingSetBytes += Math.Max(0, process.WorkingSet64);
                processCount++;

                if (_previousSamples.TryGetValue(processId, out var previous))
                {
                    var elapsedSeconds =
                        (nowTimestamp - previous.StopwatchTimestamp) /
                        (double)Stopwatch.Frequency;
                    var processorSeconds =
                        (totalProcessorTime - previous.TotalProcessorTime).TotalSeconds;
                    if (elapsedSeconds > 0 && processorSeconds >= 0)
                    {
                        cpuPercent += processorSeconds /
                            elapsedSeconds /
                            Math.Max(1, Environment.ProcessorCount) * 100d;
                    }
                }

                nextSamples[processId] = new ProcessSample(
                    totalProcessorTime,
                    nowTimestamp);
            }
            catch (ArgumentException)
            {
                // Process exited between telemetry retrieval and sampling.
            }
            catch (InvalidOperationException)
            {
                // Process is terminating or no longer exposes counters.
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Process counters are temporarily inaccessible.
            }
        }

        _previousSamples.Clear();
        foreach (var pair in nextSamples)
        {
            _previousSamples[pair.Key] = pair.Value;
        }

        return new McpProcessMetrics(
            processCount,
            Math.Clamp(cpuPercent, 0d, 100d),
            workingSetBytes,
            DateTimeOffset.Now);
    }

    public void Reset() => _previousSamples.Clear();
}
