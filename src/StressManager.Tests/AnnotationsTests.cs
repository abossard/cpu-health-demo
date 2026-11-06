using StressManager.Calculations;

namespace StressManager.Tests;

public class AnnotationsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 19, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("start", 10, 100, "rhythm:0", "node-a", "Stress test start: 10 min @ 100% (rhythm:0)")]
    [InlineData("end", 10, 100, "rhythm:0", "node-a", "Stress test end: 10 min @ 100% (rhythm:0)")]
    [InlineData("start", 45, 40, "rhythm:3", "aks-stress-1-vmss000002", "Stress test start: 45 min @ 40% (rhythm:3)")]
    [InlineData("end", 5, 70, "rhythm:2", "n3", "Stress test end: 5 min @ 70% (rhythm:2)")]
    public void Build_Shape(string phase, int minutes, int load, string source, string node, string description)
    {
        var run = new Run("20261105-190000", minutes, load, source, T0, T0.AddMinutes(minutes), [node]);

        var content = Annotations.Build(run, phase, node);

        Assert.Equal(description, content.Description);
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["runId"] = "20261105-190000",
                ["phase"] = phase,
                ["minutes"] = minutes.ToString(),
                ["load"] = load.ToString(),
                ["source"] = source,
                ["node"] = node,
            },
            content.AnnotationDetails);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "true")]
    public void Build_BackstopDetail(bool backstop, string? expected)
    {
        var run = new Run("r1", 10, 100, "rhythm:0", T0, T0.AddMinutes(10), ["n"]);

        var details = Annotations.Build(run, Annotations.End, "n", backstop).AnnotationDetails;

        Assert.Equal(expected, details.TryGetValue("backstop", out var value) ? value : null);
    }

    private static StoredAnnotation Stored(string runId, string phase, int minuteOffset, int minutes = 10) =>
        new(runId, phase, T0.AddMinutes(minuteOffset), minutes, 100, "rhythm:0");

    [Theory]
    [InlineData("r1", "start", true)]
    [InlineData("r1", "end", false)]
    [InlineData("r2", "start", false)]
    public void Contains_MatchesRunIdAndPhase(string runId, string phase, bool expected) =>
        Assert.Equal(expected, Annotations.Contains([Stored("r1", "start", 0)], runId, phase));

    // Start at minute 0, 10 min run: the backstop may act after minute 15.
    [Theory]
    [InlineData(14, false, 0)] // inside minutes + 5
    [InlineData(16, false, 1)] // past the grace, Job finished or gone
    [InlineData(16, true, 0)]  // Job still active
    [InlineData(600, true, 0)]
    public void MissingEnds_WaitsForGraceAndInactiveJob(int nowMinutes, bool jobActive, int expectedCount)
    {
        var result = Annotations.MissingEnds([Stored("r1", "start", 0)], _ => jobActive, T0.AddMinutes(nowMinutes));

        Assert.Equal(expectedCount, result.Count);
    }

    [Fact]
    public void MissingEnds_SkipsRunsWithEndAndReturnsOldestFirst()
    {
        var stored = new[]
        {
            Stored("closed", "start", 0), Stored("closed", "end", 10),
            Stored("late", "start", 60), Stored("early", "start", 20), Stored("early", "start", 25),
        };

        var result = Annotations.MissingEnds(stored, _ => false, T0.AddMinutes(200));

        Assert.Equal(["early", "late"], result.Select(r => r.RunId));
        Assert.Equal(T0.AddMinutes(20), result[0].At);
    }

    [Fact]
    public void ToRun_RebuildsTheRunFromItsStart()
    {
        var run = Annotations.ToRun(Stored("r1", "start", 5, minutes: 30));

        Assert.Equal(new Run("r1", 30, 100, "rhythm:0", T0.AddMinutes(5), T0.AddMinutes(35), []), run with { Nodes = [] });
    }
}
