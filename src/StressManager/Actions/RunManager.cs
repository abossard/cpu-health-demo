using System.Net;
using k8s;
using k8s.Autorest;
using StressManager.Calculations;

namespace StressManager.Actions;

/// <summary>Reads the stress Jobs the rhythm CronJobs create, and stops running ones.</summary>
public sealed class RunManager(IKubernetes kube, Settings settings, ILogger<RunManager> log)
{
    public async Task<ActiveRun?> GetActiveAsync(CancellationToken ct) =>
        Runs.Active(await ListAsync(ct)) is { } active ? ActiveRun.From(active) : null;

    /// <summary>Foreground delete: pods get SIGTERM, the annotator writes `end`, then the Job goes away.</summary>
    public async Task StopRunningAsync(CancellationToken ct)
    {
        foreach (var run in (await ListAsync(ct)).Where(r => Runs.IsActive(r.Phase)))
        {
            try
            {
                await Kube.Call(log, $"delete job {run.Run.RunId} (foreground)",
                    () => kube.BatchV1.DeleteNamespacedJobAsync(run.Run.RunId, settings.Namespace,
                        propagationPolicy: "Foreground", cancellationToken: ct));
            }
            catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
            {
                log.LogInformation("k8s: job {Job} already gone", run.Run.RunId);
            }
        }
    }

    private async Task<IReadOnlyList<RunView>> ListAsync(CancellationToken ct)
    {
        var jobs = Kube.Call(log, $"list jobs {StressJobs.Selector}",
            () => kube.BatchV1.ListNamespacedJobAsync(settings.Namespace, labelSelector: StressJobs.Selector, cancellationToken: ct),
            LogLevel.Debug);
        var pods = Kube.Call(log, $"list pods {StressJobs.Selector}",
            () => kube.CoreV1.ListNamespacedPodAsync(settings.Namespace, labelSelector: StressJobs.Selector, cancellationToken: ct),
            LogLevel.Debug);
        await Task.WhenAll(jobs, pods);
        return Runs.FromJobs((await jobs).Items, (await pods).Items);
    }
}
