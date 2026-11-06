using System.Collections.Concurrent;
using System.Threading.Channels;
using StressManager.Actions;
using StressManager.Calculations;

namespace StressManager.Services;

/// <summary>The one in-memory copy of the health model history (T2). Readers get slices, subscribers get refresh deltas.</summary>
public sealed class TimelineCache(TimeProvider clock)
{
    private TimelineSnapshot snapshot = TimelineSnapshot.Empty;
    private readonly ConcurrentDictionary<Channel<TimelineResponse>, byte> subscribers = new();

    public TimelineSnapshot Snapshot => Volatile.Read(ref snapshot);

    public TimelineResponse Read(long? since)
    {
        var current = Snapshot;
        return Timeline.Response(current, Timeline.Since(current.Data, since), since ?? RetentionFloor());
    }

    /// <summary>Swaps in the refreshed snapshot and pushes a delta to subscribers when anything visible changed.</summary>
    public void Publish(TimelineSnapshot next, TimelineData added)
    {
        var previous = Snapshot;
        Volatile.Write(ref snapshot, next);
        if (Timeline.Delta(previous, next, added, RetentionFloor()) is not { } delta)
            return;
        foreach (var channel in subscribers.Keys)
            channel.Writer.TryWrite(delta);
    }

    public Subscription Subscribe()
    {
        var channel = Channel.CreateUnbounded<TimelineResponse>(new UnboundedChannelOptions { SingleReader = true });
        subscribers[channel] = 0;
        return new Subscription(channel.Reader, () => subscribers.TryRemove(channel, out _));
    }

    private long RetentionFloor() => (clock.GetUtcNow() - Timeline.Retention).ToUnixTimeMilliseconds();

    public sealed class Subscription(ChannelReader<TimelineResponse> reader, Action unsubscribe) : IDisposable
    {
        public ChannelReader<TimelineResponse> Reader => reader;

        public void Dispose() => unsubscribe();
    }
}

/// <summary>
/// Incremental refresh every 30 s (T3); raw CPU from the Metrics API on every second pass (60 s).
/// Never stops the host: failures are logged and backed off up to 5 minutes.
/// </summary>
public sealed class TimelineRefresher(
    HealthHistoryReader reader, RawCpuReader rawCpu, TimelineCache cache, TimeProvider clock, ILogger<TimelineRefresher> log)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RawCpuInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private DateTimeOffset? rawCpuReadAt;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!reader.Enabled)
        {
            log.LogWarning("timeline: health model disabled, no history is read");
            return;
        }

        var delay = Interval;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(ct);
                delay = Interval;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                delay = delay * 2 < MaxBackoff ? delay * 2 : MaxBackoff;
                log.LogError(ex, "timeline: refresh failed, next try in {Delay}", delay);
            }

            try
            {
                await Task.Delay(delay, clock, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var cached = cache.Snapshot.Data;
        var (entities, relationships) = await reader.ReadModelAsync(ct);

        var reads = await Task.WhenAll(entities.Select(async entity =>
        {
            var states = reader.ReadStatesAsync(entity.Name, Timeline.FetchFrom(Timeline.LastStateAt(cached, entity.Name), now), ct);
            var annotations = reader.ReadAnnotationsAsync(entity.Name, Timeline.FetchFrom(Timeline.LastAnnotationAt(cached, entity.Name), now), ct);
            var signals = entity.Signals.Select(async signal =>
            {
                var key = new SeriesKey(entity.Name, signal);
                return (Key: key, Points: await reader.ReadSignalAsync(entity.Name, signal, Timeline.FetchFrom(Timeline.LastSignalAt(cached, key), now), ct));
            }).ToList();
            return (entity.Name, States: await states, Annotations: await annotations, Signals: await Task.WhenAll(signals));
        }));

        var incoming = new TimelineData(
            Timeline.Entities(entities, relationships),
            reads.ToDictionary(r => r.Name, r => r.States, StringComparer.Ordinal),
            reads.SelectMany(r => r.Signals).ToDictionary(s => s.Key, s => s.Points),
            reads.SelectMany(r => r.Annotations).ToList())
        {
            RawCpu = await ReadRawCpuAsync(entities, cached, now, ct),
        };

        var (merged, added) = Timeline.Merge(cached, incoming);
        var nowMs = now.ToUnixTimeMilliseconds();
        var kept = Timeline.Prune(merged, nowMs - (long)Timeline.Retention.TotalMilliseconds);
        cache.Publish(Timeline.Analyse(kept, nowMs), added);

        log.LogDebug("timeline: refreshed {Entities} entities, added {States} state, {Signals} signal, {RawCpu} raw CPU, {Annotations} annotation points",
            incoming.Entities.Count, added.States.Values.Sum(s => s.Count), added.Signals.Values.Sum(s => s.Count),
            added.RawCpu.Values.Sum(s => s.Count), added.Annotations.Count);
    }

    /// <summary>Empty when not due yet or when the Metrics API fails; a failure is logged and retried on the next pass.</summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<RawCpuPoint>>> ReadRawCpuAsync(
        IReadOnlyList<ModelEntity> entities, TimelineData cached, DateTimeOffset now, CancellationToken ct)
    {
        var none = new Dictionary<string, IReadOnlyList<RawCpuPoint>>();
        if (!rawCpu.Enabled || (rawCpuReadAt is { } last && now - last < RawCpuInterval - TimeSpan.FromSeconds(1)))
            return none;
        try
        {
            var reads = await Task.WhenAll(entities.Where(e => e.AzureResourceId is not null).Select(async e =>
                (e.Name, Points: await rawCpu.ReadAsync(e.AzureResourceId!,
                    Timeline.FetchFrom(Timeline.LastRawCpuAt(cached, e.Name), now, Timeline.RawCpuOverlap), now, ct))));
            rawCpuReadAt = now;
            return reads.ToDictionary(r => r.Name, r => r.Points, StringComparer.Ordinal);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "timeline: raw CPU read from the Metrics API failed, retrying on the next pass");
            return none;
        }
    }
}
