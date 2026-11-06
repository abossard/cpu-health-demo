using k8s.Models;
using StressManager.Calculations;

namespace StressManager.Tests;

public class RhythmMachineTests
{
    /// <summary>"a b:s c:h" = a (running), b (suspended), c (running, stale spec hash).</summary>
    private static List<CronJobRef> Refs(string notation, IReadOnlyList<V1CronJob>? current = null) =>
        notation.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(n => n.Split(':'))
            .Select(p => new CronJobRef(
                p[0],
                p.Length > 1 && p[1] == "s",
                p.Length > 1 && p[1] == "h" ? "stale" : current?.FirstOrDefault(c => c.Metadata.Name == p[0])?.Metadata.Annotations["stress/spec-hash"]))
            .ToList();

    private static string Describe(IEnumerable<CronJobEffect> effects) =>
        string.Join(" ", effects.Select(e => e switch
        {
            CreateCronJob c => $"create:{c.CronJob.Metadata.Name}",
            ReplaceCronJob r => $"replace:{r.CronJob.Metadata.Name}",
            DeleteCronJob d => $"delete:{d.Name}",
            SetSuspend s => $"{(s.Suspend ? "suspend" : "resume")}:{s.Name}",
            StopRunningJobs => "stop-jobs",
            _ => "?",
        }));

    [Theory]
    // 5@100 10@0 5@50 10@0 compiles to rhythm-s0-0 and rhythm-s2-0
    [InlineData("", false, "create:rhythm-s0-0 create:rhythm-s2-0")]
    [InlineData("rhythm-s0-0 rhythm-s2-0", false, "")]
    [InlineData("rhythm-s0-0:h rhythm-s2-0", false, "replace:rhythm-s0-0")]
    [InlineData("rhythm-s0-0 rhythm-s1-0 rhythm-s2-0", false, "delete:rhythm-s1-0")]
    [InlineData("rhythm-s0-0:s rhythm-s2-0:s", false, "resume:rhythm-s0-0 resume:rhythm-s2-0")]
    [InlineData("rhythm-s0-0:s rhythm-s2-0:h", true, "replace:rhythm-s2-0")]
    [InlineData("old-0 rhythm-s2-0:h", false, "delete:old-0 create:rhythm-s0-0 replace:rhythm-s2-0")]
    public void Apply_IsADiff(string existing, bool paused, string effects)
    {
        var compiled = RhythmCompiler.Compile(Rhythm.Normalize(TestData.Steps("5@100 10@0 5@50 10@0")).Rhythm!, paused, 3, TestData.Config);

        var t = RhythmMachine.Next(Refs(existing, compiled), new ApplyRhythm(compiled));

        Assert.Null(t.Rejected);
        Assert.Equal(effects, Describe(t.Effects));
    }

    [Theory]
    [InlineData("pause", "r0 r1", "suspend:r0 suspend:r1 stop-jobs")]
    [InlineData("pause", "r0 r1:s", "suspend:r0 stop-jobs")]
    [InlineData("pause", "r0:s", "stop-jobs")]
    [InlineData("resume", "r0:s r1:s", "resume:r0 resume:r1")]
    [InlineData("resume", "r0", "")]
    [InlineData("resume", "r0 r1:s", "resume:r1")]
    [InlineData("clear", "r0 r1", "stop-jobs delete:r0 delete:r1")]
    [InlineData("clear", "r0:s", "stop-jobs delete:r0")]
    [InlineData("clear", "", "")]
    public void Transitions(string ev, string existing, string effects)
    {
        RhythmEvent e = ev switch { "pause" => new PauseRhythm(), "resume" => new ResumeRhythm(), _ => new ClearRhythm() };

        var t = RhythmMachine.Next(Refs(existing), e);

        Assert.Null(t.Rejected);
        Assert.Equal(effects, Describe(t.Effects));
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("resume")]
    public void PauseAndResume_WithoutRhythmAreRejected(string ev)
    {
        RhythmEvent e = ev == "pause" ? new PauseRhythm() : new ResumeRhythm();

        var t = RhythmMachine.Next([], e);

        Assert.Equal(RhythmMachine.NoRhythm, t.Rejected);
        Assert.Empty(t.Effects);
    }

    [Theory]
    [InlineData(true, """{"spec":{"suspend":true}}""")]
    [InlineData(false, """{"spec":{"suspend":false}}""")]
    public void SuspendPatch_IsMergePatch(bool suspend, string expected) =>
        Assert.Equal(expected, RhythmMachine.SuspendPatch(new SetSuspend("x", suspend)));
}
