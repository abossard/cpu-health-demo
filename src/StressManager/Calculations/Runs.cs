using System.Globalization;
using k8s.Models;

namespace StressManager.Calculations;

public enum RunPhase { Pending, Running, Succeeded, Failed }

public sealed record RunView(Run Run, RunPhase Phase);

/// <summary>Response of GET /api/runs/active. Phase is "pending" or "running".</summary>
public sealed record ActiveRun(
    string RunId,
    int Minutes,
    int Load,
    string Source,
    DateTimeOffset StartedAt,
    DateTimeOffset EndsAt,
    IReadOnlyList<string> Nodes,
    string Phase)
{
    public static ActiveRun From(RunView view) => new(
        view.Run.RunId, view.Run.Minutes, view.Run.Load, view.Run.Source, view.Run.StartedAt, view.Run.EndsAt,
        view.Run.Nodes, view.Phase.ToString().ToLowerInvariant());
}

public static class Runs
{
    public static RunPhase Phase(V1Job job, IEnumerable<V1Pod> pods)
    {
        bool Condition(params string[] types) =>
            job.Status?.Conditions?.Any(c => types.Contains(c.Type) && c.Status == "True") == true;

        if (Condition("Failed", "FailureTarget"))
            return RunPhase.Failed;
        if (Condition("Complete", "SuccessCriteriaMet")
            || (job.Spec?.Completions is { } completions && (job.Status?.Succeeded ?? 0) >= completions))
            return RunPhase.Succeeded;
        return pods.Any(p => p.Status?.Phase == "Running") ? RunPhase.Running : RunPhase.Pending;
    }

    public static bool IsActive(RunPhase phase) => phase is RunPhase.Pending or RunPhase.Running;

    /// <summary>One view per stress Job, oldest first. Pods are matched by the job-name label.</summary>
    public static IReadOnlyList<RunView> FromJobs(IEnumerable<V1Job> jobs, IEnumerable<V1Pod> pods)
    {
        var podsByJob = pods
            .Where(p => p.Metadata?.Labels?.ContainsKey(StressJobs.JobNameLabel) == true)
            .ToLookup(p => p.Metadata.Labels[StressJobs.JobNameLabel], StringComparer.Ordinal);
        return jobs
            .Where(j => j.Metadata?.Name is not null)
            .Select(j => ToView(j, podsByJob[j.Metadata.Name].ToList()))
            .OrderBy(v => v.Run.StartedAt)
            .ThenBy(v => v.Run.RunId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The newest pending or running Job.</summary>
    public static RunView? Active(IReadOnlyList<RunView> runs) => runs.LastOrDefault(r => IsActive(r.Phase));

    private static RunView ToView(V1Job job, IReadOnlyList<V1Pod> pods)
    {
        var annotations = job.Metadata.Annotations ?? new Dictionary<string, string>();
        string? Annotation(string key) => annotations.TryGetValue(key, out var v) ? v : null;
        int Int(string key) => int.TryParse(Annotation(key), CultureInfo.InvariantCulture, out var v) ? v : 0;

        var started = job.Status?.StartTime ?? job.Metadata.CreationTimestamp;
        var startedAt = started is { } s ? new DateTimeOffset(DateTime.SpecifyKind(s, DateTimeKind.Utc)) : DateTimeOffset.MinValue;
        var minutes = Int(StressJobs.MinutesAnnotation);
        var run = new Run(
            job.Metadata.Name,
            minutes,
            Int(StressJobs.LoadAnnotation),
            Annotation(StressJobs.SourceAnnotation) ?? "unknown",
            startedAt,
            startedAt.AddMinutes(minutes),
            pods.Select(p => p.Spec?.NodeName).OfType<string>().Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList());
        return new RunView(run, Phase(job, pods));
    }
}
