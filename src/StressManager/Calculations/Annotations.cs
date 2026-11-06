using System.Globalization;
using Azure.ResourceManager.CloudHealth.Models;

namespace StressManager.Calculations;

/// <summary>An annotation already stored on a node entity, reduced to what the dedupe and the backstop need.</summary>
public sealed record StoredAnnotation(string RunId, string Phase, DateTimeOffset At, int Minutes, int Load, string Source);

public static class Annotations
{
    public const string Start = "start";
    public const string End = "end";
    public const string BackstopDetail = "backstop";

    /// <summary>Minutes after the planned end before the backstop closes a run that has no `end`.</summary>
    public const int BackstopGraceMinutes = 5;

    public static EntityAddDataAnnotationContent Build(Run run, string phase, string node, bool backstop = false)
    {
        var details = new Dictionary<string, string>
        {
            ["runId"] = run.RunId,
            ["phase"] = phase,
            ["minutes"] = run.Minutes.ToString(CultureInfo.InvariantCulture),
            ["load"] = run.Load.ToString(CultureInfo.InvariantCulture),
            ["source"] = run.Source,
            ["node"] = node,
        };
        if (backstop)
            details[BackstopDetail] = "true";
        return new EntityAddDataAnnotationContent(details)
        {
            Description = $"Stress test {phase}: {run.Minutes} min @ {run.Load}% ({run.Source})",
        };
    }

    public static bool Contains(IEnumerable<StoredAnnotation> stored, string runId, string phase) =>
        stored.Any(a => a.RunId == runId && a.Phase == phase);

    /// <summary>
    /// The `start` annotations that never got an `end`: the run's Job is not active any more and the start
    /// is older than the run length plus <see cref="BackstopGraceMinutes"/>.
    /// </summary>
    public static IReadOnlyList<StoredAnnotation> MissingEnds(
        IEnumerable<StoredAnnotation> stored, Func<string, bool> jobIsActive, DateTimeOffset now)
    {
        var list = stored.ToList();
        return list
            .Where(a => a.Phase == Start)
            .GroupBy(a => a.RunId, StringComparer.Ordinal)
            .Select(g => g.OrderBy(a => a.At).First())
            .Where(start => !Contains(list, start.RunId, End)
                && !jobIsActive(start.RunId)
                && start.At.AddMinutes(start.Minutes + BackstopGraceMinutes) < now)
            .OrderBy(a => a.At)
            .ToList();
    }

    /// <summary>The run a stored `start` belonged to, for writing its `end`.</summary>
    public static Run ToRun(StoredAnnotation start) =>
        new(start.RunId, start.Minutes, start.Load, start.Source, start.At, start.At.AddMinutes(start.Minutes), []);
}
