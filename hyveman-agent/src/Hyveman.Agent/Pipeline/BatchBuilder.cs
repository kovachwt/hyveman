using System.Collections.Concurrent;
using Hyveman.Agent.Net;
using Hyveman.Agent.Options;
using Hyveman.Agent.Wevtapi;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hyveman.Agent.Pipeline;

/// <summary>
/// Single consumer of the in-memory channel: forms batches bounded by
/// batch_max_events / batch_max_age_ms, serializes via the envelope builder
/// (splitting over max_batch_bytes), durably spools, THEN advances bookmarks
/// for the events that were durably spooled (AGENT.md §6.6 ordering rule).
/// </summary>
public sealed class BatchBuilder : BackgroundService
{
    private readonly BoundedQueue<EvtLogEvent> _queue;
    private readonly OptionsSnapshot _snapshot;
    private readonly SpoolWriter _spool;
    private readonly BookmarkManager _bookmarks;
    private readonly RuntimeMonitor _monitor;
    private readonly ILogger<BatchBuilder> _log;
    private readonly EnvelopeBuilder _envelope;

    private readonly object _flushSync = new();
    private bool _flushRequested;
    private readonly ConcurrentDictionary<string, long> _seqCache = new(StringComparer.Ordinal);

    public BatchBuilder(
        BoundedQueue<EvtLogEvent> queue,
        OptionsSnapshot snapshot,
        SpoolWriter spool,
        BookmarkManager bookmarks,
        RuntimeMonitor monitor,
        ILogger<BatchBuilder> log)
    {
        _queue = queue;
        _snapshot = snapshot;
        _spool = spool;
        _bookmarks = bookmarks;
        _monitor = monitor;
        _log = log;
        _envelope = new EnvelopeBuilder(snapshot.Active.Limits);
    }

    /// <summary>Signals the builder to flush a partial batch (used at shutdown).</summary>
    public void RequestFlush()
    {
        lock (_flushSync)
        {
            _flushRequested = true;
            Monitor.PulseAll(_flushSync);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("BatchBuilder loop starting");
        var batch = new List<EvtLogEvent>();
        var batchStart = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            var limits = _snapshot.Active.Limits;
            var maxAge = TimeSpan.FromMilliseconds(limits.BatchMaxAgeMs);
            var maxEvents = limits.BatchMaxEvents;

            // Async-first loop: the first await (Task.Delay) must yield quickly
            // so Host.StartAsync's sequential startup is never blocked by a
            // synchronous wait (a blocked startup thread stalls every service).
            if (_queue.TryTake(out var ev, TimeSpan.Zero) && ev is not null)
            {
                batch.Add(ev);
                if (batch.Count >= maxEvents)
                {
                    await FlushAsync(batch, stoppingToken).ConfigureAwait(false);
                    batch = new List<EvtLogEvent>();
                    batchStart = DateTime.UtcNow;
                }
                continue;
            }

            // Nothing in the queue: check whether the partial batch is due.
            bool flush;
            lock (_flushSync)
            {
                flush = _flushRequested || (batch.Count > 0 && DateTime.UtcNow - batchStart >= maxAge);
                if (flush) _flushRequested = false;
            }

            if (flush && batch.Count > 0)
            {
                await FlushAsync(batch, stoppingToken).ConfigureAwait(false);
                batch = new List<EvtLogEvent>();
                batchStart = DateTime.UtcNow;
            }

            // Yield to the thread pool (batch age granularity ~100 ms ≪ 1 s default).
            await Task.Delay(TimeSpan.FromMilliseconds(100), stoppingToken).ConfigureAwait(false);
        }

        // Shutdown: drain in-flight events to a final batch (AGENT §17 step 2).
        var rest = _queue.Drain();
        if (rest.Count > 0 || batch.Count > 0)
        {
            batch.AddRange(rest);
            await FlushAsync(batch, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task FlushAsync(List<EvtLogEvent> batch, CancellationToken ct)
    {
        if (batch.Count == 0)
            return;

        var sourceId = _snapshot.Active.SourceId;
        var chunks = _envelope.BuildBatches(batch, sourceId);

        var spooledEvents = new List<EvtLogEvent>(batch.Count);
        foreach (var (json, events) in chunks)
        {
            var fileName = _spool.WriteBatch(json, events.Count);
            if (fileName is null)
            {
                _monitor.AddBatchesFailed(1);
                continue; // caps rejected; already counted as dropped
            }

            _monitor.AddBatchesSent(1);
            _monitor.AddEventsSent(events.Count);
            spooledEvents.AddRange(events);
        }

        // Ordering rule (§6.6): bookmark advance only after the events are
        // durably spooled — and only for events that made it into a spool file.
        foreach (var group in spooledEvents.GroupBy(ev => ev.DedupScope.Length > 0 ? ev.DedupScope : ev.Channel))
        {
            // Pick the last event that carries a bookmark position: synthetic
            // events (e.g. channel_reset) have no position and must not block
            // the advance (SPEC-DEVIATIONS P2-3).
            var last = BookmarkManager.LastPositionedEvent(group);
            if (last is null)
                continue;
            var seq = _seqCache.GetOrAdd(group.Key, ch => _bookmarks.Load(ch)?.Seq ?? 0) + 1;
            _seqCache[group.Key] = seq;
            _bookmarks.Advance(group.Key, last, seq);
        }
    }
}
