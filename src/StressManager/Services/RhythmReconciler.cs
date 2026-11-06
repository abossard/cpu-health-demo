using StressManager.Actions;

namespace StressManager.Services;

/// <summary>
/// Re-applies the rhythm after a redeploy (new manager image) or a change in schedulable stress nodes.
/// Checks on startup, every 60 s, and within 15 s of a schedulable node count change.
/// </summary>
public sealed class RhythmReconciler(
    RhythmManager rhythm, StressNodes nodes, TimeProvider clock, ILogger<RhythmReconciler> log) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int? lastCount = null;
        var nextCheck = clock.GetUtcNow();
        using var timer = new PeriodicTimer(PollInterval, clock);
        do
        {
            try
            {
                var count = (await nodes.ListAsync(stoppingToken)).Count(n => n.Schedulable);
                if (count != lastCount || clock.GetUtcNow() >= nextCheck)
                {
                    if (lastCount is not null && count != lastCount)
                        log.LogInformation("rhythm reconciler: schedulable stress nodes {Old} -> {New}", lastCount, count);
                    await rhythm.RepairTemplateDriftAsync(stoppingToken);
                    lastCount = count;
                    nextCheck = clock.GetUtcNow() + CheckInterval;
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogError(ex, "rhythm reconciler: pass failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
