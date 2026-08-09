using Hyveman.Domain;
using Hyveman.Protocol;
using Microsoft.Extensions.Logging;

namespace Hyveman.Application;

/// <summary>POST /ingest/logs (PROTOCOL §6). Idempotent batch insert; a
/// malformed item is rejected per-item, never whole-batch (except the
/// documented 400/413 cases handled by the endpoint).</summary>
public sealed class LogIngestService(
    IEventStore events,
    IAgentStatusStore agentStatus,
    IAlertEvaluator evaluator,
    IClock clock,
    ILogger<LogIngestService> log)
{
    /// <summary>Validates and inserts one batch. The caller has already
    /// authenticated the token; sourceKind drives severity semantics
    /// (PROTOCOL §10).</summary>
    public async Task<IngestResult> IngestAsync(string sourceId, string sourceKind,
        IReadOnlyList<LogItemDto> items, CancellationToken ct)
    {
        if (items.Count > ProtocolValidation.MaxItemsPerBatch)
            throw new ProtocolException(ProtocolErrorCodes.TooManyItems, 400,
                $"batch has {items.Count} items; maximum is {ProtocolValidation.MaxItemsPerBatch}");

        // items are homogeneous: only kind:"log" (PROTOCOL §6.1).
        if (items.Any(i => i.Kind != "log"))
            throw new ProtocolException(ProtocolErrorCodes.InvalidRequest, 400, "wrong_item_kind: /ingest/logs accepts only kind:\"log\"");

        var valid = new List<ValidatedLogItem>(items.Count);
        var rejected = new List<ItemRejection>(items.Count);
        foreach (var item in items)
        {
            var (row, rejection) = ProtocolValidation.ValidateLogItem(item, sourceKind);
            if (rejection is not null)
                rejected.Add(rejection);
            else if (row is not null)
                valid.Add(row);
        }

        if (valid.Count == 0)
            return new IngestResult(0, 0, rejected);

        var result = await events.InsertBatchAsync(sourceId, valid, ct);
        // accepted rows only: deduped items are not new events.
        var acceptedItems = valid.Take(result.Accepted).ToList();
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
