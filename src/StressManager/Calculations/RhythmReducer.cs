using System.Globalization;
using k8s.Models;

namespace StressManager.Calculations;

public sealed record RhythmCronJobView(string Name, string Cron, int Step, int Minutes, int Load, bool Suspended);

public sealed record NextOnView(DateTimeOffset At, int Step);

/// <summary>The health model signal, so the UI can show what a step must reach to turn a node Degraded or Unhealthy.</summary>
public sealed record SignalView(string TimeGrain, double Degraded, double Unhealthy)
{
    public static readonly SignalView Current =
        new(HealthModelSpec.TimeGrain, HealthModelSpec.DegradedThreshold, HealthModelSpec.UnhealthyThreshold);
}

/// <summary>Response of the rhythm endpoints.</summary>
public sealed record RhythmView(
    string State,
    IReadOnlyList<Step> Steps,
    int CycleMinutes,
    int PaddedMinutes,
    IReadOnlyList<RhythmCronJobView> CronJobs,
    NextOnView? NextOn,
    SignalView Signal);

/// <summary>
/// Template: the rhythm is intact but the Job template (images, env, node count) changed; re-applying fixes it.
/// Structure: annotations, names, crons or suspend flags do not form one rhythm; only a new apply fixes it.
/// </summary>
public enum RhythmDrift { None, Template, Structure }

/// <summary>Paused is true when every CronJob is suspended. Rhythm is null when it cannot be read back.</summary>
public sealed record ReducedRhythm(RhythmState State, RhythmDrift Drift, bool Paused, NormalizedRhythm? Rhythm, RhythmView View);

public static class RhythmReducer
{
    /// <summary>A null <paramref name="expectedTemplateHash"/> skips the template check.</summary>
    public static ReducedRhythm Reduce(IEnumerable<V1CronJob> rhythmCronJobs, DateTimeOffset now, string? expectedTemplateHash)
    {
        var cronJobs = rhythmCronJobs.OrderBy(c => c.Metadata?.Name, StringComparer.Ordinal).ToList();
        if (cronJobs.Count == 0)
            return new ReducedRhythm(RhythmState.Off, RhythmDrift.None, false, null,
                new RhythmView("off", [], 0, 0, [], null, SignalView.Current));

        var texts = cronJobs.Select(c => Annotation(c, RhythmCompiler.RhythmAnnotation)).Distinct().ToList();
        var rhythm = texts.Count == 1 ? RhythmCompiler.ParseRhythm(texts[0]) : null;
        var plan = rhythm is null ? [] : RhythmCompiler.Plan(rhythm.Steps, rhythm.CycleMinutes);
        var planSetEqual = rhythm is not null
            && plan.Select(p => (p.Name, p.Cron)).ToHashSet().SetEquals(cronJobs.Select(c => (c.Metadata.Name, c.Spec?.Schedule ?? "")));
        var suspended = cronJobs.Count(c => c.Spec?.Suspend == true);
        var paused = suspended == cronJobs.Count;
        var suspendConsistent = suspended == 0 || paused;
        var templateCurrent = expectedTemplateHash is null
            || cronJobs.All(c => Annotation(c, RhythmCompiler.TemplateHashAnnotation) == expectedTemplateHash);

        var drift = !planSetEqual || !suspendConsistent ? RhythmDrift.Structure
            : !templateCurrent ? RhythmDrift.Template
            : RhythmDrift.None;
        var state = drift != RhythmDrift.None ? RhythmState.Drifted
            : paused ? RhythmState.Paused
            : RhythmState.Active;

        var steps = plan.ToDictionary(p => p.Name);
        var views = cronJobs
            .Select(c => new RhythmCronJobView(
                c.Metadata.Name,
                c.Spec?.Schedule ?? "",
                steps.TryGetValue(c.Metadata.Name, out var p) ? p.Step : -1,
                TemplateInt(c, StressJobs.MinutesAnnotation),
                TemplateInt(c, StressJobs.LoadAnnotation),
                c.Spec?.Suspend == true))
            .OrderBy(v => v.Step).ThenBy(v => v.Name, StringComparer.Ordinal)
            .ToList();
        var next = state == RhythmState.Active ? NextOn(rhythm!.Steps, rhythm.CycleMinutes, now) : null;
        var view = new RhythmView(
            StateName(state), rhythm?.Steps ?? [], rhythm?.CycleMinutes ?? 0, rhythm?.PaddedMinutes ?? 0, views, next, SignalView.Current);
        return new ReducedRhythm(state, drift, paused, rhythm, view);
    }

    /// <summary>What an apply would create; nothing is written.</summary>
    public static RhythmView Preview(NormalizedRhythm rhythm, DateTimeOffset now) => new(
        "preview",
        rhythm.Steps,
        rhythm.CycleMinutes,
        rhythm.PaddedMinutes,
        RhythmCompiler.Plan(rhythm.Steps, rhythm.CycleMinutes)
            .Select(p => new RhythmCronJobView(p.Name, p.Cron, p.Step, p.Minutes, p.Load, false))
            .ToList(),
        NextOn(rhythm.Steps, rhythm.CycleMinutes, now),
        SignalView.Current);

    public static string StateName(RhythmState state) => state.ToString().ToLowerInvariant();

    private static NextOnView? NextOn(IReadOnlyList<Step> steps, int cycle, DateTimeOffset now) =>
        Rhythm.NextOn(steps, cycle, now) is { } n ? new NextOnView(n.At, n.Step) : null;

    private static string? Annotation(V1CronJob cronJob, string key) =>
        cronJob.Metadata?.Annotations?.TryGetValue(key, out var value) == true ? value : null;

    private static int TemplateInt(V1CronJob cronJob, string key) =>
        cronJob.Spec?.JobTemplate?.Metadata?.Annotations?.TryGetValue(key, out var text) == true
        && int.TryParse(text, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
}
