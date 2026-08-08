using Hyveman.Server.Auth;
using Hyveman.Server.Common;
using Hyveman.Server.Storage;
using Hyveman.Server.Storage.Repos;
using Microsoft.Data.Sqlite;

namespace Hyveman.Server.Ingest;

/// <summary>
/// POST /register (§7.5, PROTOCOL §5): validate the reg_ token, resolve/reuse the source
/// (reinstall-friendly with boot_id disambiguation), mint the agt_ token, consume the reg_
/// token — all in one transaction.
/// </summary>
public sealed class RegistrationService
{
    private readonly Db _db;
    private readonly ITokenService _tokens;

    public RegistrationService(Db db, ITokenService tokens)
    {
        _db = db;
        _tokens = tokens;
    }

    public sealed record RegisterResult(bool Ok, int Status, string? ErrorCode, string? ErrorMessage,
        string? SourceId, string? Token, List<string>? Scopes, string? IssuedAt);

    public async Task<RegisterResult> RegisterAsync(RegisterRequest req, string rawRegToken)
    {
        if (string.IsNullOrWhiteSpace(req.Kind) || string.IsNullOrWhiteSpace(req.Hostname))
            return new(false, 400, "invalid_request", "kind and hostname are required", null, null, null, null);
        if (req.Hostname.Length > 200)
            return new(false, 400, "invalid_request", "hostname too long", null, null, null, null);

        var res = await _tokens.ResolveAsync(rawRegToken);
        switch (res.Outcome)
        {
            case TokenResolveOutcome.Invalid: return new(false, 401, "token_invalid", "unknown or expired registration token", null, null, null, null);
            case TokenResolveOutcome.Revoked: return new(false, 401, "token_revoked", "registration token revoked", null, null, null, null);
            case TokenResolveOutcome.Consumed: return new(false, 410, "token_consumed", "registration token already used; reissue via admin UI", null, null, null, null);
            case TokenResolveOutcome.UnknownSource: return new(false, 401, "token_invalid", "registration token invalid", null, null, null, null);
        }

        // Scope + bound-kind checks.
        if (!HasScope(res.Token!, "register"))
            return new(false, 403, "wrong_scope", "token lacks register scope", null, null, null, null);
        if (res.Token!.BoundKind is not null && !string.Equals(res.Token.BoundKind, req.Kind, StringComparison.OrdinalIgnoreCase))
            return new(false, 400, "invalid_request", $"kind_mismatch: token bound to '{res.Token.BoundKind}', request is '{req.Kind}'", null, null, null, null);

        var now = WireTime.NowMs();
        var outcome = await _db.Writer.WithTransactionAsync(async conn =>
        {
            // Source resolution (PROTOCOL §5.2 step 2).
            var existing = await _db.Sources.FindByKindNameAsync(req.Kind!, req.Hostname);
            string sourceId;
            if (existing is null)
            {
                sourceId = Ulid.Prefixed("src_");
                await _db.Sources.InsertAsync(conn, sourceId, req.Kind!, req.Hostname, req.BootId);
            }
            else
            {
                if (!string.IsNullOrEmpty(existing.BootId) && !string.IsNullOrEmpty(req.BootId)
                    && !string.Equals(existing.BootId, req.BootId, StringComparison.Ordinal))
                {
                    // Same hostname+kind, different physical host → disambiguate (HOST01-2, HOST01-3, ...).
                    for (var n = 2; ; n++)
                    {
                        var candidate = $"{req.Hostname}-{n}";
                        if (await _db.Sources.FindByKindNameAsync(req.Kind!, candidate) is null)
                        {
                            sourceId = Ulid.Prefixed("src_");
                            await _db.Sources.InsertAsync(conn, sourceId, req.Kind!, candidate, req.BootId);
                            break;
                        }
                        if (n > 100)
                            return new RegisterResult(false, 409, "name_collision",
                                "hostname collision cannot be disambiguated (too many suffix attempts); operator action required",
                                null, null, null, null);
                    }
                }
                else
                {
                    sourceId = existing.Id;
                    if (existing.BootId is null && req.BootId is not null)
                        await _db.Sources.UpdateBootIdAsync(conn, existing.Id, req.BootId);
                }
            }

            // Mint agt_ token.
            var raw = TokenHasher.NewRawToken("agt_");
            var tokId = Ulid.Prefixed("tok_");
            await _db.Tokens.InsertAsync(conn, tokId, sourceId, TokenHasher.Hash(raw), "[\"ingest\"]");

            // Consume the reg_ token (single-use).
            await _db.Tokens.MarkConsumedAsync(conn, res.Token.Id);

            return new RegisterResult(true, 200, null, null, sourceId, raw, new List<string> { "ingest" }, now);
        });

        if (outcome.Ok)
        {
            await _db.Audit.WriteAsync($"agent: {outcome.SourceId}", "source.register", "sources", outcome.SourceId,
                $"{{\"hostname\":\"{req.Hostname}\",\"kind\":\"{req.Kind}\"}}");
        }
        return outcome;
    }

    private static bool HasScope(TokenRow token, string scope)
    {
        try
        {
            var scopes = System.Text.Json.JsonSerializer.Deserialize<List<string>>(token.Scopes);
            return scopes?.Contains(scope) == true;
        }
        catch
        {
            return false;
        }
    }
}
