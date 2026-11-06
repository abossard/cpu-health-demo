using k8s;
using k8s.Models;
using StressManager.Calculations;

namespace StressManager.Actions;

public sealed class CronJobs(IKubernetes kube, RunManager runs, Settings settings, ILogger<CronJobs> log)
{
    public async Task<IReadOnlyList<V1CronJob>> ListRhythmAsync(CancellationToken ct) =>
        (await Kube.Call(log, $"list cronjobs {RhythmCompiler.Selector}",
            () => kube.BatchV1.ListNamespacedCronJobAsync(settings.Namespace, labelSelector: RhythmCompiler.Selector, cancellationToken: ct),
            LogLevel.Debug)).Items.ToList();

    public async Task ExecuteAsync(IEnumerable<CronJobEffect> effects, CancellationToken ct)
    {
        foreach (var effect in effects)
        {
            switch (effect)
            {
                case CreateCronJob create:
                    await Kube.Call(log, $"create cronjob {create.CronJob.Metadata.Name} '{create.CronJob.Spec.Schedule}' suspend={create.CronJob.Spec.Suspend}",
                        () => kube.BatchV1.CreateNamespacedCronJobAsync(create.CronJob, settings.Namespace, cancellationToken: ct));
                    break;
                case ReplaceCronJob replace:
                    await Kube.Call(log, $"replace cronjob {replace.CronJob.Metadata.Name} '{replace.CronJob.Spec.Schedule}' suspend={replace.CronJob.Spec.Suspend}",
                        () => kube.BatchV1.ReplaceNamespacedCronJobAsync(replace.CronJob, replace.CronJob.Metadata.Name, settings.Namespace, cancellationToken: ct));
                    break;
                case DeleteCronJob delete:
                    await Kube.Call(log, $"delete cronjob {delete.Name}",
                        () => kube.BatchV1.DeleteNamespacedCronJobAsync(delete.Name, settings.Namespace,
                            propagationPolicy: "Background", cancellationToken: ct));
                    break;
                case SetSuspend suspend:
                    var patch = RhythmMachine.SuspendPatch(suspend);
                    await Kube.Call(log, $"patch cronjob {suspend.Name} {patch}",
                        () => kube.BatchV1.PatchNamespacedCronJobAsync(new V1Patch(patch, V1Patch.PatchType.MergePatch),
                            suspend.Name, settings.Namespace, cancellationToken: ct));
                    break;
                case StopRunningJobs:
                    await runs.StopRunningAsync(ct);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(effects), effect, "unknown cronjob effect");
            }
        }
    }
}
