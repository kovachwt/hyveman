using System.Collections.Concurrent;
using Hyveman.Agent.Net;
using Hyveman.Agent.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hyveman.Agent.Pipeline;

/// <summary>
/// Drains spool files oldest-first (AGENT.md §5.1, §6.5): one file = one
/// batch = one POST. 2xx → delete; non-retryable 4xx / permanent per-item
/// rejects → quarantine to state\quarantine\; 408/429/5xx/network → keep the
/// file and retry with exponential backoff + jitter (cap 60 s, honor
/// Retry-After); 400 too_many_items / 413 → split the file in half. Bounded
/// concurrency (send_concurrency); while the backend is down the sender
/// sleeps on the backoff — no retry storm, CPU ~0 (H5).
/// </summary>
public sealed class LogSender : BackgroundService
{
    private readonly string _spoolDir;
    private readonly string _quarantineDir;
    private readonly OptionsSnapshot _snapshot;
    private readonly BackendClient _client;
    private readonly RuntimeMonitor _monitor;
    private readonly ILogger<LogSender> _log;
    private readonly EnvelopeBuilder _envelope;

    // Slow retry for credential-class 4xx: the file is valid and kept in the
    // spool, but a bad credential won't self-heal quickly (P1-3).
    private const int AuthErrorRetrySeconds = 5 * 60;

    private sealed class PendingFile
    {
        public required string Path;
        public int Attempt;
        public DateTimeOffset RetryAt = DateTimeOffset.MinValue;
    }

    private readonly ConcurrentDictionary<string, PendingFile> _pending = new(StringComparer.OrdinalIgnoreCase);

    public LogSender(
        string spoolDir,
        string stateDir,
        OptionsSnapshot snapshot,
        BackendClient client,
        RuntimeMonitor monitor,
        ILogger<LogSender> log)
    {
        _spoolDir = spoolDir;
        _quarantineDir = Path.Combine(stateDir, "quarantine");
        _snapshot = snapshot;
        _client = client;
        _monitor = monitor;
        _log = log;
        _envelope = new EnvelopeBuilder(snapshot.Active.Limits);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("LogSender loop starting");
        Directory.CreateDirectory(_quarantineDir);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "LogSender drain loop failed; continuing");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task DrainOnceAsync(CancellationToken ct)
    {
        var concurrency = _snapshot.Active.Limits.SendConcurrency;
        var now = DateTimeOffset.UtcNow;

        // Files already being retried: add the ones whose retry time has come.
        var due = _pending.Values.Where(p => p.RetryAt <= now).Select(p => p.Path).ToList();
        foreach (var path in due)
            _pending.TryRemove(path, out _);

        var files = SpoolDirectory.OldestFirst(_spoolDir)
            .Where(f => !_pending.ContainsKey(f))
            .Take(Math.Max(0, concurrency - due.Count))
            .ToList();

        if (due.Count == 0 && files.Count == 0)
            return;

        var tasks = new List<Task>();
        foreach (var path in due)
            tasks.Add(SendFileAsync(path, ct));
        foreach (var path in files)
            tasks.Add(SendFileAsync(path, ct));

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task SendFileAsync(string path, CancellationToken ct)
    {
        byte[] body;
        try
        {
            body = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read spool file {file}; skipping this round", Path.GetFileName(path));
            return;
        }

        var gzip = _snapshot.Active.Limits.Gzip;
        var result = await _client.PostLogsAsync(body, gzip, ct).ConfigureAwait(false);

        switch (result.Outcome)
        {
            case SendOutcome.Accepted:
                var logs = result.Logs;
                if (logs is not null)
                {
                    _monitor.AddEventsSent(logs.Accepted + logs.Deduped);
                    if (logs.Deduped > 0)
                        _log.LogDebug("Batch {file}: {deduped} events deduped server-side", Path.GetFileName(path), logs.Deduped);
                }
                await DeleteFileAsync(path).ConfigureAwait(false);
                break;

            case SendOutcome.Quarantine:
                await QuarantineAsync(path).ConfigureAwait(false);
                break;

            case SendOutcome.Split:
                await SplitAndResendAsync(path, ct).ConfigureAwait(false);
                break;

            case SendOutcome.Retry:
                await ScheduleRetryAsync(path, result.RetryAfterSeconds).ConfigureAwait(false);
                break;

            case SendOutcome.CredentialsInvalid:
                await HandleCredentialsInvalidAsync(path).ConfigureAwait(false);
                break;
        }
    }

    private async Task ScheduleRetryAsync(string path, int? retryAfterSeconds)
    {
        _monitor.RecordSendError();
        _monitor.AddBatchesFailed(1);

        if (_pending.TryGetValue(path, out var existing))
        {
            existing.Attempt++;
        }
        else
        {
            _pending[path] = new PendingFile { Path = path, Attempt = 1 };
            existing = _pending[path];
        }

        var delay = retryAfterSeconds is > 0
            ? TimeSpan.FromSeconds(retryAfterSeconds.Value)
            : Backoff.DelayFor(existing.Attempt);

        existing.RetryAt = DateTimeOffset.UtcNow + delay;
        _log.LogDebug("Retrying {file} in {delay}s (attempt {attempt})", Path.GetFileName(path), delay.TotalSeconds, existing.Attempt);
    }

    private async Task QuarantineAsync(string path)
    {
        _monitor.RecordSendError();
        _monitor.AddBatchesFailed(1);
        _monitor.AddQuarantinedBatches(1);
        _monitor.SetDegraded("quarantined");
        _log.LogError("Quarantining non-retryable batch {file} (server refuses it; no infinite retry loop)", Path.GetFileName(path));
        try
        {
            Directory.CreateDirectory(_quarantineDir);
            var dest = Path.Combine(_quarantineDir, Path.GetFileName(path) + ".quarantined");
            File.Move(path, dest, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Quarantine move failed for {file}", Path.GetFileName(path));
        }
    }

    /// <summary>
    /// Credential-class 4xx (token_invalid/token_revoked/wrong_scope/
    /// unknown_source): the batch is valid — keep it in the spool, surface
    /// auth_rejected in the heartbeat, and retry slowly (5 min) so a rotated
    /// or re-registered token recovers the file automatically. Never
    /// quarantine a good batch for a bad credential (PROTOCOL §13.3).
    /// </summary>
    private async Task HandleCredentialsInvalidAsync(string path)
    {
        _monitor.RecordSendError();
        _monitor.AddBatchesFailed(1);
        _monitor.SetDegraded("auth_rejected");
        _log.LogError(
            "Backend rejected credentials for {file} (token invalid/revoked/out of scope, or source missing); spool file retained — re-register the agent with a fresh reg_ token",
            Path.GetFileName(path));
        await ScheduleRetryAsync(path, AuthErrorRetrySeconds).ConfigureAwait(false);
    }

    private async Task SplitAndResendAsync(string path, CancellationToken ct)
    {
        _monitor.RecordSendError();
        _log.LogWarning("Server asked to split batch {file}; splitting in half and resending", Path.GetFileName(path));

        byte[] body;
        try
        {
            body = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return;
        }

        var halves = _envelope.SplitInHalf(body);
        foreach (var (json, _) in halves)
        {
            var fileName = SpoolFiles.NewFileName();
            var tmp = Path.Combine(_spoolDir, fileName + ".tmp");
            var final = Path.Combine(_spoolDir, fileName);
            try
            {
                await File.WriteAllBytesAsync(tmp, json, ct).ConfigureAwait(false);
                using (var fs = new FileStream(tmp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    fs.Flush(flushToDisk: true);
                File.Move(tmp, final);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to write split batch {file}", fileName);
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
            }
        }

        await DeleteFileAsync(path).ConfigureAwait(false);
    }

    private async Task DeleteFileAsync(string path)
    {
        _pending.TryRemove(path, out _);
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to delete spool file {file} after send (will be re-sent; dedup collapses it)", Path.GetFileName(path));
        }
        await Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Bounded wait for in-flight uploads (AGENT §17 step 4), then exit;
        // unsent spool files stay on disk and are sent on next start.
        var grace = TimeSpan.FromSeconds(_snapshot.Active.Limits.ShutdownGraceS);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(grace);
        try { await ExecuteTask!.WaitAsync(cts.Token).ConfigureAwait(false); } catch (Exception) { }
        await base.StopAsync(cancellationToken);
    }
}
