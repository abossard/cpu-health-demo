using k8s;
using StressManager.Actions;
using StressManager.Calculations;

namespace StressManager.Services;

/// <summary>
/// Closes runs whose pod died before it wrote `end`. Every 2 minutes it reads the last 3 hours of annotations
/// of each node entity and writes `end` (detail `backstop=true`) for every `start` whose Job is gone or finished.
/// </summary>
public sealed class EndAnnotationBackstop(
    StressNodes nodes, IKubernetes kube, Settings settings, TimeProvider clock, ILogger<EndAnnotationBackstop> log)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LookBack = TimeSpan.FromHours(3);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var writer = new AnnotationWriter(ArmClients.AnnotationClientOrNull(settings), settings, log);
        if (!writer.Enabled)
        {
            log.LogWarning("backstop: health model disabled, missing {Missing}", string.Join(", ", settings.MissingHealthModelVariables));
            return;
        }

        using var timer = new PeriodicTimer(Interval, clock);
        do
        {
            try
            {
                await PassAsync(writer, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogError(ex, "backstop: pass failed");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task PassAsync(AnnotationWriter writer, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var current = await nodes.ListAsync(ct);
        var jobs = await Kube.Call(log, $"list jobs {StressJobs.Selector}",
            () => kube.BatchV1.ListNamespacedJobAsync(settings.Namespace, labelSelector: StressJobs.Selector, cancellationToken: ct),
            LogLevel.Debug);
        var activeJobs = jobs.Items
            .Where(j => Runs.IsActive(Runs.Phase(j, [])))
            .Select(j => j.Metadata.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var node in current)
        {
            try
            {
                var stored = await writer.ListAsync(node.Name, now - LookBack, ct);
                foreach (var start in Annotations.MissingEnds(stored, activeJobs.Contains, now))
                {
                    log.LogWarning("backstop: run {RunId} on {Node} has no end annotation, writing one", start.RunId, node.Name);
                    await writer.TryWriteAsync(Annotations.ToRun(start), Annotations.End, node.Name, WriteTimeout, backstop: true, ct);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogError(ex, "backstop: node {Node} failed", node.Name);
            }
        }
    }
}
