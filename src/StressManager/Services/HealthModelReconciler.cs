using StressManager.Actions;
using StressManager.Calculations;

namespace StressManager.Services;

/// <summary>Reconciles the health model on startup and when the stress node set changes. A failed pass is retried after a minute.</summary>
public sealed class HealthModelReconciler(
    StressNodes nodes, HealthModelClient healthModel, TimeProvider clock, ILogger<HealthModelReconciler> log)
    : BackgroundService
{
    private static readonly TimeSpan NodePollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        string? reconciled = null;
        var retryAt = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(NodePollInterval, clock);
        do
        {
            try
            {
                var current = await nodes.ListAsync(ct);
                var fingerprint = HealthModelSpec.Fingerprint(current);
                if (HealthModelSpec.ShouldReconcile(fingerprint, reconciled, clock.GetUtcNow(), retryAt))
                {
                    log.LogInformation("health model: reconcile ({Reason}), nodes: {Nodes}",
                        reconciled is null ? "startup" : "stress nodes changed", fingerprint.Length == 0 ? "none" : fingerprint);
                    try
                    {
                        await healthModel.ReconcileAsync(current, ct);
                        reconciled = fingerprint;
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        log.LogError(ex, "health model: reconcile failed, retrying in {Retry}", RetryInterval);
                        retryAt = clock.GetUtcNow() + RetryInterval;
                    }
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogError(ex, "health model: node poll failed");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}
