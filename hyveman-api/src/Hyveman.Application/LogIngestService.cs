using System.Text.Json;
using Hyveman.Domain;
using Hyveman.Protocol;
using Microsoft.Extensions.Logging;

namespace Hyveman.Application;

/// <summary>POST /ingest/logs (PROTOCOL §6). Idempotent batch insert; a
/// malformed item is rejected per-item, never whole-batch (except the
/// documented 400/413 cases handled by the endpoint).</summary>
public sealed class LogIngestService(
    IEventStore events,
    IAlertEvaluator evaluator,
    LogonStatsService logonStats,
    ILogger<LogIngestService> log)
{
    /// <summary>Validates and inserts one batch. The caller has already
    /// authenticated the token; sourceKind drives severity semantics
    /// (PROTOCOL §10). Items are raw JSON elements so a malformed item is
    /// rejected per-item ("schema") instead of failing the batch at
    /// deserialization (PROTOCOL §6.2/§6.4).</summary>
    public async Task<IngestResult> IngestAsync(string sourceId, string sourceKind,
        IReadOnlyList<JsonElement> items, CancellationToken ct)
    {
        if (items.Count > ProtocolValidation.MaxItemsPerBatch)
            throw new ProtocolException(ProtocolErrorCodes.TooManyItems, 400,
                $"batch has {items.Count} items; maximum is {ProtocolValidation.MaxItemsPerBatch}");
        if (items.Count == 0)
            throw new ProtocolException(ProtocolErrorCodes.InvalidRequest, 400, "items must not be empty");

        // items are homogeneous: only kind:"log" (PROTOCOL §6.1). A well-formed
        // item of another kind is a whole-batch error; an item with a missing
        // or non-string kind is malformed and rejected per-item below.
        foreach (var item in items)
        {
            if (item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String &&
                kind.GetString() != "log")
                throw new ProtocolException(ProtocolErrorCodes.InvalidRequest, 400, "wrong_item_kind: /ingest/logs accepts only kind:\"log\"");
        }

        var valid = new List<ValidatedLogItem>(items.Count);
        var rejected = new List<ItemRejection>(items.Count);
        foreach (var item in items)
        {
            var (row, rejection) = ProtocolValidation.ParseLogItem(item, sourceKind);
            if (rejection is not null)
                rejected.Add(rejection);
            else if (row is not null)
                valid.Add(row);
        }

        if (valid.Count == 0)
            return new IngestResult(0, 0, rejected, []);

        var result = await events.InsertBatchAsync(sourceId, valid, ct);
        // Accepted rows only: deduped items are not new events. The store returns
        // the exact accepted subset — dedup can hit any position when a partially
        // committed batch is retried (PROTOCOL §6.6), so it cannot be reconstructed
        // from the Accepted count.
        var acceptedItems = result.AcceptedItems;
        if (acceptedItems.Count > 0)
        {
            try
            {
                await evaluator.OnEventsAcceptedAsync(sourceId, acceptedItems, ct);
            }
            catch (Exception ex)
            {
                // Alert evaluation must never break ingest durability; the
                // reconciliation pass repairs state after a crash (API.md §9.3).
                log.LogError(ex, "Event rule evaluation failed for source {sourceId}; will reconcile later", sourceId);
            }

            try
            {
                // Per-user/per-day security-logon aggregates (DESIGN §4.1/§13 #5).
                // Accepted rows only: deduped replays must not double-count.
                await logonStats.RecordAcceptedAsync(sourceId, acceptedItems, ct);
            }
            catch (Exception ex)
            {
                // Stats are derived data; an aggregation failure must not reject
                // the already-committed batch (API.md §13 failure behavior).
                log.LogError(ex, "Logon stats aggregation failed for source {sourceId}", sourceId);
            }
        }

        if (result.Rejected.Count > 0 || rejected.Count > 0)
            log.LogInformation("Log batch for {sourceId}: {accepted} accepted, {deduped} deduped, {rejected} rejected",
                sourceId, result.Accepted, result.Deduped, result.Rejected.Count + rejected.Count);

        return result with { Rejected = [.. result.Rejected, .. rejected] };
    }
}

/// <summary>Whole-batch protocol failure (maps to a protocol error envelope).</summary>
public sealed class ProtocolException(string code, int status, string message) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

internal static class ProtocolErrorCodes
{
    public const string TooManyItems = "too_many_items";
    public const string InvalidRequest = "invalid_request";
}
