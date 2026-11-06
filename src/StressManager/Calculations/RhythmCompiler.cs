using System.Text.Json;
using k8s;
using k8s.Models;

namespace StressManager.Calculations;

public sealed record RhythmCronJobPlan(string Name, string Cron, int Step, int Offset, int Minutes, int Load);

/// <summary>Steps to CronJobs. Each CronJob carries the whole rhythm in one annotation, so the reducer can rebuild it.</summary>
public static class RhythmCompiler
{
    public const string AppLabelValue = "stress-schedule";
    public const string KindLabel = "stress/kind";
    public const string RhythmKind = "rhythm";
    public const string Selector = "app=" + AppLabelValue + "," + KindLabel + "=" + RhythmKind;
    public const string RhythmAnnotation = "stress/rhythm";
    public const string TemplateHashAnnotation = "stress/template-hash";
    public const string SpecHashAnnotation = "stress/spec-hash";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Name(int step, int group) => $"rhythm-s{step}-{group}";

    public static string Source(int step) => $"rhythm:{step}";

    public static IReadOnlyList<RhythmCronJobPlan> Plan(IReadOnlyList<Step> steps, int cycleMinutes)
    {
        var offsets = Rhythm.Offsets(steps);
        return steps
            .Select((step, i) => (step, i))
            .Where(s => s.step.IsOn)
            .SelectMany(s => CronExpressions(Rhythm.Firings(offsets[s.i], cycleMinutes))
                .Select((cron, j) => new RhythmCronJobPlan(Name(s.i, j), cron, s.i, offsets[s.i], s.step.Minutes, s.step.Load)))
            .ToList();
    }

    /// <summary>
    /// Groups day minutes into 5-field crons: per minute the set of hours, minutes sharing the
    /// same hour set become one expression. Ordered by first minute.
    /// </summary>
    public static IReadOnlyList<string> CronExpressions(IEnumerable<int> dayMinutes) =>
        dayMinutes
            .Select(d => (Hour: d / 60, Minute: d % 60))
            .GroupBy(t => t.Minute)
            .Select(g => (Minute: g.Key, Hours: g.Select(t => t.Hour).Distinct().Order().ToList()))
            .GroupBy(m => string.Join(',', m.Hours))
            .Select(g => (Minutes: g.Select(m => m.Minute).Order().ToList(), Hours: g.First().Hours))
            .OrderBy(g => g.Minutes[0])
            .Select(g => $"{string.Join(',', g.Minutes)} {HourField(g.Hours)} * * *")
            .ToList();

    /// <summary>"*" for all hours, "*/s" or "a-23/s" for a progression running to the end of the day, else a list.</summary>
    public static string HourField(IReadOnlyList<int> hours)
    {
        if (hours.Count == 24)
            return "*";
        if (hours.Count >= 3)
        {
            var step = hours[1] - hours[0];
            var progression = Enumerable.Range(0, hours.Count).All(i => hours[i] == hours[0] + i * step);
            if (progression && hours[^1] + step > 23)
                return hours[0] == 0 ? $"*/{step}" : $"{hours[0]}-23/{step}";
        }
        return string.Join(',', hours);
    }

    public static string SerializeRhythm(NormalizedRhythm rhythm) => JsonSerializer.Serialize(rhythm, Json);

    /// <summary>Null unless the text is a rhythm that normalizes to itself.</summary>
    public static NormalizedRhythm? ParseRhythm(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        try
        {
            var rhythm = JsonSerializer.Deserialize<NormalizedRhythm>(text, Json);
            if (rhythm?.Steps is null)
                return null;
            var renormalized = Rhythm.Normalize(rhythm.Steps).Rhythm;
            return renormalized is not null
                   && renormalized.Steps.SequenceEqual(rhythm.Steps)
                   && renormalized.CycleMinutes == rhythm.CycleMinutes
                   && rhythm.PaddedMinutes >= 0 && rhythm.PaddedMinutes < rhythm.CycleMinutes
                ? rhythm
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static IReadOnlyList<V1CronJob> Compile(NormalizedRhythm rhythm, bool paused, int nodeCount, JobTemplateConfig config)
    {
        var rhythmText = SerializeRhythm(rhythm);
        var templateHash = StressJobs.TemplateHash(nodeCount, config);
        return Plan(rhythm.Steps, rhythm.CycleMinutes)
            .Select(plan => ToCronJob(plan, rhythmText, templateHash, paused, nodeCount, config))
            .ToList();
    }

    /// <summary>Hash of labels, annotations (without the hash itself) and spec without suspend.</summary>
    public static string SpecHash(V1CronJob cronJob)
    {
        var annotations = (cronJob.Metadata?.Annotations ?? new Dictionary<string, string>())
            .Where(a => a.Key != SpecHashAnnotation)
            .OrderBy(a => a.Key, StringComparer.Ordinal);
        var labels = (cronJob.Metadata?.Labels ?? new Dictionary<string, string>()).OrderBy(l => l.Key, StringComparer.Ordinal);
        var spec = KubernetesJson.Deserialize<V1CronJobSpec>(KubernetesJson.Serialize(cronJob.Spec));
        spec.Suspend = null;
        return StressJobs.Hash(string.Join("\n",
            string.Join(",", labels.Select(l => $"{l.Key}={l.Value}")),
            string.Join(",", annotations.Select(a => $"{a.Key}={a.Value}")),
            KubernetesJson.Serialize(spec)));
    }

    private static V1CronJob ToCronJob(
        RhythmCronJobPlan plan, string rhythmText, string templateHash, bool paused, int nodeCount, JobTemplateConfig config)
    {
        var cronJob = new V1CronJob
        {
            Metadata = new V1ObjectMeta
            {
                Name = plan.Name,
                Labels = new Dictionary<string, string> { ["app"] = AppLabelValue, [KindLabel] = RhythmKind },
                Annotations = new Dictionary<string, string>
                {
                    [RhythmAnnotation] = rhythmText,
                    [TemplateHashAnnotation] = templateHash,
                },
            },
            Spec = new V1CronJobSpec
            {
                Schedule = plan.Cron,
                TimeZone = "Etc/UTC",
                ConcurrencyPolicy = "Forbid",
                StartingDeadlineSeconds = 120,
                SuccessfulJobsHistoryLimit = 1,
                FailedJobsHistoryLimit = 3,
                Suspend = paused,
                JobTemplate = StressJobs.BuildTemplate(
                    new StressRequest(plan.Minutes, plan.Load, Source(plan.Step)), nodeCount, config),
            },
        };
        cronJob.Metadata.Annotations[SpecHashAnnotation] = SpecHash(cronJob);
        return cronJob;
    }
}
