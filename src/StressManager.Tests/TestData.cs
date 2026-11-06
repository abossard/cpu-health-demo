using StressManager.Calculations;

namespace StressManager.Tests;

/// <summary>Shared builders: step notation "10@100 5@0" (minutes@load) and a fixed template config.</summary>
public static class TestData
{
    public static readonly JobTemplateConfig Config = new(
        "cr.example.io/stress-manager:1",
        StressJobs.StressNgImage,
        "workload",
        "stress",
        new Dictionary<string, string>
        {
            [EnvNames.SubscriptionId] = "s1",
            [EnvNames.ResourceGroup] = "rg-demo",
            [EnvNames.HealthModelName] = "hm-demo",
        });

    public static List<Step> Steps(string notation) =>
        notation.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Split('@'))
            .Select(p => new Step(int.Parse(p[0]), int.Parse(p[1])))
            .ToList();

    public static string Notation(IEnumerable<Step> steps) => string.Join(" ", steps.Select(s => $"{s.Minutes}@{s.Load}"));

    /// <summary>True when the 5-field cron (as the compiler emits it: lists, ranges, steps, *) fires at the day minute.</summary>
    public static bool CronMatches(string cron, int dayMinute)
    {
        var fields = cron.Split(' ');
        return FieldMatches(fields[0], dayMinute % 60, 59) && FieldMatches(fields[1], dayMinute / 60, 23)
               && fields[2] == "*" && fields[3] == "*" && fields[4] == "*";
    }

    private static bool FieldMatches(string field, int value, int max) =>
        field.Split(',').Any(part =>
        {
            var (range, step) = part.Split('/') is [var r, var s] ? (r, int.Parse(s)) : (part, 1);
            var (from, to) = range == "*" ? (0, max)
                : range.Split('-') is [var a, var b] ? (int.Parse(a), int.Parse(b))
                : (int.Parse(range), int.Parse(range));
            return value >= from && value <= to && (value - from) % step == 0;
        });
}
