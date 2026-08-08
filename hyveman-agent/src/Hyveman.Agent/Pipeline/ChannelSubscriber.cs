using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Hyveman.Agent.Options;
using ChannelOptions = Hyveman.Agent.Options.ChannelOptions;
using Hyveman.Agent.Wevtapi;
using Hyveman.Agent.Wevtapi.Native;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hyveman.Agent.Pipeline;

/// <summary>
/// One EvtSubscribe push subscription per configured channel (AGENT.md §5.1,
/// §6). The ETW callback renders + enqueues and NEVER blocks (§6.3); reset
/// handling runs on a dedicated control loop so wevtapi is never re-entered
/// from the callback thread.
/// </summary>
public sealed class ChannelSubscriber : BackgroundService
{
    private readonly ChannelOptions _config;
    private readonly OptionsSnapshot _snapshot;
    private readonly BoundedQueue<EvtLogEvent> _queue;
    private readonly RuntimeMonitor _monitor;
    private readonly BookmarkManager _bookmarks;
    private readonly EpochManager _epochs;
    private readonly ILogger<ChannelSubscriber> _log;

    private readonly Channel<ResetRequest> _control = System.Threading.Channels.Channel.CreateUnbounded<ResetRequest>();

    private CancellationToken _stoppingToken;
    private WevtApiNative.EvtSubscribeCallback? _callback; // keep-alive: native keeps this delegate
    private IntPtr _subscription;
    private IntPtr _bookmarkHandle;
    private volatile bool _closed;
    private long _maxRecordIdSeen;
    private int _epoch;
    private bool _skippedPermanently;
    private string _actualChannel;

    public ChannelSubscriber(
        ChannelOptions config,
        OptionsSnapshot snapshot,
        BoundedQueue<EvtLogEvent> queue,
        RuntimeMonitor monitor,
        BookmarkManager bookmarks,
        EpochManager epochs,
        ILogger<ChannelSubscriber> log)
    {
        _config = config;
        _snapshot = snapshot;
        _queue = queue;
        _monitor = monitor;
        _bookmarks = bookmarks;
        _epochs = epochs;
        _log = log;
        _actualChannel = config.Channel ?? config.Name;
    }

    public string ChannelName => _config.Name;
    public string ActualChannel => _actualChannel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        _log.LogInformation("Subscriber loop starting for {channel} (actual {actual})", ChannelName, _actualChannel);
        var bookmark = _bookmarks.Load(ChannelName);
        _epoch = _epochs.Load(ChannelName);
        _maxRecordIdSeen = bookmark?.RecordId ?? 0;

        if (!TrySubscribe(bookmark?.BookmarkXml, out var error))
        {
            if (error == WevtApiNative.ErrorEvtChannelNotFound)
            {
                _log.LogWarning("Channel {channel} does not exist on this host; skipping (never crashes startup)", _actualChannel);
                _skippedPermanently = true;
                return; // §6.2: configured-but-absent channels are skipped
            }

            if (bookmark is not null && (error == WevtApiNative.ErrorEvtQueryResultInvalidPosition || error == WevtApiNative.ErrorInvalidParameter))
            {
                // Stale/invalid bookmark ⇒ assume channel clear/wrap (AGENT §6.7, PROTOCOL §11.1).
                _log.LogWarning("Subscribe with bookmark failed for {channel} (win32 {err}); assuming channel clear/wrap — resetting epoch and resubscribing from now", _actualChannel, error);
                BumpEpochAndResubscribe();
            }
            else
            {
                _log.LogWarning("Subscribe failed for {channel} (win32 {err}); will retry", _actualChannel, error);
                await RetrySubscribeUntilSuccessAsync(stoppingToken, bookmark?.BookmarkXml).ConfigureAwait(false);
                return;
            }
        }

        _log.LogInformation("Subscribed to channel {channel} (query: {query})", _actualChannel, BuildQuery());

        // Control loop: reset requests + shutdown.
        await foreach (var req in _control.Reader.ReadAllAsync(stoppingToken))
        {
            if (req == ResetRequest.PerformReset)
                BumpEpochAndResubscribe();
            else if (req == ResetRequest.Shutdown)
                break;
        }
    }

    private bool TrySubscribe(string? bookmarkXml, out int errorCode)
    {
        CloseSubscription();

        // Per-subscriber bookmark handle: advanced per event in the callback,
        // rendered to XML for persistence after durable spool (AGENT §6.6).
        _bookmarkHandle = WevtApiNative.EvtCreateBookmark(bookmarkXml);
        if (_bookmarkHandle == IntPtr.Zero)
        {
            errorCode = Marshal.GetLastWin32Error();
            return false;
        }

        var flags = WevtApiNative.EvtSubscribeTolerateQueryErrors;
        var resume = !string.IsNullOrEmpty(bookmarkXml);
        flags |= resume
            ? WevtApiNative.EvtSubscribeStartAfterBookmark   // resume
            : WevtApiNative.EvtSubscribeToFutureEvents;      // first run (or post-reset): from now

        _callback = OnEvent; // keep the delegate alive for the subscription lifetime
        _subscription = WevtApiNative.EvtSubscribe(
            IntPtr.Zero, IntPtr.Zero, _actualChannel, BuildQuery(),
            resume ? _bookmarkHandle : IntPtr.Zero,   // bookmark only valid with StartAfterBookmark
            IntPtr.Zero, _callback, flags);

        if (_subscription == IntPtr.Zero)
        {
            errorCode = Marshal.GetLastWin32Error();
            _callback = null;
            WevtApiNative.EvtClose(_bookmarkHandle);
            _bookmarkHandle = IntPtr.Zero;
            return false;
        }

        errorCode = 0;
        return true;
    }

    private void CloseSubscription()
    {
        _closed = true;
        var handle = Interlocked.Exchange(ref _subscription, IntPtr.Zero);
        if (handle != IntPtr.Zero)
        {
            // Give in-flight callbacks a moment to return before closing (wevtapi
            // is not re-entrant-safe at EvtClose during an active callback).
            Thread.Sleep(150);
            WevtApiNative.EvtClose(handle);
        }
        if (_bookmarkHandle != IntPtr.Zero)
        {
            WevtApiNative.EvtClose(_bookmarkHandle);
            _bookmarkHandle = IntPtr.Zero;
        }
        _closed = false;
    }

    private uint OnEvent(uint action, IntPtr userContext, IntPtr eventHandle)
    {
        if (_closed || _skippedPermanently)
            return 0;

        if (action == WevtApiNative.EvtSubscribeActionError)
        {
            _log.LogWarning("EvtSubscribe delivered error for {channel}: {err}", _actualChannel, eventHandle.ToInt32());
            return 0;
        }

        if (action != WevtApiNative.EvtSubscribeActionDeliver || eventHandle == IntPtr.Zero)
            return 0;

        try
        {
            var ev = EventRenderer.Render(eventHandle, _actualChannel, _bookmarkHandle);
            if (ev is null)
                return 0;
            ev.DedupScope = _config.Name;
            ev.Epoch = _epoch;

            // Channel clear/wrap detection #2: RecordID regression (AGENT §6.7).
            var prevMax = Interlocked.Read(ref _maxRecordIdSeen);
            if (ev.RecordId > 0 && ev.RecordId < (ulong)prevMax)
            {
                _log.LogWarning("RecordID regression on {channel}: saw {id} after max {max}; channel clear — resetting epoch",
                    _actualChannel, ev.RecordId, prevMax);
                _control.Writer.TryWrite(ResetRequest.PerformReset);
                return 0;
            }
            if (ev.RecordId > (ulong)prevMax)
                Interlocked.Exchange(ref _maxRecordIdSeen, (long)ev.RecordId);

            if (!PassesInProcessFilter(ev))
                return 0;

            var dropped = _queue.TryAdd(ev);
            if (dropped > 0)
            {
                _monitor.AddEventsDropped(dropped);
                _monitor.SetDegraded("overrun");
            }
        }
        catch (Exception ex)
        {
            // The callback contract: never throw into wevtapi.
            _log.LogError(ex, "Unhandled error in EvtSubscribe callback for {channel}", _actualChannel);
        }

        return 0;
    }

    /// <summary>
    /// Source-side filtering beyond XPath (AGENT §6.2/§6.4): the curated
    /// Security 4624 LogonType post-filter. Cheap; logon volume is low.
    /// </summary>
    private bool PassesInProcessFilter(EvtLogEvent ev)
        => SecurityFilter.ShouldKeep(ev, _snapshot.Active.SecurityLog);

    /// <summary>Builds the XPath that pushes level + ID filtering into the API (AGENT §6.2).</summary>
    public string BuildQuery()
        => ChannelQueryBuilder.Build(_config, _snapshot.Active.SecurityLog, _actualChannel);

    private void BumpEpochAndResubscribe()
    {
        try
        {
            _epoch++;
            _epochs.Save(ChannelName, _epoch);
            _monitor.AddChannelResets(1);
            _monitor.SetDegraded("channel_reset");
            _maxRecordIdSeen = 0;

            if (!TrySubscribe(bookmarkXml: null, out var error))
            {
                _log.LogWarning("Resubscribe from now failed for {channel} (win32 {err}); will retry", _actualChannel, error);
                _ = RetrySubscribeUntilSuccessAsync(_stoppingToken, bookmarkXml: null);
                return;
            }

            _log.LogWarning("Channel {channel} reset: epoch now {epoch}, resubscribed from now", _actualChannel, _epoch);

            // Synthetic channel_reset event (idempotently keyed: e<epoch>:0).
            _queue.TryAdd(new EvtLogEvent
            {
                Channel = _actualChannel,
                DedupScope = _config.Name,
                RecordId = 0,
                Epoch = _epoch,
                TimeCreatedUtc = DateTime.UtcNow,
                Level = 3,
                EventId = 0,
                ProviderName = "HyvemanAgent",
                Computer = Environment.MachineName,
                Message = $"Channel '{_actualChannel}' was cleared or wrapped; subscription restarted from now (epoch {_epoch})",
                EventData = new Dictionary<string, string?> { ["channel"] = _actualChannel, ["epoch"] = _epoch.ToString(CultureInfo.InvariantCulture) }
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Channel reset handling failed for {channel}", _actualChannel);
        }
    }

    private async Task RetrySubscribeUntilSuccessAsync(CancellationToken ct, string? bookmarkXml)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            attempt++;
            await Task.Delay(Backoff.DelayFor(attempt), ct).ConfigureAwait(false);
            if (TrySubscribe(bookmarkXml, out var error))
            {
                _log.LogInformation("Resubscribed to {channel} after retry", _actualChannel);
                return;
            }
            if (error == WevtApiNative.ErrorEvtChannelNotFound)
            {
                _log.LogWarning("Channel {channel} does not exist on this host; skipping", _actualChannel);
                _skippedPermanently = true;
                return;
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _control.Writer.TryWrite(ResetRequest.Shutdown);
        try { await ExecuteTask!.ConfigureAwait(false); } catch (OperationCanceledException) { }
        CloseSubscription();
        await base.StopAsync(cancellationToken);
    }
}

internal enum ResetRequest
{
    PerformReset,
    Shutdown
}

/// <summary>Exponential backoff per PROTOCOL §14: base 1 s, factor 2, cap 60 s, ±20% jitter.</summary>
public static class Backoff
{
    public static TimeSpan DelayFor(int attempt)
    {
        var raw = Math.Min(60.0, Math.Pow(2, Math.Max(0, attempt - 1))); // 1,2,4,... capped at 60
        var jittered = raw * (0.8 + 0.4 * Random.Shared.NextDouble());
        return TimeSpan.FromMilliseconds(jittered * 1000);
    }
}
