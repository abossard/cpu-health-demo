using k8s.Models;
using StressManager.Calculations;

namespace StressManager.Tests;

public class RunsTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 20, 0, 0, TimeSpan.Zero);

    private static V1Job Job(string name, string? condition = null, int succeeded = 0, int minutes = 10, int load = 80)
    {
        var template = StressJobs.BuildTemplate(new StressRequest(minutes, load, "rhythm:0"), 3, TestData.Config);
        template.Metadata.Name = name;
        var job = new V1Job { Metadata = template.Metadata, Spec = template.Spec };
        job.Status = new V1JobStatus
        {
            StartTime = Start.UtcDateTime,
            Succeeded = succeeded,
            Conditions = condition is null ? null : [new V1JobCondition { Type = condition, Status = "True" }],
        };
        return job;
    }

    private static IEnumerable<V1Pod> Pods(string job, params string[] phases) =>
        phases.Select((phase, i) => new V1Pod
        {
            Metadata = new V1ObjectMeta { Name = $"{job}-{i}", Labels = new Dictionary<string, string> { [StressJobs.JobNameLabel] = job } },
            Spec = new V1PodSpec { NodeName = $"node-{i}" },
            Status = new V1PodStatus { Phase = phase },
        });

    [Theory]
    [InlineData(null, 0, new string[0], RunPhase.Pending)]
    [InlineData(null, 0, new[] { "Pending", "Pending", "Pending" }, RunPhase.Pending)]
    [InlineData(null, 0, new[] { "Pending", "Running", "Pending" }, RunPhase.Running)]
    [InlineData(null, 1, new[] { "Succeeded", "Running", "Running" }, RunPhase.Running)]
    [InlineData(null, 3, new[] { "Succeeded", "Succeeded", "Succeeded" }, RunPhase.Succeeded)]
    [InlineData("SuccessCriteriaMet", 3, new[] { "Succeeded", "Succeeded", "Succeeded" }, RunPhase.Succeeded)]
    [InlineData("Complete", 3, new string[0], RunPhase.Succeeded)]
    [InlineData("FailureTarget", 0, new[] { "Failed", "Running", "Running" }, RunPhase.Failed)]
    [InlineData("Failed", 0, new[] { "Failed" }, RunPhase.Failed)]
    public void Phase_FromJobAndPods(string? condition, int succeeded, string[] podPhases, RunPhase expected) =>
        Assert.Equal(expected, Runs.Phase(Job("j", condition, succeeded), Pods("j", podPhases)));

    [Theory]
    [InlineData(new[] { "Running", "Running", "Running" }, new[] { "node-0", "node-1", "node-2" })]
    [InlineData(new[] { "Pending" }, new[] { "node-0" })]
    public void FromJobs_ReadsRunFromJob(string[] podPhases, string[] nodes)
    {
        var view = Assert.Single(Runs.FromJobs([Job("rhythm-s0-0-1")], Pods("rhythm-s0-0-1", podPhases).Concat(Pods("other", "Running"))));

        var run = view.Run;
        Assert.Equal(("rhythm-s0-0-1", 10, 80, "rhythm:0"), (run.RunId, run.Minutes, run.Load, run.Source));
        Assert.Equal((Start, Start.AddMinutes(10)), (run.StartedAt, run.EndsAt));
        Assert.Equal(nodes, run.Nodes);
    }

    [Theory]
    [InlineData("Complete", "Complete", null)]
    [InlineData("Complete", "none", "b")]
    [InlineData("none", "Complete", "a")]
    [InlineData("none", "none", "b")]
    [InlineData("Failed", "none", "b")]
    public void Active_IsNewestPendingOrRunningJob(string conditionA, string conditionB, string? expected)
    {
        var a = Job("a", conditionA == "none" ? null : conditionA);
        var b = Job("b", conditionB == "none" ? null : conditionB);
        b.Status.StartTime = Start.AddMinutes(30).UtcDateTime;

        Assert.Equal(expected, Runs.Active(Runs.FromJobs([b, a], []))?.Run.RunId);
    }

    [Theory]
    [InlineData(new[] { "Pending" }, "pending")]
    [InlineData(new[] { "Running", "Pending" }, "running")]
    public void ActiveRun_CarriesPhase(string[] podPhases, string phase)
    {
        var view = Runs.Active(Runs.FromJobs([Job("rhythm-s0-0-1")], Pods("rhythm-s0-0-1", podPhases)))!;

        var active = ActiveRun.From(view);

        Assert.Equal(("rhythm-s0-0-1", 10, 80, "rhythm:0", phase), (active.RunId, active.Minutes, active.Load, active.Source, active.Phase));
        Assert.Equal(view.Run.Nodes, active.Nodes);
    }
}
