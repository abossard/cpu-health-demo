using k8s.Models;
using StressManager.Calculations;

namespace StressManager.Tests;

public class RhythmReducerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 20, 5, 0, TimeSpan.Zero);
    private static readonly string Hash = StressJobs.TemplateHash(3, TestData.Config);

    private static List<V1CronJob> Compiled(string steps, bool paused = false) =>
        RhythmCompiler.Compile(Rhythm.Normalize(TestData.Steps(steps)).Rhythm!, paused, 3, TestData.Config).ToList();

    [Theory]
    [InlineData("10@100 20@0", false, "active")]
    [InlineData("10@100 20@0", true, "paused")]
    [InlineData("5@100 10@0 5@50 10@0", false, "active")]
    [InlineData("7@100", true, "paused")]
    public void Reduce_StateFromSuspend(string steps, bool paused, string state)
    {
        var reduced = RhythmReducer.Reduce(Compiled(steps, paused), Now, Hash);

        Assert.Equal((state, RhythmDrift.None, paused), (reduced.View.State, reduced.Drift, reduced.Paused));
        Assert.Equal(TestData.Notation(Rhythm.Normalize(TestData.Steps(steps)).Rhythm!.Steps), TestData.Notation(reduced.View.Steps));
        Assert.All(reduced.View.CronJobs, c => Assert.Equal(paused, c.Suspended));
        Assert.Equal(state == "active", reduced.View.NextOn is not null);
    }

    [Theory]
    [InlineData("5@100 10@0 5@50 10@0", "rhythm-s0-0", 0, 5, 100)]
    [InlineData("5@100 10@0 5@50 10@0", "rhythm-s2-0", 2, 5, 50)]
    public void Reduce_CronJobViewsComeFromPlanAndJobTemplate(string steps, string name, int step, int minutes, int load)
    {
        var cronJob = RhythmReducer.Reduce(Compiled(steps), Now, Hash).View.CronJobs.Single(c => c.Name == name);

        Assert.Equal((step, minutes, load), (cronJob.Step, cronJob.Minutes, cronJob.Load));
    }

    [Fact]
    public void Reduce_NoCronJobsIsOff()
    {
        var reduced = RhythmReducer.Reduce([], Now, Hash);

        Assert.Equal((RhythmState.Off, RhythmDrift.None), (reduced.State, reduced.Drift));
        Assert.Equal(("off", 0, 0, (NextOnView?)null), (reduced.View.State, reduced.View.CycleMinutes, reduced.View.PaddedMinutes, reduced.View.NextOn));
        Assert.Empty(reduced.View.Steps);
        Assert.Empty(reduced.View.CronJobs);
    }

    [Theory]
    [InlineData("mixed-suspend")]
    [InlineData("missing-rhythm")]
    [InlineData("bad-rhythm")]
    [InlineData("rhythm-differs")]
    [InlineData("group-deleted")]
    [InlineData("extra-cronjob")]
    [InlineData("cron-edited")]
    public void Reduce_BrokenStructureIsDrifted(string drift)
    {
        var cronJobs = drift == "group-deleted" ? Compiled("15@100 30@0") : Compiled("5@100 10@0 5@50 10@0");
        var a = cronJobs[0];
        switch (drift)
        {
            case "mixed-suspend": a.Spec.Suspend = true; break;
            case "missing-rhythm": a.Metadata.Annotations.Remove("stress/rhythm"); break;
            case "bad-rhythm": foreach (var c in cronJobs) c.Metadata.Annotations["stress/rhythm"] = "{"; break;
            case "rhythm-differs": a.Metadata.Annotations["stress/rhythm"] = Compiled("10@100 20@0")[0].Metadata.Annotations["stress/rhythm"]; break;
            case "group-deleted": cronJobs.RemoveAt(1); break;
            case "extra-cronjob": cronJobs.Add(Compiled("10@0 5@80 15@0")[0]); cronJobs[^1].Metadata.Annotations["stress/rhythm"] = a.Metadata.Annotations["stress/rhythm"]; break;
            case "cron-edited": a.Spec.Schedule = "1,31 * * * *"; break;
        }

        var reduced = RhythmReducer.Reduce(cronJobs, Now, Hash);

        Assert.Equal((RhythmState.Drifted, RhythmDrift.Structure, "drifted"), (reduced.State, reduced.Drift, reduced.View.State));
        Assert.Null(reduced.View.NextOn);
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 4)]
    public void Reduce_TemplateOrNodeCountChangeIsTemplateDrift(bool paused, int nodeCount)
    {
        var reduced = RhythmReducer.Reduce(Compiled("10@100 20@0", paused), Now, StressJobs.TemplateHash(nodeCount, TestData.Config));

        Assert.Equal((RhythmState.Drifted, RhythmDrift.Template, paused), (reduced.State, reduced.Drift, reduced.Paused));
        Assert.Equal("10@100 20@0", TestData.Notation(reduced.Rhythm!.Steps));
    }

    [Theory]
    [InlineData(null, "active")]
    [InlineData("0123456789abcdef", "drifted")]
    public void Reduce_NullExpectedHashSkipsTemplateCheck(string? expected, string state) =>
        Assert.Equal(state, RhythmReducer.Reduce(Compiled("10@100 20@0"), Now, expected).View.State);

    [Theory]
    [InlineData("10@100 20@0", "2026-11-05T20:30:00Z", 0)]
    [InlineData("10@0 5@80 15@0", "2026-11-05T20:10:00Z", 1)]
    public void Reduce_ActiveHasNextOn(string steps, string at, int step) =>
        Assert.Equal(new NextOnView(DateTimeOffset.Parse(at), step), RhythmReducer.Reduce(Compiled(steps), Now, Hash).View.NextOn);

    [Theory]
    [InlineData("active")]
    [InlineData("off")]
    public void Reduce_ReportsSignalThresholds(string state)
    {
        var view = RhythmReducer.Reduce(state == "off" ? [] : Compiled("10@100 20@0"), Now, Hash).View;

        Assert.Equal(new SignalView("PT5M", 90, 98), view.Signal);
    }

    [Theory]
    [InlineData("7@100", "7@100 1@0", 8, 1, 2)]
    [InlineData("10@150 20@0", "10@100 20@0", 30, 0, 1)]
    public void Preview_IsNormalizedAndCompiled(string input, string steps, int cycle, int padded, int cronJobs)
    {
        var view = RhythmReducer.Preview(Rhythm.Normalize(TestData.Steps(input)).Rhythm!, Now);

        Assert.Equal("preview", view.State);
        Assert.Equal(steps, TestData.Notation(view.Steps));
        Assert.Equal((cycle, padded, cronJobs), (view.CycleMinutes, view.PaddedMinutes, view.CronJobs.Count));
        Assert.All(view.CronJobs, c => Assert.False(c.Suspended));
        Assert.NotNull(view.NextOn);
        Assert.Equal(SignalView.Current, view.Signal);
    }
}
