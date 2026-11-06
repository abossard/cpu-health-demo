using StressManager.Calculations;

namespace StressManager.Actions;

/// <summary>The GET shape on success, otherwise an HTTP status and message.</summary>
public sealed record RhythmOutcome(RhythmView? View, int Status, string? Error = null)
{
    public static RhythmOutcome Ok(RhythmView view) => new(view, StatusCodes.Status200OK);
}

/// <summary>
/// State lives only in the CronJobs. Mutations share one lock and run on ApplicationStopping,
/// so a closed browser tab cannot cut an apply in half.
/// </summary>
public sealed class RhythmManager(
    CronJobs cronJobs, StressNodes nodes, Settings settings, TimeProvider clock,
    IHostApplicationLifetime lifetime, ILogger<RhythmManager> log)
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<RhythmView> GetAsync(CancellationToken ct) => (await ReduceAsync(ct)).Reduced.View;

    public RhythmOutcome Preview(IEnumerable<Step?>? steps) =>
        Rhythm.Normalize(steps) is ({ } rhythm, _)
            ? RhythmOutcome.Ok(RhythmReducer.Preview(rhythm, clock.GetUtcNow()))
            : new RhythmOutcome(null, StatusCodes.Status400BadRequest, Rhythm.NoOnStep);

    /// <summary>Compiles against the current schedulable node count and applies as a diff, active.</summary>
    public async Task<RhythmOutcome> ApplyAsync(IEnumerable<Step?>? steps)
    {
        var (rhythm, error) = Rhythm.Normalize(steps);
        if (rhythm is null)
            return new RhythmOutcome(null, StatusCodes.Status400BadRequest, error);
        return await MutateAsync(async ct =>
        {
            var nodeCount = await SchedulableCountAsync(ct);
            if (nodeCount == 0)
                return new RhythmOutcome(null, StatusCodes.Status503ServiceUnavailable, $"no schedulable nodes with label {settings.NodeLabel}");
            log.LogInformation("rhythm: apply {Steps} (cycle {Cycle} min, padded {Padded}) on {Nodes} nodes",
                string.Join(" ", rhythm.Steps.Select(s => $"{s.Minutes}m@{s.Load}%")), rhythm.CycleMinutes, rhythm.PaddedMinutes, nodeCount);
            return await TransitionAsync(new ApplyRhythm(RhythmCompiler.Compile(rhythm, false, nodeCount, settings.JobTemplate)), ct);
        });
    }

    public Task<RhythmOutcome> PauseAsync() => MutateAsync(ct => TransitionAsync(new PauseRhythm(), ct));

    public Task<RhythmOutcome> ResumeAsync() => MutateAsync(ct => TransitionAsync(new ResumeRhythm(), ct));

    public Task<RhythmOutcome> ClearAsync() => MutateAsync(ct => TransitionAsync(new ClearRhythm(), ct));

    /// <summary>Re-applies the stored rhythm when only the Job template or node count changed. Keeps suspend.</summary>
    public async Task RepairTemplateDriftAsync(CancellationToken stoppingToken)
    {
        await _lock.WaitAsync(stoppingToken);
        try
        {
            var (reduced, nodeCount) = await ReduceAsync(stoppingToken);
            if (reduced.Drift != RhythmDrift.Template || reduced.Rhythm is null || nodeCount == 0)
                return;
            log.LogInformation("rhythm: template or node count changed, re-applying on {Nodes} nodes (paused={Paused})",
                nodeCount, reduced.Paused);
            var compiled = RhythmCompiler.Compile(reduced.Rhythm, reduced.Paused, nodeCount, settings.JobTemplate);
            await TransitionAsync(new ApplyRhythm(compiled), stoppingToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<RhythmOutcome> MutateAsync(Func<CancellationToken, Task<RhythmOutcome>> mutation)
    {
        var ct = lifetime.ApplicationStopping;
        await _lock.WaitAsync(ct);
        try
        {
            return await mutation(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<RhythmOutcome> TransitionAsync(RhythmEvent ev, CancellationToken ct)
    {
        var existing = await cronJobs.ListRhythmAsync(ct);
        var transition = RhythmMachine.Next(existing.Select(RhythmMachine.ToRef).ToList(), ev);
        if (transition.Rejected is { } rejected)
        {
            log.LogInformation("rhythm: {Event} rejected: {Reason}", ev.GetType().Name, rejected);
            return new RhythmOutcome(null, StatusCodes.Status409Conflict, rejected);
        }
        log.LogInformation("rhythm: {Event}, {Count} effects", ev.GetType().Name, transition.Effects.Count);
        await cronJobs.ExecuteAsync(transition.Effects, ct);
        return RhythmOutcome.Ok(await GetAsync(ct));
    }

    private async Task<(ReducedRhythm Reduced, int NodeCount)> ReduceAsync(CancellationToken ct)
    {
        var cronJobsTask = cronJobs.ListRhythmAsync(ct);
        var nodeCountTask = SchedulableCountAsync(ct);
        await Task.WhenAll(cronJobsTask, nodeCountTask);
        var nodeCount = await nodeCountTask;
        // Without a manager image no template can be built, so the template check is skipped.
        var expectedHash = settings.ManagerImage is null || nodeCount == 0 ? null : StressJobs.TemplateHash(nodeCount, settings.JobTemplate);
        return (RhythmReducer.Reduce(await cronJobsTask, clock.GetUtcNow(), expectedHash), nodeCount);
    }

    private async Task<int> SchedulableCountAsync(CancellationToken ct) =>
        (await nodes.ListAsync(ct)).Count(n => n.Schedulable);
}
