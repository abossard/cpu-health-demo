using System.Globalization;
using System.Text.Json;
using StressManager.Calculations;

namespace StressManager.Tests;

/// <summary>Series notation: "0=1 2=3" is minute=state; per-minute strings "1123" hold one state per minute, '.' = no point.</summary>
public class TimelineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 20, 0, 0, TimeSpan.Zero);
    private static readonly long T0Ms = T0.ToUnixTimeMilliseconds();

    private static long At(int minute) => T0Ms + minute * 60_000L;

    private static List<StatePoint> Series(string notation) =>
        notation.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('='))
            .Select(p => new StatePoint(At(int.Parse(p[0])), int.Parse(p[1])))
            .ToList();

    private static string Notation(IEnumerable<StatePoint> points) =>
        string.Join(" ", points.Select(p => $"{(p.T - T0Ms) / 60_000}={p.State}"));

    private static List<StatePoint> PerMinute(string states) =>
        states.Select((c, i) => (c, i)).Where(x => x.c != '.').Select(x => new StatePoint(At(x.i), x.c - '0')).ToList();

    private static List<TimelineEntity> Nodes(params string[] names) => names.Select(n => new TimelineEntity(n, n, Timeline.Node, "flow")).ToList();

    private static TimelineData StatesOf(IReadOnlyList<TimelineEntity> entities, params (string Entity, string Notation)[] series) =>
        TimelineData.Empty with
        {
            Entities = entities,
            States = series.ToDictionary(s => s.Entity, s => (IReadOnlyList<StatePoint>)Series(s.Notation)),
        };

    private static TimelineAnnotation Annotation(string entity, string id, int minute, string phase = Annotations.Start, string runId = "r1") =>
        new(entity, id, At(minute), phase, runId, 5, 100, "rhythm:0", "Stress test");

    [Theory]
    [InlineData("Healthy", 1)]
    [InlineData("Degraded", 2)]
    [InlineData("Unhealthy", 3)]
    [InlineData("Unknown", 0)]
    [InlineData("Deleted", 0)]
    [InlineData(null, 0)]
    public void StateCode_MapsHealthState(string? state, int expected) => Assert.Equal(expected, Timeline.StateCode(state));

    public static TheoryData<ModelEntity[], Relationship[], TimelineEntity[]> EntityCases => new()
    {
        {
            [new("n2", "AKS node: n2", ["average-cpu"]), new("hm", "hm", []), new("n1", "AKS node: n1", ["average-cpu"]), new("flow", "Flow", [])],
            [new("hm-flow", "hm", "flow"), new("flow-n1", "flow", "n1"), new("flow-n2", "flow", "n2")],
            [new("hm", "hm", Timeline.Root, null), new("flow", "Flow", Timeline.Flow, "hm"),
             new("n1", "AKS node: n1", Timeline.Node, "flow"), new("n2", "AKS node: n2", Timeline.Node, "flow")]
        },
        {
            [new("b", "b", []), new("a", "a", [])],
            [],
            [new("a", "a", Timeline.Root, null), new("b", "b", Timeline.Root, null)]
        },
    };

    [Theory]
    [MemberData(nameof(EntityCases))]
    public void Entities_DeriveKindParentAndModelOrder(ModelEntity[] entities, Relationship[] relationships, TimelineEntity[] expected) =>
        Assert.Equal(expected, Timeline.Entities(entities, relationships));

    [Theory]
    [InlineData(null, 1440)]
    [InlineData(10, 12)]
    [InlineData(0, 2)]
    [InlineData(1439, 1440)]
    [InlineData(3000, 1440)]
    public void FetchFrom_IsLastSeenMinusOverlapWithinRetention(int? lastSeenMinutesAgo, int expectedMinutesAgo)
    {
        var now = T0.AddDays(1);
        long? lastSeen = lastSeenMinutesAgo is { } m ? now.AddMinutes(-m).ToUnixTimeMilliseconds() : null;

        Assert.Equal(now.AddMinutes(-expectedMinutesAgo), Timeline.FetchFrom(lastSeen, now));
    }

    [Theory]
    [InlineData("", "0=1 1=2", "0=1 1=2", "0=1 1=2")]
    [InlineData("0=1 1=2", "1=2 2=3", "0=1 1=2 2=3", "2=3")]
    [InlineData("0=1 1=2", "1=3", "0=1 1=3", "1=3")]
    [InlineData("0=1 1=2", "", "0=1 1=2", "")]
    [InlineData("", "2=1 0=1", "0=1 2=1", "0=1 2=1")]
    public void Merge_StatesByOccurredAt(string cached, string incoming, string merged, string added)
    {
        var entities = Nodes("a");

        var result = Timeline.Merge(StatesOf(entities, ("a", cached)), StatesOf(entities, ("a", incoming)));

        Assert.Equal(merged, Notation(result.Merged.States["a"]));
        Assert.Equal(added, Notation(result.Added.States.GetValueOrDefault("a") ?? []));
    }

    [Theory]
    [InlineData(50.0, 50.0, 1, 1, false)]
    [InlineData(50.0, 60.0, 1, 1, true)]
    [InlineData(null, 60.0, 0, 1, true)]
    [InlineData(95.0, 95.0, 1, 2, true)]
    public void Merge_SignalsReplaceChangedPoints(double? cachedValue, double? incomingValue, int cachedState, int incomingState, bool added)
    {
        var key = new SeriesKey("a", "average-cpu");
        TimelineData Data(double? value, int state) => TimelineData.Empty with
        {
            Entities = Nodes("a"),
            Signals = new Dictionary<SeriesKey, IReadOnlyList<SignalPoint>> { [key] = [new SignalPoint(At(0), value, state)] },
        };

        var result = Timeline.Merge(Data(cachedValue, cachedState), Data(incomingValue, incomingState));

        Assert.Equal(new SignalPoint(At(0), incomingValue, incomingState), Assert.Single(result.Merged.Signals[key]));
        Assert.Equal(added, result.Added.Signals.ContainsKey(key));
    }

    [Theory]
    [InlineData("x", "x y", "x y", "y")]
    [InlineData("x y", "y", "x y", "")]
    [InlineData("", "y x", "x y", "x y")]
    public void Merge_AnnotationsByEntityAndId(string cached, string incoming, string merged, string added)
    {
        TimelineData Data(string ids) => TimelineData.Empty with
        {
            Entities = Nodes("a"),
            Annotations = ids.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(id => Annotation("a", id, id == "x" ? 0 : 1)).ToList(),
        };

        var result = Timeline.Merge(Data(cached), Data(incoming));

        Assert.Equal(merged, string.Join(" ", result.Merged.Annotations.Select(a => a.Id)));
        Assert.Equal(added, string.Join(" ", result.Added.Annotations.Select(a => a.Id)));
    }

    [Theory]
    [InlineData("a b", "a", "a")]
    [InlineData("a", "a b", "a")]
    [InlineData("a b", "", "")]
    public void Merge_DropsSeriesOfEntitiesThatLeftTheModel(string cachedEntities, string incomingEntities, string kept)
    {
        string[] Names(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cache = StatesOf(Nodes(Names(cachedEntities)), Names(cachedEntities).Select(e => (e, "0=1")).ToArray()) with
        {
            Annotations = Names(cachedEntities).Select(e => Annotation(e, $"id-{e}", 0)).ToList(),
        };

        var merged = Timeline.Merge(cache, StatesOf(Nodes(Names(incomingEntities)))).Merged;

        Assert.Equal(Names(kept), merged.States.Keys.Order().ToArray());
        Assert.Equal(Names(kept), merged.Annotations.Select(a => a.Entity).ToArray());
    }

    private static TimelineData AllKinds(string minutes)
    {
        var times = minutes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
        return TimelineData.Empty with
        {
            Entities = Nodes("a"),
            States = new Dictionary<string, IReadOnlyList<StatePoint>> { ["a"] = times.Select(m => new StatePoint(At(m), 1)).ToList() },
            Signals = new Dictionary<SeriesKey, IReadOnlyList<SignalPoint>>
            {
                [new SeriesKey("a", "average-cpu")] = times.Select(m => new SignalPoint(At(m), 5, 1)).ToList(),
            },
            Annotations = times.Select(m => Annotation("a", $"id{m}", m)).ToList(),
            RawCpu = new Dictionary<string, IReadOnlyList<RawCpuPoint>> { ["a"] = times.Select(m => new RawCpuPoint(At(m), 5, 6)).ToList() },
        };
    }

    private static (string States, string Signals, string RawCpu, string Annotations) Minutes(TimelineData data)
    {
        string Join(IEnumerable<long> times) => string.Join(" ", times.Select(t => (t - T0Ms) / 60_000));
        return (Join(data.States["a"].Select(p => p.T)), Join(data.Signals[new SeriesKey("a", "average-cpu")].Select(p => p.T)),
            Join(data.RawCpu["a"].Select(p => p.T)), Join(data.Annotations.Select(a => a.T)));
    }

    [Theory]
    [InlineData("0 1 2", 0, "0 1 2")]
    [InlineData("0 1 2", 1, "1 2")]
    [InlineData("0 1 2", 3, "")]
    public void Prune_DropsPointsOlderThanCutoff(string minutes, int cutoffMinute, string expected) =>
        Assert.Equal((expected, expected, expected, expected), Minutes(Timeline.Prune(AllKinds(minutes), At(cutoffMinute))));

    [Theory]
    [InlineData("0 1 2", null, "0 1 2")]
    [InlineData("0 1 2", 0, "1 2")]
    [InlineData("0 1 2", 1, "2")]
    [InlineData("0 1 2", 2, "")]
    public void Since_KeepsOnlyNewerPoints(string minutes, int? sinceMinute, string expected)
    {
        var data = AllKinds(minutes);

        var slice = Timeline.Since(data, sinceMinute is { } m ? At(m) : null);

        Assert.Equal((expected, expected, expected, expected), Minutes(slice));
        Assert.Equal(data.Entities, slice.Entities);
    }

    [Theory]
    [InlineData("", "", 100)]
    [InlineData("0", "", -2)]
    [InlineData("3 10 7", "", 8)]
    [InlineData("", "10", 5)]
    [InlineData("10", "10", 5)]
    [InlineData("10", "20", 8)]
    public void Cursor_IsNewestPointMinusOverlapEarliestWins(string healthMinutes, string rawMinutes, int expectedMinute)
    {
        var data = AllKinds(healthMinutes) with { RawCpu = AllKinds(rawMinutes).RawCpu };

        Assert.Equal(At(expectedMinute), Timeline.Cursor(data, At(100)));
    }

    [Theory]
    [InlineData(null, 1440)]
    [InlineData(10, 15)]
    [InlineData(0, 5)]
    [InlineData(1437, 1440)]
    public void FetchFrom_RawCpuUsesFiveMinuteOverlap(int? lastSeenMinutesAgo, int expectedMinutesAgo)
    {
        var now = T0.AddDays(1);
        long? lastSeen = lastSeenMinutesAgo is { } m ? now.AddMinutes(-m).ToUnixTimeMilliseconds() : null;

        Assert.Equal(now.AddMinutes(-expectedMinutesAgo), Timeline.FetchFrom(lastSeen, now, Timeline.RawCpuOverlap));
    }

    [Theory]
    // cached / incoming raw CPU as "minute=average/maximum"; a later read of the same minute replaces the earlier value.
    [InlineData("", "0=4.1/4.3 1=50/99.6", "0=4.1/4.3 1=50/99.6", "0=4.1/4.3 1=50/99.6")]
    [InlineData("0=4.1/4.3 1=71.7/99.6", "1=99.6/99.6 2=98.6/99.6", "0=4.1/4.3 1=99.6/99.6 2=98.6/99.6", "1=99.6/99.6 2=98.6/99.6")]
    [InlineData("0=4.1/4.3 1=50/99.6", "1=50/99.6", "0=4.1/4.3 1=50/99.6", "")]
    [InlineData("0=4.1/4.3", "", "0=4.1/4.3", "")]
    public void Merge_RawCpuLateValuesReplace(string cached, string incoming, string merged, string added)
    {
        TimelineData Data(string notation) => TimelineData.Empty with
        {
            Entities = Nodes("a"),
            RawCpu = notation.Length == 0
                ? new Dictionary<string, IReadOnlyList<RawCpuPoint>>()
                : new Dictionary<string, IReadOnlyList<RawCpuPoint>> { ["a"] = RawPoints(notation) },
        };

        var result = Timeline.Merge(Data(cached), Data(incoming));

        Assert.Equal(merged, RawNotation(result.Merged.RawCpu["a"]));
        Assert.Equal(added, RawNotation(result.Added.RawCpu.GetValueOrDefault("a") ?? []));
    }

    private static List<RawCpuPoint> RawPoints(string notation) =>
        notation.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', '/'))
            .Select(p => new RawCpuPoint(At(int.Parse(p[0])), double.Parse(p[1], CultureInfo.InvariantCulture),
                p[2] == "-" ? null : double.Parse(p[2], CultureInfo.InvariantCulture)))
            .ToList();

    private static string RawNotation(IEnumerable<RawCpuPoint> points) =>
        string.Join(" ", points.Select(p => FormattableString.Invariant($"{(p.T - T0Ms) / 60_000}={p.Average}/{(p.Maximum is { } m ? m : "-")}")));

    [Theory]
    [InlineData("""{"value":[{"timeseries":[{"data":[{"timeStamp":"2026-11-05T20:01:00Z","average":71.715,"maximum":99.6},{"timeStamp":"2026-11-05T20:00:00Z","average":4.135,"maximum":4.34}]}]}]}""", "0=4.135/4.34 1=71.715/99.6")]
    [InlineData("""{"value":[{"timeseries":[{"data":[{"timeStamp":"2026-11-05T20:00:00Z"},{"timeStamp":"2026-11-05T20:01:00Z","average":98.58}]}]}]}""", "1=98.58/-")]
    [InlineData("""{"value":[{"timeseries":[]}]}""", "")]
    [InlineData("""{"value":[]}""", "")]
    [InlineData("""{}""", "")]
    public void ParseRawCpu_SkipsNullAveragesAndKeepsExactValues(string json, string expected) =>
        Assert.Equal(expected, RawNotation(Timeline.ParseRawCpu(json)));

    [Theory]
    [InlineData("/subscriptions/s/resourceGroups/mc/providers/Microsoft.Compute/virtualMachineScaleSets/v/virtualMachines/0", 0, 1440,
        "/subscriptions/s/resourceGroups/mc/providers/Microsoft.Compute/virtualMachineScaleSets/v/virtualMachines/0/providers/Microsoft.Insights/metrics?api-version=2023-10-01&metricnames=Percentage%20CPU&interval=PT1M&aggregation=Average,Maximum&timespan=2026-11-05T20:00:00Z/2026-11-06T20:00:00Z")]
    [InlineData("/r", 5, 10,
        "/r/providers/Microsoft.Insights/metrics?api-version=2023-10-01&metricnames=Percentage%20CPU&interval=PT1M&aggregation=Average,Maximum&timespan=2026-11-05T20:05:00Z/2026-11-05T20:10:00Z")]
    public void RawCpuPath_MatchesMetricsApiContract(string resourceId, int fromMinute, int toMinute, string expected) =>
        Assert.Equal(expected, Timeline.RawCpuPath(resourceId, T0.AddMinutes(fromMinute), T0.AddMinutes(toMinute)));

    [Theory]
    [InlineData(null, true, null)]
    [InlineData("", true, null)]
    [InlineData("2026-11-05T20:00:00Z", true, 0)]
    [InlineData("2026-11-05T20:03:00.000Z", true, 3)]
    [InlineData("2026-11-05T21:03:00+01:00", true, 3)]
    [InlineData("2026-11-05T20:03:00", true, 3)]
    [InlineData("yesterday", false, null)]
    public void TryParseSince_ReadsIso8601AsUtc(string? text, bool ok, int? expectedMinute)
    {
        Assert.Equal(ok, Timeline.TryParseSince(text, out var since));
        Assert.Equal(expectedMinute is { } m ? At(m) : null, since);
    }

    [Theory]
    // a, b: per-minute signal states; run r1 starts at minute 2 on a and b; expected "degraded,unhealthy,recovered" minutes.
    [InlineData("1122331111", "1112223311", null, "2,4,6", "3,6,8", 60L, 120L)]
    [InlineData("1122111111", "1111111111", null, "2,-,4", "-,-,-", null, null)]
    [InlineData("2211222111", "1111211111", null, "4,-,7", "4,-,5", 0L, null)]
    [InlineData("1112223311", "1111111111", 4, "3,-,-", "-,-,-", null, null)]
    [InlineData("1133111111", "1133111111", null, "2,2,4", "2,2,4", 0L, 0L)]
    public void Runs_OnsetPerNodeAndSpread(
        string a, string b, int? nextRunStart, string expectedA, string expectedB, long? spreadDegraded, long? spreadUnhealthy)
    {
        var annotations = new List<TimelineAnnotation>
        {
            Annotation("a", "1", 2), Annotation("b", "2", 2),
            Annotation("a", "3", 7, Annotations.End), Annotation("b", "4", 7, Annotations.End),
        };
        if (nextRunStart is { } next)
            annotations.Add(Annotation("a", "5", next, runId: "r2"));
        var states = new Dictionary<string, IReadOnlyList<StatePoint>> { ["a"] = PerMinute(a), ["b"] = PerMinute(b) };

        var run = Timeline.Runs(annotations, states).First(r => r.RunId == "r1");

        string Onset(RunNodeOnset n) => string.Join(",", new[] { n.DegradedAt, n.UnhealthyAt, n.RecoveredAt }
            .Select(t => t is { } v ? ((v - T0Ms) / 60_000).ToString() : "-"));
        Assert.Equal((At(2), At(7)), (run.StartedAt, run.EndedAt));
        Assert.Equal(["a", "b"], run.Nodes.Select(n => n.Entity));
        Assert.Equal((expectedA, expectedB), (Onset(run.Nodes[0]), Onset(run.Nodes[1])));
        Assert.Equal((spreadDegraded, spreadUnhealthy), (run.SpreadDegradedSec, run.SpreadUnhealthySec));
    }

    [Theory]
    [InlineData("start", true, false)]
    [InlineData("start end", true, true)]
    [InlineData("end", false, false)]
    public void Runs_NeedAStartAnnotation(string phases, bool listed, bool ended)
    {
        var annotations = phases.Split(' ').Select((phase, i) => Annotation("a", $"id{i}", i * 5, phase)).ToList();

        var runs = Timeline.Runs(annotations, new Dictionary<string, IReadOnlyList<StatePoint>>());

        Assert.Equal(listed, runs.Count == 1);
        Assert.Equal(ended, runs.SingleOrDefault()?.EndedAt is not null);
    }

    [Theory]
    [InlineData("1111", "1111", "")]
    [InlineData("1121", "1111", "2-3")]
    [InlineData("1221", "1111", "1-3")]
    [InlineData("1213", "1111", "1-2 3-4")]
    [InlineData("0111", "1111", "0-1")]
    [InlineData("12..", "1111", "1-4")]
    [InlineData("....", "1213", "")]
    [InlineData("2.........", "1111111111", "0-6")]
    public void Disagreements_OnMinuteGridMerged(string a, string b, string expected)
    {
        var states = new Dictionary<string, IReadOnlyList<StatePoint>> { ["a"] = PerMinute(a), ["b"] = PerMinute(b) };

        var intervals = Timeline.Disagreements(states, At(0), At(a.Length - 1));

        Assert.Equal(expected, string.Join(" ", intervals.Select(i => $"{(i[0] - T0Ms) / 60_000}-{(i[1] - T0Ms) / 60_000}")));
    }

    [Theory]
    [InlineData(null, 2)]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void Response_MatchesContractShape(int? sinceMinute, int expectedPoints)
    {
        var entities = Timeline.Entities(
            [new("n1", "AKS node: n1", ["average-cpu"]), new("hm", "hm", []), new("flow", "Flow", [])],
            [new("hm-flow", "hm", "flow"), new("flow-n1", "flow", "n1")]);
        var data = TimelineData.Empty with
        {
            Entities = entities,
            States = new Dictionary<string, IReadOnlyList<StatePoint>> { ["n1"] = Series("0=1 1=3"), ["hm"] = Series("0=1 1=3") },
            Signals = new Dictionary<SeriesKey, IReadOnlyList<SignalPoint>>
            {
                [new SeriesKey("n1", "average-cpu")] = [new SignalPoint(At(0), 4.5, 1), new SignalPoint(At(1), null, 0)],
            },
            Annotations = [Annotation("n1", "x", 0), Annotation("n1", "y", 1, Annotations.End)],
            RawCpu = new Dictionary<string, IReadOnlyList<RawCpuPoint>> { ["n1"] = [new RawCpuPoint(At(0), 4.1, 4.3), new RawCpuPoint(At(1), 99.6, null)] },
        };
        var snapshot = Timeline.Analyse(data, At(2));

        var response = Timeline.Response(snapshot, Timeline.Since(data, sinceMinute is { } m ? At(m) : null), At(-1440));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response, JsonSerializerOptions.Web));
        var root = json.RootElement;

        Assert.Equal(
            ["cursor", "thresholds", "entities", "entityStates", "signals", "rawCpu", "annotations", "runs", "disagreements"],
            root.EnumerateObject().Select(p => p.Name));
        Assert.Equal("2026-11-05T19:56:00.000Z", root.GetProperty("cursor").GetString());
        Assert.Equal("""{"degraded":90,"unhealthy":98}""", root.GetProperty("thresholds").GetRawText());
        Assert.Equal("""{"name":"hm","displayName":"hm","kind":"root","parent":null}""", root.GetProperty("entities")[0].GetRawText());
        Assert.Equal(expectedPoints, root.GetProperty("annotations").GetArrayLength());
        if (expectedPoints == 0)
        {
            Assert.Equal(0, root.GetProperty("entityStates").GetArrayLength());
            Assert.Equal(0, root.GetProperty("signals").GetArrayLength());
            Assert.Equal(0, root.GetProperty("rawCpu").GetArrayLength());
            return;
        }
        Assert.Equal(["hm", "n1"], root.GetProperty("entityStates").EnumerateArray().Select(e => e.GetProperty("entity").GetString()));
        Assert.Equal($"[{At(1)},3]", root.GetProperty("entityStates")[0].GetProperty("points")[expectedPoints - 1].GetRawText());
        Assert.Equal($"[{At(1)},null,0]", root.GetProperty("signals")[0].GetProperty("points")[expectedPoints - 1].GetRawText());
        Assert.Equal("n1", root.GetProperty("rawCpu")[0].GetProperty("entity").GetString());
        Assert.Equal($"[{At(1)},99.6,null]", root.GetProperty("rawCpu")[0].GetProperty("points")[expectedPoints - 1].GetRawText());
        Assert.Equal(
            $$"""{"entity":"n1","id":"y","t":{{At(1)}},"phase":"end","runId":"r1","minutes":5,"load":100,"source":"rhythm:0","description":"Stress test"}""",
            root.GetProperty("annotations")[expectedPoints - 1].GetRawText());
        Assert.Equal(
            $$"""{"runId":"r1","startedAt":{{At(0)}},"endedAt":{{At(1)}},"nodes":[{"entity":"n1","degradedAt":null,"unhealthyAt":null,"recoveredAt":null}],"spreadDegradedSec":null,"spreadUnhealthySec":null}""",
            root.GetProperty("runs")[0].GetRawText());
    }

    [Theory]
    [InlineData(99.6216666666667, 99.62)]
    [InlineData(4.555, 4.56)]
    [InlineData(100.0, 100.0)]
    [InlineData(null, null)]
    public void RoundValue_KeepsTwoDecimals(double? value, double? expected) => Assert.Equal(expected, Timeline.RoundValue(value));

    private static RunOnset Run(string id, long? degradedAt = null) =>
        new(id, At(0), null, [new RunNodeOnset("a", degradedAt, null, null)], null, null);

    private static TimelineSnapshot Snapshot(IReadOnlyList<TimelineEntity> entities, IReadOnlyList<RunOnset> runs, IReadOnlyList<long[]> disagreements) =>
        new(TimelineData.Empty with { Entities = entities }, runs, disagreements);

    [Theory]
    // previous / next: runs as "id" or "id:degradedMinute"; disagreements as "from-to"; entities as names.
    [InlineData("r1", "r1", "", "", "a", "a", false, null, null, null)]
    [InlineData("r1", "r1", "", "", "a", "a", true, "", null, null)]
    [InlineData("r1", "r1 r2", "", "", "a", "a", false, "", "r2", null)]
    [InlineData("r1 r2", "r1:3 r2", "", "", "a", "a", false, "", "r1", null)]
    [InlineData("r1", "r1", "0-1", "0-2", "a", "a", false, "", null, "0-2")]
    [InlineData("r1", "r1", "0-1", "0-1", "a", "a b", false, "", null, null)]
    public void Delta_CarriesOnlyWhatChanged(
        string previousRuns, string nextRuns, string previousDisagreements, string nextDisagreements,
        string previousEntities, string nextEntities, bool pointsAdded, string? expectedPoints, string? expectedRuns, string? expectedDisagreements)
    {
        List<RunOnset> Runs(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(r => r.Split(':')).Select(r => Run(r[0], r.Length > 1 ? At(int.Parse(r[1])) : null)).ToList();
        List<long[]> Intervals(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(iv => iv.Split('-').Select(int.Parse).Select(At).ToArray()).ToList();
        string[] Names(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var added = pointsAdded ? StatesOf(Nodes(Names(nextEntities)), ("a", "5=1")) : TimelineData.Empty with { Entities = Nodes(Names(nextEntities)) };

        var delta = Timeline.Delta(
            Snapshot(Nodes(Names(previousEntities)), Runs(previousRuns), Intervals(previousDisagreements)),
            Snapshot(Nodes(Names(nextEntities)), Runs(nextRuns), Intervals(nextDisagreements)),
            added, At(0));

        Assert.Equal(expectedPoints is null, delta is null);
        if (delta is null)
            return;
        Assert.Equal(pointsAdded ? 1 : 0, delta.EntityStates.Count);
        Assert.Equal(expectedRuns, delta.Runs is null ? null : string.Join(" ", delta.Runs.Select(r => r.RunId)));
        Assert.Equal(expectedDisagreements,
            delta.Disagreements is null ? null : string.Join(" ", delta.Disagreements.Select(iv => $"{(iv[0] - T0Ms) / 60_000}-{(iv[1] - T0Ms) / 60_000}")));
    }
}
