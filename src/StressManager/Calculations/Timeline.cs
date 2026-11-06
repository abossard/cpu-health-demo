using System.Globalization;

namespace StressManager.Calculations;

/// <summary>An entity as read from ARM, before kind and parent are derived.</summary>
public sealed record ModelEntity(string Name, string DisplayName, IReadOnlyList<string> Signals);

/// <summary>An entity row of the timeline (docs/aks/timeline.md). Kind: root, flow or node.</summary>
public sealed record TimelineEntity(string Name, string DisplayName, string Kind, string? Parent);

public readonly record struct StatePoint(long T, int State);

public readonly record struct SignalPoint(long T, double? Value, int State);

public readonly record struct SeriesKey(string Entity, string Signal);

public sealed record TimelineAnnotation(
    string Entity, string Id, long T, string Phase, string RunId, int? Minutes, int? Load, string Source, string Description);

/// <summary>Cached history, or a slice of it (a delta). Series are sorted by time.</summary>
public sealed record TimelineData(
    IReadOnlyList<TimelineEntity> Entities,
    IReadOnlyDictionary<string, IReadOnlyList<StatePoint>> States,
    IReadOnlyDictionary<SeriesKey, IReadOnlyList<SignalPoint>> Signals,
    IReadOnlyList<TimelineAnnotation> Annotations)
{
    public static readonly TimelineData Empty = new([], new Dictionary<string, IReadOnlyList<StatePoint>>(),
        new Dictionary<SeriesKey, IReadOnlyList<SignalPoint>>(), []);

    public bool HasPoints => States.Values.Any(s => s.Count > 0) || Signals.Values.Any(s => s.Count > 0) || Annotations.Count > 0;
}

public sealed record RunNodeOnset(string Entity, long? DegradedAt, long? UnhealthyAt, long? RecoveredAt);

public sealed record RunOnset(
    string RunId, long StartedAt, long? EndedAt, IReadOnlyList<RunNodeOnset> Nodes, long? SpreadDegradedSec, long? SpreadUnhealthySec);

/// <summary>Cache content plus the analysis computed once per refresh.</summary>
public sealed record TimelineSnapshot(TimelineData Data, IReadOnlyList<RunOnset> Runs, IReadOnlyList<long[]> Disagreements)
{
    public static readonly TimelineSnapshot Empty = new(TimelineData.Empty, [], []);
}

public sealed record TimelineThresholds(double Degraded, double Unhealthy);

public sealed record EntityStates(string Entity, IReadOnlyList<long[]> Points);

/// <summary>Points are [t, value, state]; value may be null.</summary>
public sealed record SignalSeries(string Entity, string Signal, IReadOnlyList<object?[]> Points);

/// <summary>
/// Response of GET /api/timeline and of the SSE delta event (docs/aks/timeline.md, API).
/// In a delta, Runs holds only new or changed runs and is null when none changed; Disagreements is null when unchanged.
/// </summary>
public sealed record TimelineResponse(
    string Cursor,
    TimelineThresholds Thresholds,
    IReadOnlyList<TimelineEntity> Entities,
    IReadOnlyList<EntityStates> EntityStates,
    IReadOnlyList<SignalSeries> Signals,
    IReadOnlyList<TimelineAnnotation> Annotations,
    IReadOnlyList<RunOnset>? Runs,
    IReadOnlyList<long[]>? Disagreements);

/// <summary>Health timeline (docs/aks/timeline.md): merge, delta, retention, run onset and disagreement analysis. Pure.</summary>
public static class Timeline
{
    public const string Root = "root";
    public const string Flow = "flow";
    public const string Node = "node";

    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    /// <summary>Re-read window before the last seen point; absorbs late points (T3).</summary>
    public static readonly TimeSpan Overlap = TimeSpan.FromMinutes(2);

    /// <summary>A node state older than this at a grid minute counts as missing, not as a state.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    private const long Minute = 60_000;

    /// <summary>0 Unknown, 1 Healthy, 2 Degraded, 3 Unhealthy.</summary>
    public static int StateCode(string? state) => state?.ToLowerInvariant() switch
    {
        "healthy" => 1,
        "degraded" => 2,
        "unhealthy" => 3,
        _ => 0,
    };

    /// <summary>Signal values are sent with 2 decimals; more carries no information for a CPU percentage.</summary>
    public static double? RoundValue(double? value) => value is { } v ? Math.Round(v, 2, MidpointRounding.AwayFromZero) : null;

    /// <summary>Kind and parent from the relationships, ordered root, flow, then nodes by name.</summary>
    public static IReadOnlyList<TimelineEntity> Entities(IEnumerable<ModelEntity> entities, IEnumerable<Relationship> relationships)
    {
        var rels = relationships.ToList();
        var parents = rels.GroupBy(r => r.Child, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Parent, StringComparer.Ordinal);
        var withChildren = rels.Select(r => r.Parent).ToHashSet(StringComparer.Ordinal);
        return entities
            .Select(e =>
            {
                var parent = parents.GetValueOrDefault(e.Name);
                var kind = parent is null ? Root : withChildren.Contains(e.Name) ? Flow : Node;
                return new TimelineEntity(e.Name, e.DisplayName, kind, parent);
            })
            .OrderBy(e => KindRank(e.Kind))
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Start of the next read for one series: last seen minus the overlap, never before the retention window.</summary>
    public static DateTimeOffset FetchFrom(long? lastSeen, DateTimeOffset now)
    {
        var floor = now - Retention;
        if (lastSeen is not { } t)
            return floor;
        var from = DateTimeOffset.FromUnixTimeMilliseconds(t) - Overlap;
        return from > floor ? from : floor;
    }

    public static long? LastStateAt(TimelineData data, string entity) =>
        data.States.TryGetValue(entity, out var s) && s.Count > 0 ? s[^1].T : null;

    public static long? LastSignalAt(TimelineData data, SeriesKey key) =>
        data.Signals.TryGetValue(key, out var s) && s.Count > 0 ? s[^1].T : null;

    public static long? LastAnnotationAt(TimelineData data, string entity) =>
        data.Annotations.Where(a => a.Entity == entity).Select(a => (long?)a.T).Max();

    /// <summary>
    /// Merges a fresh read into the cache by (entity, occurredAt), (entity, signal, occurredAt) and (entity, annotation id).
    /// Entities are replaced by the incoming ones; series of entities that left the model are dropped.
    /// Added holds the points that are new or changed.
    /// </summary>
    public static (TimelineData Merged, TimelineData Added) Merge(TimelineData cache, TimelineData incoming)
    {
        var names = incoming.Entities.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);

        var states = new Dictionary<string, IReadOnlyList<StatePoint>>(StringComparer.Ordinal);
        var addedStates = new Dictionary<string, IReadOnlyList<StatePoint>>(StringComparer.Ordinal);
        foreach (var entity in cache.States.Keys.Concat(incoming.States.Keys).Where(names.Contains).Distinct())
        {
            var (merged, added) = MergeByKey(cache.States.GetValueOrDefault(entity) ?? [],
                incoming.States.GetValueOrDefault(entity) ?? [], p => p.T, p => p.T);
            states[entity] = merged;
            if (added.Count > 0)
                addedStates[entity] = added;
        }

        var signals = new Dictionary<SeriesKey, IReadOnlyList<SignalPoint>>();
        var addedSignals = new Dictionary<SeriesKey, IReadOnlyList<SignalPoint>>();
        foreach (var key in cache.Signals.Keys.Concat(incoming.Signals.Keys).Where(k => names.Contains(k.Entity)).Distinct())
        {
            var (merged, added) = MergeByKey(cache.Signals.GetValueOrDefault(key) ?? [],
                incoming.Signals.GetValueOrDefault(key) ?? [], p => p.T, p => p.T);
            signals[key] = merged;
            if (added.Count > 0)
                addedSignals[key] = added;
        }

        var (annotations, addedAnnotations) = MergeByKey(
            cache.Annotations.Where(a => names.Contains(a.Entity)).ToList(),
            incoming.Annotations.Where(a => names.Contains(a.Entity)),
            a => (a.Entity, a.Id), a => a.T);

        return (new TimelineData(incoming.Entities, states, signals, annotations),
            new TimelineData(incoming.Entities, addedStates, addedSignals, addedAnnotations));
    }

    /// <summary>Drops every point older than the cutoff.</summary>
    public static TimelineData Prune(TimelineData data, long cutoff) => data with
    {
        States = data.States.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<StatePoint>)kv.Value.Where(p => p.T >= cutoff).ToList(), StringComparer.Ordinal),
        Signals = data.Signals.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<SignalPoint>)kv.Value.Where(p => p.T >= cutoff).ToList()),
        Annotations = data.Annotations.Where(a => a.T >= cutoff).ToList(),
    };

    /// <summary>Only points with occurredAt &gt; since; all of them without since. Entities stay complete.</summary>
    public static TimelineData Since(TimelineData data, long? since) => since is not { } s ? data : data with
    {
        States = data.States.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<StatePoint>)kv.Value.Where(p => p.T > s).ToList(), StringComparer.Ordinal),
        Signals = data.Signals.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<SignalPoint>)kv.Value.Where(p => p.T > s).ToList()),
        Annotations = data.Annotations.Where(a => a.T > s).ToList(),
    };

    /// <summary>
    /// The next since for a client: the newest cached point minus the overlap, so points that arrive late for a lagging
    /// series are sent again. Clients merge by key. Fallback when nothing is cached.
    /// </summary>
    public static long Cursor(TimelineData data, long fallback)
    {
        var times = data.States.Values.SelectMany(s => s.Select(p => p.T))
            .Concat(data.Signals.Values.SelectMany(s => s.Select(p => p.T)))
            .Concat(data.Annotations.Select(a => a.T))
            .ToList();
        return times.Count == 0 ? fallback : times.Max() - (long)Overlap.TotalMilliseconds;
    }

    public static string FormatCursor(long t) =>
        DateTimeOffset.FromUnixTimeMilliseconds(t).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    public static bool TryParseSince(string? text, out long? since)
    {
        since = null;
        if (string.IsNullOrWhiteSpace(text))
            return true;
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            return false;
        since = parsed.ToUnixTimeMilliseconds();
        return true;
    }

    /// <summary>Signal state over time per node entity, all signals of a node merged by time.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<StatePoint>> NodeSignalStates(TimelineData data)
    {
        var nodes = data.Entities.Where(e => e.Kind == Node).Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        return data.Signals
            .Where(kv => nodes.Contains(kv.Key.Entity))
            .GroupBy(kv => kv.Key.Entity, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<StatePoint>)g.SelectMany(kv => kv.Value).OrderBy(p => p.T).Select(p => new StatePoint(p.T, p.State)).ToList(),
                StringComparer.Ordinal);
    }

    /// <summary>
    /// Per run (annotations grouped by runId, runs without a start annotation skipped), for each annotated node: the first
    /// Degraded-or-worse and the first Unhealthy at or after the run start, and the first Healthy after the worse of the two.
    /// Each run is analysed until the next run starts.
    /// </summary>
    public static IReadOnlyList<RunOnset> Runs(
        IEnumerable<TimelineAnnotation> annotations, IReadOnlyDictionary<string, IReadOnlyList<StatePoint>> nodeStates)
    {
        var runs = annotations
            .GroupBy(a => a.RunId, StringComparer.Ordinal)
            .Select(g => (RunId: g.Key,
                Start: g.Where(a => a.Phase == Annotations.Start).Select(a => (long?)a.T).Min(),
                End: g.Where(a => a.Phase == Annotations.End).Select(a => (long?)a.T).Max(),
                Nodes: g.Select(a => a.Entity).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList()))
            .Where(r => r.Start is not null)
            .OrderBy(r => r.Start)
            .ThenBy(r => r.RunId, StringComparer.Ordinal)
            .ToList();

        return runs.Select((run, i) =>
        {
            var from = run.Start!.Value;
            var until = i + 1 < runs.Count ? runs[i + 1].Start!.Value : long.MaxValue;
            var nodes = run.Nodes.Select(node =>
            {
                var points = (nodeStates.GetValueOrDefault(node) ?? []).Where(p => p.T >= from && p.T < until).ToList();
                var degraded = First(points, p => p.State >= 2);
                var unhealthy = First(points, p => p.State == 3);
                var worst = Math.Max(degraded ?? long.MinValue, unhealthy ?? long.MinValue);
                var recovered = degraded is null ? null : First(points, p => p.T > worst && p.State == 1);
                return new RunNodeOnset(node, degraded, unhealthy, recovered);
            }).ToList();
            return new RunOnset(run.RunId, from, run.End, nodes,
                SpreadSec(nodes.Select(n => n.DegradedAt)), SpreadSec(nodes.Select(n => n.UnhealthyAt)));
        }).ToList();
    }

    /// <summary>
    /// Intervals [start, end] on a 1-minute grid within [from, to] where the known node states differ, adjacent minutes merged.
    /// A node's state at a minute is its latest point at or before it, unless that point is older than StaleAfter.
    /// </summary>
    public static IReadOnlyList<long[]> Disagreements(
        IReadOnlyDictionary<string, IReadOnlyList<StatePoint>> nodeStates, long from, long to)
    {
        var series = nodeStates.Values.Where(s => s.Count > 0).ToList();
        var cursors = new int[series.Count];
        var stale = (long)StaleAfter.TotalMilliseconds;
        var intervals = new List<long[]>();
        for (var m = (from + Minute - 1) / Minute * Minute; m <= to; m += Minute)
        {
            int? first = null;
            var differ = false;
            for (var i = 0; i < series.Count; i++)
            {
                while (cursors[i] < series[i].Count && series[i][cursors[i]].T <= m)
                    cursors[i]++;
                if (cursors[i] == 0)
                    continue;
                var point = series[i][cursors[i] - 1];
                if (m - point.T > stale)
                    continue;
                first ??= point.State;
                differ |= point.State != first;
            }
            if (!differ)
                continue;
            if (intervals.Count > 0 && intervals[^1][1] == m)
                intervals[^1][1] = m + Minute;
            else
                intervals.Add([m, m + Minute]);
        }
        return intervals;
    }

    /// <summary>Cache content after a refresh plus its analysis over the retention window ending at now.</summary>
    public static TimelineSnapshot Analyse(TimelineData data, long now)
    {
        var nodeStates = NodeSignalStates(data);
        return new TimelineSnapshot(data, Runs(data.Annotations, nodeStates),
            Disagreements(nodeStates, now - (long)Retention.TotalMilliseconds, now));
    }

    /// <summary>The wire shape for a slice (full, since-filtered or a refresh delta) of a snapshot.</summary>
    public static TimelineResponse Response(TimelineSnapshot snapshot, TimelineData slice, long fallbackCursor)
    {
        var order = snapshot.Data.Entities.Select((e, i) => (e.Name, i)).ToDictionary(x => x.Name, x => x.i, StringComparer.Ordinal);
        int Rank(string entity) => order.GetValueOrDefault(entity, int.MaxValue);

        return new TimelineResponse(
            FormatCursor(Cursor(snapshot.Data, fallbackCursor)),
            new TimelineThresholds(HealthModelSpec.DegradedThreshold, HealthModelSpec.UnhealthyThreshold),
            snapshot.Data.Entities,
            slice.States.Where(kv => kv.Value.Count > 0)
                .OrderBy(kv => Rank(kv.Key)).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new EntityStates(kv.Key, kv.Value.Select(p => new[] { p.T, p.State }).ToList()))
                .ToList(),
            slice.Signals.Where(kv => kv.Value.Count > 0)
                .OrderBy(kv => Rank(kv.Key.Entity)).ThenBy(kv => kv.Key.Entity, StringComparer.Ordinal).ThenBy(kv => kv.Key.Signal, StringComparer.Ordinal)
                .Select(kv => new SignalSeries(kv.Key.Entity, kv.Key.Signal,
                    kv.Value.Select(p => new object?[] { p.T, p.Value, p.State }).ToList()))
                .ToList(),
            slice.Annotations,
            snapshot.Runs,
            snapshot.Disagreements);
    }

    /// <summary>
    /// The SSE delta after a refresh: added points, runs that are new or changed, disagreements only when changed.
    /// Null when the refresh changed nothing a client shows.
    /// </summary>
    public static TimelineResponse? Delta(TimelineSnapshot previous, TimelineSnapshot next, TimelineData added, long fallbackCursor)
    {
        var before = previous.Runs.ToDictionary(r => r.RunId, StringComparer.Ordinal);
        var runs = next.Runs.Where(r => !before.TryGetValue(r.RunId, out var old) || !SameRun(old, r)).ToList();
        var disagreementsChanged = !previous.Disagreements.SequenceEqual(next.Disagreements, IntervalComparer.Instance);
        var entitiesChanged = !previous.Data.Entities.SequenceEqual(next.Data.Entities);
        if (!added.HasPoints && runs.Count == 0 && !disagreementsChanged && !entitiesChanged)
            return null;
        return Response(next, added, fallbackCursor) with
        {
            Runs = runs.Count > 0 ? runs : null,
            Disagreements = disagreementsChanged ? next.Disagreements : null,
        };
    }

    private static bool SameRun(RunOnset a, RunOnset b) => a with { Nodes = b.Nodes } == b && a.Nodes.SequenceEqual(b.Nodes);

    private sealed class IntervalComparer : IEqualityComparer<long[]>
    {
        public static readonly IntervalComparer Instance = new();

        public bool Equals(long[]? x, long[]? y) => x is not null && y is not null && x.AsSpan().SequenceEqual(y);

        public int GetHashCode(long[] obj) => obj.Length > 0 ? obj[0].GetHashCode() : 0;
    }

    private static (IReadOnlyList<T> Merged, IReadOnlyList<T> Added) MergeByKey<T, TKey>(
        IReadOnlyList<T> existing, IEnumerable<T> incoming, Func<T, TKey> key, Func<T, long> time) where TKey : notnull
    {
        var byKey = new Dictionary<TKey, T>();
        foreach (var item in existing)
            byKey[key(item)] = item;
        var added = new Dictionary<TKey, T>();
        foreach (var item in incoming)
        {
            var k = key(item);
            if (byKey.TryGetValue(k, out var old) && EqualityComparer<T>.Default.Equals(old, item))
                continue;
            byKey[k] = item;
            added[k] = item;
        }
        return (byKey.Values.OrderBy(time).ToList(), added.Values.OrderBy(time).ToList());
    }

    private static long? First(IEnumerable<StatePoint> points, Func<StatePoint, bool> match) =>
        points.Where(match).Select(p => (long?)p.T).FirstOrDefault();

    private static long? SpreadSec(IEnumerable<long?> times)
    {
        var reached = times.OfType<long>().ToList();
        return reached.Count < 2 ? null : (reached.Max() - reached.Min()) / 1000;
    }

    private static int KindRank(string kind) => kind switch { Root => 0, Flow => 1, _ => 2 };
}
