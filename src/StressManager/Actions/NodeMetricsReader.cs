using k8s;
using k8s.Models;
using StressManager.Calculations;

namespace StressManager.Actions;

public sealed class NodeMetricsReader(IKubernetes kube, Settings settings, ILogger<NodeMetricsReader> log)
{
    public async Task<IReadOnlyList<NodeCpu>> ReadAsync(CancellationToken ct)
    {
        var nodesTask = Kube.Call(log, $"list nodes {settings.NodeLabel}",
            () => kube.CoreV1.ListNodeAsync(labelSelector: settings.NodeLabel, cancellationToken: ct), LogLevel.Debug);
        var metricsTask = Kube.Call(log, $"list metrics.k8s.io/v1beta1 nodes {settings.NodeLabel}",
            () => kube.CustomObjects.ListClusterCustomObjectAsync<NodeMetricsList>(
                "metrics.k8s.io", "v1beta1", "nodes", labelSelector: settings.NodeLabel, cancellationToken: ct), LogLevel.Debug);
        await Task.WhenAll(nodesTask, metricsTask);
        var nodes = await nodesTask;
        var metrics = await metricsTask;

        var allocatable = nodes.Items.ToDictionary(
            n => n.Metadata.Name,
            n => n.Status?.Allocatable?.TryGetValue("cpu", out var cpu) == true ? NodeUsage.Value(cpu) : null);
        var samples = (metrics.Items ?? []).Select(m => new MetricSample(
            m.Metadata.Name,
            m.Usage?.TryGetValue("cpu", out var cpu) == true ? NodeUsage.Value(cpu) : null,
            m.Usage?.TryGetValue("memory", out var memory) == true ? NodeUsage.Value(memory) : null,
            m.Timestamp is { } t ? new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)) : null));
        return NodeUsage.Combine(allocatable, samples);
    }
}
