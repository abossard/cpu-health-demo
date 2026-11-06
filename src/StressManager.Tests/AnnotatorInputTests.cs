using StressManager.Calculations;

namespace StressManager.Tests;

public class AnnotatorInputTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 20, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("n1", "rhythm-s0-0-29898560", "10", "80", "rhythm:0", null)]
    [InlineData("n1", "rhythm-s1-0-1", " 5 ", "100", "rhythm:1", null)]
    [InlineData(null, "r", "10", "80", "rhythm:0", "missing or invalid env: NODE_NAME")]
    [InlineData("n1", "", "ten", "80", "rhythm:0", "missing or invalid env: RUN_ID, MINUTES")]
    [InlineData("n1", "r", "10", null, null, "missing or invalid env: LOAD, SOURCE")]
    public void From(string? node, string? runId, string? minutes, string? load, string? source, string? error)
    {
        var env = new Dictionary<string, string?>
        {
            [EnvNames.NodeName] = node, [EnvNames.RunId] = runId, [EnvNames.Minutes] = minutes,
            [EnvNames.Load] = load, [EnvNames.Source] = source,
        };

        var (run, parsedNode, parseError) = AnnotatorInput.From(k => env.GetValueOrDefault(k), Now);

        Assert.Equal(error, parseError);
        if (error is not null)
        {
            Assert.Null(run);
            return;
        }
        Assert.Equal(node, parsedNode);
        Assert.Equal((runId, int.Parse(minutes!), int.Parse(load!), source), (run!.RunId, run.Minutes, run.Load, run.Source));
        Assert.Equal([node!], run.Nodes);
    }

    [Theory]
    [InlineData("/run/stress/started", "/run/stress/started")]
    [InlineData(" /run/stress/started ", "/run/stress/started")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void StartedFile(string? value, string? expected) =>
        Assert.Equal(expected, AnnotatorInput.StartedFile(k => k == EnvNames.StartedFile ? value : null));
}
