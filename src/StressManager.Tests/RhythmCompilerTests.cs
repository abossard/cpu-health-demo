using StressManager.Calculations;

namespace StressManager.Tests;

public class RhythmCompilerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 20, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23 }, "*")]
    [InlineData(new[] { 0, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22 }, "*/2")]
    [InlineData(new[] { 1, 3, 5, 7, 9, 11, 13, 15, 17, 19, 21, 23 }, "1-23/2")]
    [InlineData(new[] { 0, 8, 16 }, "*/8")]
    [InlineData(new[] { 4, 12, 20 }, "4-23/8")]
    [InlineData(new[] { 5 }, "5")]
    [InlineData(new[] { 1, 2 }, "1,2")]
    [InlineData(new[] { 0, 5, 10 }, "0,5,10")]
    public void HourField(int[] hours, string expected) =>
        Assert.Equal(expected, RhythmCompiler.HourField(hours));

    [Theory]
    [InlineData("10@100 20@0", new[] { "rhythm-s0-0=0,30 * * * *" })]
    [InlineData("60@100", new[] { "rhythm-s0-0=0 * * * *" })]
    [InlineData("1440@100", new[] { "rhythm-s0-0=0 0 * * *" })]
    [InlineData("10@0 5@80 15@0", new[] { "rhythm-s1-0=10,40 * * * *" })]
    [InlineData("5@100 10@0 5@50 10@0", new[] { "rhythm-s0-0=0,30 * * * *", "rhythm-s2-0=15,45 * * * *" })]
    [InlineData("60@0 60@100 1320@0", new[] { "rhythm-s1-0=0 1 * * *" })]
    // cycle 120: one firing every 2 hours
    [InlineData("30@100 90@0", new[] { "rhythm-s0-0=0 */2 * * *" })]
    // cycle 45 (lcm with 60 is 180): three hour sets
    [InlineData("15@100 30@0", new[] { "rhythm-s0-0=0,45 */3 * * *", "rhythm-s0-1=15 2-23/3 * * *", "rhythm-s0-2=30 1-23/3 * * *" })]
    // cycle 8 (7 padded to 8): even and odd hours alternate minute sets
    [InlineData("7@100", new[] { "rhythm-s0-0=0,8,16,24,32,40,48,56 */2 * * *", "rhythm-s0-1=4,12,20,28,36,44,52 1-23/2 * * *" })]
    public void Plan_GroupsFiringsIntoCrons(string steps, string[] expected)
    {
        var rhythm = Rhythm.Normalize(TestData.Steps(steps)).Rhythm!;

        var plan = RhythmCompiler.Plan(rhythm.Steps, rhythm.CycleMinutes);

        Assert.Equal(expected, plan.Select(p => $"{p.Name}={p.Cron}"));
    }

    [Theory]
    [InlineData("10@100 20@0", false, 3)]
    [InlineData("10@0 5@80 15@0", true, 2)]
    public void ToCronJob_Shape(string steps, bool paused, int nodes)
    {
        var rhythm = Rhythm.Normalize(TestData.Steps(steps)).Rhythm!;

        var cronJob = Assert.Single(RhythmCompiler.Compile(rhythm, paused, nodes, TestData.Config));

        var step = rhythm.Steps.Select((s, i) => (s, i)).Single(x => x.s.IsOn);
        Assert.Equal(("stress-schedule", "rhythm"), (cronJob.Metadata.Labels["app"], cronJob.Metadata.Labels["stress/kind"]));
        Assert.Equal(RhythmCompiler.SerializeRhythm(rhythm), cronJob.Metadata.Annotations["stress/rhythm"]);
        Assert.Equal(StressJobs.TemplateHash(nodes, TestData.Config), cronJob.Metadata.Annotations["stress/template-hash"]);
        Assert.Equal(RhythmCompiler.SpecHash(cronJob), cronJob.Metadata.Annotations["stress/spec-hash"]);
        Assert.Equal(("Etc/UTC", "Forbid", 120L), (cronJob.Spec.TimeZone, cronJob.Spec.ConcurrencyPolicy, cronJob.Spec.StartingDeadlineSeconds!.Value));
        Assert.Equal((1, 3), (cronJob.Spec.SuccessfulJobsHistoryLimit, cronJob.Spec.FailedJobsHistoryLimit));
        Assert.Equal(paused, cronJob.Spec.Suspend);
        var job = cronJob.Spec.JobTemplate;
        Assert.Equal(($"rhythm:{step.i}", step.s.Minutes.ToString(), step.s.Load.ToString()),
            (job.Metadata.Annotations["stress/source"], job.Metadata.Annotations["stress/minutes"], job.Metadata.Annotations["stress/load"]));
        Assert.Equal(nodes, job.Spec.Parallelism);
    }

    [Theory]
    [InlineData("10@100 20@0")]
    [InlineData("7@100")]
    [InlineData("5@0 1@100 13@0 2@60")]
    public void ParseRhythm_RoundTripsSerialize(string steps)
    {
        var rhythm = Rhythm.Normalize(TestData.Steps(steps)).Rhythm!;

        var parsed = RhythmCompiler.ParseRhythm(RhythmCompiler.SerializeRhythm(rhythm))!;

        Assert.Equal((TestData.Notation(rhythm.Steps), rhythm.CycleMinutes, rhythm.PaddedMinutes),
            (TestData.Notation(parsed.Steps), parsed.CycleMinutes, parsed.PaddedMinutes));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"steps":[],"cycleMinutes":0,"paddedMinutes":0}""")]
    [InlineData("""{"steps":[{"minutes":10,"load":0}],"cycleMinutes":10,"paddedMinutes":0}""")]
    [InlineData("""{"steps":[{"minutes":7,"load":100}],"cycleMinutes":7,"paddedMinutes":0}""")]
    [InlineData("""{"steps":[{"minutes":10,"load":100},{"minutes":5,"load":0},{"minutes":5,"load":0}],"cycleMinutes":20,"paddedMinutes":0}""")]
    [InlineData("""{"steps":[{"minutes":10,"load":100},{"minutes":20,"load":0}],"cycleMinutes":60,"paddedMinutes":0}""")]
    [InlineData("""{"steps":[{"minutes":10,"load":100},{"minutes":20,"load":0}],"cycleMinutes":30,"paddedMinutes":30}""")]
    public void ParseRhythm_RejectsAnythingThatIsNotNormalized(string? text) =>
        Assert.Null(RhythmCompiler.ParseRhythm(text));

    [Theory]
    [InlineData(3, 3, true)]
    [InlineData(3, 2, false)]
    public void TemplateHash_ChangesWithNodeCount(int a, int b, bool same) =>
        Assert.Equal(same, StressJobs.TemplateHash(a, TestData.Config) == StressJobs.TemplateHash(b, TestData.Config));

    [Theory]
    [InlineData("image")]
    [InlineData("env")]
    [InlineData("label")]
    public void TemplateHash_ChangesWithClusterFacts(string change)
    {
        var changed = change switch
        {
            "image" => TestData.Config with { ManagerImage = "cr.example.io/stress-manager:2" },
            "env" => TestData.Config with { HealthModelEnv = new Dictionary<string, string> { [EnvNames.HealthModelName] = "other" } },
            _ => TestData.Config with { NodeLabelValue = "other" },
        };

        Assert.NotEqual(StressJobs.TemplateHash(3, TestData.Config), StressJobs.TemplateHash(3, changed));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void SpecHash_IgnoresSuspend(bool a, bool b)
    {
        var rhythm = Rhythm.Normalize(TestData.Steps("10@100 20@0")).Rhythm!;

        Assert.Equal(
            RhythmCompiler.Compile(rhythm, a, 3, TestData.Config)[0].Metadata.Annotations["stress/spec-hash"],
            RhythmCompiler.Compile(rhythm, b, 3, TestData.Config)[0].Metadata.Annotations["stress/spec-hash"]);
    }

    public static TheoryData<string> RandomRhythms()
    {
        var random = new Random(1440);
        var data = new TheoryData<string>();
        for (var c = 0; c < 150; c++)
        {
            var steps = Enumerable.Range(0, random.Next(1, 7))
                .Select(_ => new Step(random.Next(-2, random.Next(2) == 0 ? 30 : 400), random.Next(3) == 0 ? 0 : random.Next(-10, 120)));
            data.Add(TestData.Notation(steps));
        }
        foreach (var fixedCase in new[] { "10@100 20@0", "7@100", "1@100", "1440@100", "15@100 30@0", "5@0 1@100 13@0 2@60", "719@100 2@0" })
            data.Add(fixedCase);
        return data;
    }

    [Theory]
    [MemberData(nameof(RandomRhythms))]
    public void RoundTrip_ReduceCompileNormalize_IsNormalize(string input)
    {
        var (rhythm, error) = Rhythm.Normalize(TestData.Steps(input));
        if (rhythm is null)
        {
            Assert.Equal(Rhythm.NoOnStep, error);
            return;
        }

        var reduced = RhythmReducer.Reduce(
            RhythmCompiler.Compile(rhythm, false, 3, TestData.Config), Now, StressJobs.TemplateHash(3, TestData.Config));
        var view = reduced.View;

        Assert.Equal((RhythmState.Active, RhythmDrift.None), (reduced.State, reduced.Drift));
        Assert.Equal(TestData.Notation(rhythm.Steps), TestData.Notation(view.Steps));
        Assert.Equal((rhythm.CycleMinutes, rhythm.PaddedMinutes), (view.CycleMinutes, view.PaddedMinutes));
    }

    [Theory]
    [MemberData(nameof(RandomRhythms))]
    public void EveryOnMinuteFiresExactlyOncePerCycle_AcrossAFullDay(string input)
    {
        var rhythm = Rhythm.Normalize(TestData.Steps(input)).Rhythm;
        if (rhythm is null)
            return;
        var plan = RhythmCompiler.Plan(rhythm.Steps, rhythm.CycleMinutes);
        var offsets = Rhythm.Offsets(rhythm.Steps);
        var expected = rhythm.Steps
            .Select((s, i) => (s, i))
            .Where(x => x.s.IsOn)
            .SelectMany(x => Rhythm.Firings(offsets[x.i], rhythm.CycleMinutes).Select(d => (Minute: d, Step: x.i)))
            .ToDictionary(f => f.Minute, f => f.Step);

        for (var minute = 0; minute < Rhythm.MinutesPerDay; minute++)
        {
            var firing = plan.Where(p => TestData.CronMatches(p.Cron, minute)).ToList();
            if (expected.TryGetValue(minute, out var step))
                Assert.Equal(step, Assert.Single(firing).Step);
            else
                Assert.Empty(firing);
        }
        Assert.All(expected.GroupBy(e => e.Value), g => Assert.Equal(Rhythm.MinutesPerDay / rhythm.CycleMinutes, g.Count()));
    }
}
