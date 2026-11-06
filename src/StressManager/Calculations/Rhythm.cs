using System.Text.Json.Serialization;

namespace StressManager.Calculations;

/// <summary>Load 1-100 is ON, 0 is a break.</summary>
public sealed record Step(int Minutes, int Load)
{
    [JsonIgnore]
    public bool IsOn => Load > 0;
}

/// <summary>Cycle length divides 1440, anchored at 00:00 UTC.</summary>
public sealed record NormalizedRhythm(IReadOnlyList<Step> Steps, int CycleMinutes, int PaddedMinutes);

public static class Rhythm
{
    public const int MinutesPerDay = 1440;
    public const string NoOnStep = "rhythm needs at least one ON step (load 1-100)";

    public static readonly IReadOnlyList<int> DayDivisors =
        Enumerable.Range(1, MinutesPerDay).Where(d => MinutesPerDay % d == 0).ToList();

    /// <summary>Returns the normalized rhythm, or an error when no ON step remains.</summary>
    public static (NormalizedRhythm? Rhythm, string? Error) Normalize(IEnumerable<Step?>? input)
    {
        var steps = new List<Step>();
        var total = 0;
        foreach (var raw in input ?? [])
        {
            if (raw is null || raw.Minutes <= 0 || total >= MinutesPerDay)
                continue;
            var step = new Step(Math.Min(Math.Min(raw.Minutes, MinutesPerDay), MinutesPerDay - total), Math.Clamp(raw.Load, 0, 100));
            total += step.Minutes;
            if (!step.IsOn && steps.Count > 0 && !steps[^1].IsOn)
                steps[^1] = steps[^1] with { Minutes = steps[^1].Minutes + step.Minutes };
            else
                steps.Add(step);
        }

        if (!steps.Any(s => s.IsOn))
            return (null, NoOnStep);

        var cycle = DayDivisors.First(d => d >= total);
        var padded = cycle - total;
        if (padded > 0)
        {
            if (steps[^1].IsOn)
                steps.Add(new Step(padded, 0));
            else
                steps[^1] = steps[^1] with { Minutes = steps[^1].Minutes + padded };
        }
        return (new NormalizedRhythm(steps, cycle, padded), null);
    }

    public static IReadOnlyList<int> Offsets(IReadOnlyList<Step> steps)
    {
        var offsets = new List<int>(steps.Count);
        var at = 0;
        foreach (var step in steps)
        {
            offsets.Add(at);
            at += step.Minutes;
        }
        return offsets;
    }

    /// <summary>Day minutes (0-1439) at which a step starting at <paramref name="offset"/> fires.</summary>
    public static IEnumerable<int> Firings(int offset, int cycleMinutes) =>
        Enumerable.Range(0, MinutesPerDay / cycleMinutes).Select(k => offset + k * cycleMinutes);

    /// <summary>Next firing strictly after <paramref name="now"/>, with its step index. Null without ON step.</summary>
    public static (DateTimeOffset At, int Step)? NextOn(IReadOnlyList<Step> steps, int cycleMinutes, DateTimeOffset now)
    {
        if (cycleMinutes <= 0)
            return null;
        var utc = now.ToUniversalTime();
        var dayStart = new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero);
        var sinceDayStart = (utc - dayStart).TotalMinutes;
        var offsets = Offsets(steps);
        (DateTimeOffset At, int Step)? best = null;
        for (var i = 0; i < steps.Count; i++)
        {
            if (!steps[i].IsOn)
                continue;
            var k = (long)Math.Floor((sinceDayStart - offsets[i]) / cycleMinutes) + 1;
            var at = dayStart.AddMinutes(offsets[i] + k * cycleMinutes);
            if (best is null || at < best.Value.At)
                best = (at, i);
        }
        return best;
    }
}
