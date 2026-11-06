using System.Text.Json.Nodes;
using k8s.Models;

namespace StressManager.Calculations;

/// <summary>off: no rhythm CronJobs. active: none suspended. paused: all suspended. drifted: see RhythmDrift.</summary>
public enum RhythmState { Off, Active, Paused, Drifted }

public abstract record RhythmEvent;

/// <summary>Carries the compiled CronJobs; compiling needs cluster facts, so it happens before the transition.</summary>
public sealed record ApplyRhythm(IReadOnlyList<V1CronJob> CronJobs) : RhythmEvent;

public sealed record PauseRhythm : RhythmEvent;

public sealed record ResumeRhythm : RhythmEvent;

public sealed record ClearRhythm : RhythmEvent;

public sealed record CronJobRef(string Name, bool Suspended, string? SpecHash);

public abstract record CronJobEffect;

public sealed record CreateCronJob(V1CronJob CronJob) : CronJobEffect;

/// <summary>Update in place: the CronJob keeps its UID, so its running Job keeps running.</summary>
public sealed record ReplaceCronJob(V1CronJob CronJob) : CronJobEffect;

public sealed record DeleteCronJob(string Name) : CronJobEffect;

public sealed record SetSuspend(string Name, bool Suspend) : CronJobEffect;

/// <summary>Foreground-deletes active stress Jobs, so their pods get SIGTERM and write `end`.</summary>
public sealed record StopRunningJobs : CronJobEffect;

/// <summary>Rejected is set when the event does not apply (no effects then).</summary>
public sealed record RhythmTransition(IReadOnlyList<CronJobEffect> Effects, string? Rejected = null);

/// <summary>Effects are data; the action layer executes them in order.</summary>
public static class RhythmMachine
{
    public const string NoRhythm = "no rhythm";

    public static RhythmTransition Next(IReadOnlyList<CronJobRef> existing, RhythmEvent ev) => ev switch
    {
        ApplyRhythm apply => new RhythmTransition(Diff(existing, apply.CronJobs)),
        PauseRhythm or ResumeRhythm when existing.Count == 0 => new RhythmTransition([], NoRhythm),
        PauseRhythm => new RhythmTransition(
        [
            .. existing.Where(r => !r.Suspended).Select(r => (CronJobEffect)new SetSuspend(r.Name, true)),
            new StopRunningJobs(),
        ]),
        ResumeRhythm => new RhythmTransition(
            existing.Where(r => r.Suspended).Select(r => (CronJobEffect)new SetSuspend(r.Name, false)).ToList()),
        ClearRhythm when existing.Count == 0 => new RhythmTransition([]),
        ClearRhythm => new RhythmTransition(
        [
            new StopRunningJobs(),
            .. existing.Select(r => (CronJobEffect)new DeleteCronJob(r.Name)),
        ]),
        _ => throw new ArgumentOutOfRangeException(nameof(ev), ev, "unknown rhythm event"),
    };

    /// <summary>
    /// Deletes CronJobs the new rhythm drops, replaces changed ones in place, creates new ones.
    /// An unchanged CronJob (same name and spec hash) only gets its suspend flag aligned.
    /// </summary>
    private static List<CronJobEffect> Diff(IReadOnlyList<CronJobRef> existing, IReadOnlyList<V1CronJob> desired)
    {
        var current = existing.ToDictionary(r => r.Name, StringComparer.Ordinal);
        var desiredNames = desired.Select(c => c.Metadata.Name).ToHashSet(StringComparer.Ordinal);
        var effects = new List<CronJobEffect>();
        effects.AddRange(existing.Where(r => !desiredNames.Contains(r.Name)).Select(r => new DeleteCronJob(r.Name)));
        foreach (var cronJob in desired)
        {
            var suspend = cronJob.Spec.Suspend == true;
            if (!current.TryGetValue(cronJob.Metadata.Name, out var old))
                effects.Add(new CreateCronJob(cronJob));
            else if (old.SpecHash != cronJob.Metadata.Annotations[RhythmCompiler.SpecHashAnnotation])
                effects.Add(new ReplaceCronJob(cronJob));
            else if (old.Suspended != suspend)
                effects.Add(new SetSuspend(old.Name, suspend));
        }
        return effects;
    }

    /// <summary>JSON merge patch for a SetSuspend effect.</summary>
    public static string SuspendPatch(SetSuspend effect) =>
        new JsonObject { ["spec"] = new JsonObject { ["suspend"] = effect.Suspend } }.ToJsonString();

    public static CronJobRef ToRef(V1CronJob cronJob) => new(
        cronJob.Metadata.Name,
        cronJob.Spec?.Suspend == true,
        cronJob.Metadata.Annotations?.TryGetValue(RhythmCompiler.SpecHashAnnotation, out var hash) == true ? hash : null);
}
