using Hyveman.Contracts;
using Hyveman.Domain;

namespace Hyveman.Application;

/// <summary>Source administration (API.md §7): source list with agent status
/// and token metadata; one-time registration tokens (raw value returned only
/// at creation); token revocation.</summary>
public sealed class SourcesService(
    ISourceStore sources,
    ITokenStore tokens,
    IRegistrationTokenStore regTokens,
    IAgentStatusStore agentStatus,
    IHostStore hosts,
    IClock clock,
    IAuditStore audit)
{
    public async Task<List<SourceDto>> ListAsync(CancellationToken ct)
    {
        var all = await sources.ListAsync(ct);
        var statuses = (await agentStatus.ListAllAsync(ct)).ToDictionary(s => s.SourceId);
        var hostBySource = (await hosts.ListAsync(ct)).Where(h => h.SourceId is not null)
            .ToDictionary(h => h.SourceId!, h => h.Id);
        var result = new List<SourceDto>();
        foreach (var s in all)
        {
            var tokenList = await tokens.ListForSourceAsync(s.Id, ct);
            result.Add(new SourceDto
            {
                Id = s.Id,
                Kind = s.Kind,
                Name = s.Name,
                CreatedAt = s.CreatedAt,
                HostId = hostBySource.GetValueOrDefault(s.Id),
                Agent = statuses.TryGetValue(s.Id, out var st) ? AgentStatusMapper.ToDto(st, clock.UtcNow) : null,
                Tokens = tokenList.Select(t => new TokenDto
                {
                    Id = t.Id, Prefix = t.Prefix, Scopes = t.Scopes, Created = t.Created,
                    LastUsed = t.LastUsed, Revoked = t.Revoked,
                }).ToList(),
            });
        }
        return result;
    }

    public async Task<RegistrationTokenCreatedDto> CreateRegistrationTokenAsync(string kind, int? lifetimeMinutes, string actor, CancellationToken ct)
    {
        if (!SourceKinds.IsKnown(kind))
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["kind"] = [$"kind must be one of: {string.Join(", ", SourceKinds.Known)}."],
            });
        if (lifetimeMinutes is { } lm && (lm < 5 || lm > 60 * 24 * 365))
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["lifetimeMinutes"] = ["Lifetime must be between 5 minutes and 365 days."],
            });
        var now = clock.UtcNow;
        var (id, raw) = await regTokens.CreateAsync(kind, lifetimeMinutes is { } l ? TimeSpan.FromMinutes(l) : null, actor, now, ct);
        await audit.RecordAsync(actor, "registration_token.created", "registration_token", id,
            $"{{\"kind\":\"{kind}\"}}", now, ct);
        return new RegistrationTokenCreatedDto
        {
            Id = id,
            Kind = kind,
            Token = raw,
            Created = now,
            ExpiresAt = lifetimeMinutes is { } l2 ? now.AddMinutes(l2) : null,
        };
    }

    public Task<IReadOnlyList<RegistrationTokenInfo>> ListRegistrationTokensAsync(CancellationToken ct)
        => regTokens.ListAsync(ct);

    public async Task RevokeRegistrationTokenAsync(string id, string actor, CancellationToken ct)
    {
        if (!await regTokens.RevokeAsync(id, ct))
            throw new NotFoundException($"registration token '{id}' not found");
        await audit.RecordAsync(actor, "registration_token.revoked", "registration_token", id, null, clock.UtcNow, ct);
    }

    public async Task RevokeTokenAsync(string sourceId, string tokenId, string actor, CancellationToken ct)
    {
        if (await sources.GetByIdAsync(sourceId, ct) is null)
            throw new NotFoundException($"source '{sourceId}' not found");
        if (!await tokens.RevokeAsync(tokenId, ct))
            throw new NotFoundException($"token '{tokenId}' not found");
        await audit.RecordAsync(actor, "token.revoked", "token", tokenId, null, clock.UtcNow, ct);
    }

    public async Task<List<RegistrationTokenDto>> ListRegistrationTokenDtosAsync(CancellationToken ct)
    {
        var list = await regTokens.ListAsync(ct);
        return list.Select(t => new RegistrationTokenDto
        {
            Id = t.Id, Kind = t.Kind, Created = t.Created, ExpiresAt = t.ExpiresAt,
            ConsumedAt = t.ConsumedAt, Revoked = t.Revoked,
        }).ToList();
    }
}
