using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Hyveman.Agent.Net;
using Hyveman.Agent.Options;
using Hyveman.Agent.Pipeline;
using Hyveman.Agent.Wevtapi;

namespace Hyveman.Agent.Telemetry;

/// <summary>
/// Periodic heartbeat (AGENT.md §8): every heartbeat.interval_s (default 30 s),
/// best-effort via TelemetrySender, never spooled.
/// </summary>
public sealed class HeartbeatTimer : BackgroundService
{
    private readonly OptionsSnapshot _snapshot;
    private readonly RuntimeMonitor _monitor;
    private readonly BoundedQueue<EvtLogEvent> _queue;
    private readonly TelemetrySender _sender;
    private readonly ILogger<HeartbeatTimer> _log;
    private readonly string _spoolDir;

    public HeartbeatTimer(
        OptionsSnapshot snapshot,
        RuntimeMonitor monitor,
        BoundedQueue<EvtLogEvent> queue,
        TelemetrySender sender,
        string spoolDir,
        ILogger<HeartbeatTimer> log)
    {
        _snapshot = snapshot;
        _monitor = monitor;
        _queue = queue;
        _sender = sender;
        _spoolDir = spoolDir;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Heartbeat loop starting");
        // First heartbeat shortly after start so the "agent silent" timer
        // gets reset quickly.
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromSeconds(_snapshot.Active.Heartbeat.IntervalS);
            try
            {
                var hb = HeartbeatFactory.Build(_snapshot, _monitor, _queue, _spoolDir);
                _log.LogDebug("Heartbeat: degraded={degraded} spool={spoolBytes}B/{spoolFiles}f queue={queue}",
                    hb.Degraded, hb.Counters.SpoolBytes, hb.Counters.SpoolFiles, hb.Counters.QueueDepth);
                await _sender.SendAsync(hb, _snapshot.Active.SourceId, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Heartbeat send failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
