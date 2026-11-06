using k8s.Models;

namespace StressManager.Calculations;

/// <summary>Raw Kubernetes quantities from metrics.k8s.io: CPU in cores, memory in bytes.</summary>
public sealed record MetricSample(string Node, decimal? Cpu, decimal? Memory, DateTimeOffset? Timestamp);

/// <summary>Response item of GET /api/metrics/nodes. Nulls when the node has no metrics yet.</summary>
public sealed record NodeCpu(string Node, long? CpuMillicores, double? CpuPercent, long? MemoryBytes, DateTimeOffset? Timestamp);

public static class NodeUsage
{
    /// <summary>Value of a Kubernetes quantity ("1500m" is 1.5), null when absent or out of the decimal range.</summary>
    public static decimal? Value(ResourceQuantity? quantity)
    {
        if (quantity is null)
            return null;
        try
        {
            return quantity.ToDecimal();
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    /// <summary>CPU usage as percent of allocatable, one decimal. Can exceed 100 (system daemons are outside allocatable).</summary>
    public static double? CpuPercent(decimal? usageCores, decimal? allocatableCores) =>
        usageCores is { } usage && allocatableCores is > 0 and { } allocatable
            ? (double)Math.Round(usage / allocatable * 100m, 1)
            : null;

    /// <summary>One entry per node (sorted by name); nodes without a metrics sample get nulls.</summary>
    public static IReadOnlyList<NodeCpu> Combine(
        IReadOnlyDictionary<string, decimal?> allocatableCpuByNode, IEnumerable<MetricSample> samples)
    {
        var byNode = samples
            .GroupBy(s => s.Node, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return allocatableCpuByNode
            .OrderBy(n => n.Key, StringComparer.Ordinal)
            .Select(n =>
            {
                var sample = byNode.GetValueOrDefault(n.Key);
                var cpu = sample?.Cpu;
                var memory = sample?.Memory;
                return new NodeCpu(
                    n.Key,
                    cpu is { } c ? (long)Math.Round(c * 1000m) : null,
                    CpuPercent(cpu, n.Value),
                    memory is { } m ? (long)Math.Round(m) : null,
                    sample?.Timestamp);
            })
            .ToList();
    }
}
