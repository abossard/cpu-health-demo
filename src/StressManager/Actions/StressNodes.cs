using k8s;
using k8s.Models;
using StressManager.Calculations;

namespace StressManager.Actions;

public sealed class StressNodes(IKubernetes kube, Settings settings, ILogger<StressNodes> log)
{
    public async Task<IReadOnlyList<StressNode>> ListAsync(CancellationToken ct)
    {
        var nodes = await Kube.Call(log, $"list nodes {settings.NodeLabel}",
            () => kube.CoreV1.ListNodeAsync(labelSelector: settings.NodeLabel, cancellationToken: ct),
            LogLevel.Debug);
        return nodes.Items
            .Select(n => new StressNode(n.Metadata.Name, n.Spec?.ProviderID, IsSchedulable(n)))
            .OrderBy(n => n.Name, StringComparer.Ordinal)
            .ToList();
    }

    public static bool IsSchedulable(V1Node node) =>
        node.Spec?.Unschedulable != true
        && node.Status?.Conditions?.Any(c => c.Type == "Ready" && c.Status == "True") == true;
}
