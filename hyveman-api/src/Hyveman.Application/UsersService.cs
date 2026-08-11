using Hyveman.Contracts;
using Hyveman.Domain;

namespace Hyveman.Application;

/// <summary>User administration + invitations (docs/MULTI-USER.md). All users
/// have equal permissions for now; every mutation is audited with the acting
/// user's real name.</summary>
public sealed class UsersService(
    IUserStore users,
    IPasskeyStore passkeys,
    ISessionStore sessions,
    IInvitationStore invitations,
    IAuditStore audit,
    IClock clock)
{
    public async Task<List<UserDto>> ListAsync(CancellationToken ct)
    {
        var all = await users.ListAsync(ct);
        var passkeyRows = await passkeys.ListAsync(ct);
        var result = new List<UserDto>();
        foreach (var u in all)
        {
            var owned = passkeyRows.Where(p => p.UserId == u.Id).ToList();
            result.Add(new UserDto
            {
                Id = u.Id,
                Name = u.Name,
                DisplayName = u.DisplayName,
                Disabled = u.Disabled,
                Created = u.Created,
                CreatedBy = u.CreatedBy,
                PasskeyCount = owned.Count,
                LastActive = owned.Max(p => p.LastUsed),
            });
        }
        return result;
    }

    public async Task<UserDetailDto?> GetAsync(string id, CancellationToken ct)
    {
        var u = await users.GetAsync(id, ct);
        if (u is null) return null;
        var owned = await passkeys.ListByUserAsync(id, ct);
        return new UserDetailDto
        {
            Id = u.Id,
            Name = u.Name,
            DisplayName = u.DisplayName,
            Disabled = u.Disabled,
            Created = u.Created,
            CreatedBy = u.CreatedBy,
            PasskeyCount = owned.Count,
            LastActive = owned.Max(p => p.LastUsed),
            Passkeys = owned.Select(p => new PasskeyDto
            {
                Id = p.Id, Name = p.Name, Created = p.Created, LastUsed = p.LastUsed,
            }).ToList(),
        };
    }

    public async Task DisableAsync(string id, string actorUserId, CancellationToken ct)
    {
        var user = await users.GetAsync(id, ct) ?? throw new NotFoundException($"user '{id}' not found");
        if (user.Id == actorUserId)
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["id"] = ["You cannot disable your own account."],
            });
        if (!user.Disabled && await users.CountEnabledAsync(ct) <= 1)
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["id"] = ["Cannot disable the last enabled user; another enabled user must remain."],
            });
        await users.SetDisabledAsync(id, true, ct);
        await sessions.RevokeAllForUserAsync(id, ct);
        await audit.RecordAsync(await ActorNameAsync(actorUserId, ct), "user.disabled", "user", id,
            null, clock.UtcNow, ct);
    }

    public async Task EnableAsync(string id, string actorUserId, CancellationToken ct)
    {
        if (await users.GetAsync(id, ct) is null)
            throw new NotFoundException($"user '{id}' not found");
        await users.SetDisabledAsync(id, false, ct);
        await audit.RecordAsync(await ActorNameAsync(actorUserId, ct), "user.enabled", "user", id,
            null, clock.UtcNow, ct);
    }

    public async Task DeleteAsync(string id, bool confirm, string actorUserId, CancellationToken ct)
    {
        if (!confirm)
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["confirm"] = ["User deletion requires confirm=true."],
            });
        var user = await users.GetAsync(id, ct) ?? throw new NotFoundException($"user '{id}' not found");
        if (user.Id == actorUserId)
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["id"] = ["You cannot delete your own account."],
            });
        if (!user.Disabled && await users.CountEnabledAsync(ct) <= 1)
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["id"] = ["Cannot delete the last enabled user; another enabled user must remain."],
            });
        await users.DeleteAsync(id, ct); // passkeys + sessions cascade (FK)
        await audit.RecordAsync(await ActorNameAsync(actorUserId, ct), "user.deleted", "user", id,
            $"{{\"name\":\"{user.Name}\"}}", clock.UtcNow, ct);
    }

    /// <summary>Removes a passkey. Any user may remove any passkey (equal
    /// permissions), but removal must never leave zero enabled login paths,
    /// and nobody may strip their own last passkey (self-lockout).</summary>
    public async Task RemovePasskeyAsync(string passkeyId, string actorUserId, CancellationToken ct)
    {
        var passkey = await passkeys.GetAsync(passkeyId, ct)
            ?? throw new NotFoundException($"passkey '{passkeyId}' not found");
        if (await users.GetAsync(passkey.UserId, ct) is null)
            throw new NotFoundException($"passkey '{passkeyId}' not found");

        // Self-lockout: nobody may strip their own last passkey.
        if (passkey.UserId == actorUserId && await passkeys.CountByUserAsync(passkey.UserId, ct) <= 1)
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["id"] = ["Cannot remove your final passkey; register another first."],
            });

        // Fleet lockout: never leave zero enabled login paths.
        var enabledIds = (await users.ListAsync(ct)).Where(u => !u.Disabled).Select(u => u.Id).ToHashSet();
        var loginPaths = (await passkeys.ListAsync(ct)).Where(p => enabledIds.Contains(p.UserId)).ToList();
        if (loginPaths.Count == 1 && loginPaths[0].Id == passkey.Id)
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["id"] = ["Cannot remove the final passkey of the last enabled user; register another first (console reset is the only other path)."],
            });

        await passkeys.RemoveAsync(passkeyId, ct);
        await audit.RecordAsync(await ActorNameAsync(actorUserId, ct), "passkey.removed", "passkey",
            passkeyId, null, clock.UtcNow, ct);
    }

    // ── invitations ────────────────────────────────────────────────────────

    public async Task<InvitationCreatedDto> CreateInvitationAsync(int? expiresInMinutes,
        string actorUserId, CancellationToken ct)
    {
        if (expiresInMinutes is { } m && (m < 5 || m > 60 * 24 * 7))
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["expiresInMinutes"] = ["Invitation lifetime must be between 5 minutes and 7 days."],
            });
        var now = clock.UtcNow;
        var lifetime = expiresInMinutes is { } l ? TimeSpan.FromMinutes(l) : TimeSpan.FromDays(7);
        var (id, raw) = await invitations.CreateAsync(actorUserId, null, lifetime, now, ct);
        await audit.RecordAsync(await ActorNameAsync(actorUserId, ct), "invitation.created",
            "invitation", id, $"{{\"lifetime_minutes\":{(int)lifetime.TotalMinutes}}}", now, ct);
        return new InvitationCreatedDto
        {
            Id = id,
            Token = raw,
            Created = now,
            ExpiresAt = now.Add(lifetime),
        };
    }

    public async Task<List<InvitationDto>> ListInvitationsAsync(CancellationToken ct)
    {
        var list = await invitations.ListAsync(ct);
        var usersById = (await users.ListAsync(ct)).ToDictionary(u => u.Id);
        return list.Select(i => new InvitationDto
        {
            Id = i.Id,
            CreatedBy = i.CreatedBy,
            CreatedByDisplayName = i.CreatedBy is not null && usersById.TryGetValue(i.CreatedBy, out var u)
                ? u.Name : null,
            Created = i.Created,
            ExpiresAt = i.ExpiresAt,
            ConsumedAt = i.ConsumedAt,
            Revoked = i.Revoked,
        }).ToList();
    }

    public async Task RevokeInvitationAsync(string id, string actorUserId, CancellationToken ct)
    {
        if (!await invitations.RevokeAsync(id, ct))
            throw new NotFoundException($"invitation '{id}' not found");
        await audit.RecordAsync(await ActorNameAsync(actorUserId, ct), "invitation.revoked",
            "invitation", id, null, clock.UtcNow, ct);
    }

    /// <summary>Inspect an invite token without consuming it (accept-invite
    /// landing page). Returns null when the token is unknown; Valid is false
    /// when it is known but consumed/revoked/expired.</summary>
    public async Task<InviteInspectResponse?> InspectInvitationAsync(string token, CancellationToken ct)
    {
        var invite = await invitations.LookupAsync(token, ct);
        if (invite is null) return null;
        var valid = !invite.Revoked && invite.ConsumedAt is null &&
            (invite.ExpiresAt is null || invite.ExpiresAt > clock.UtcNow);
        var createdBy = invite.CreatedBy is not null
            ? (await users.GetAsync(invite.CreatedBy, ct))?.Name : null;
        return new InviteInspectResponse
        {
            Valid = valid,
            ExpiresAt = invite.ExpiresAt,
            CreatedBy = createdBy,
        };
    }

    private async Task<string> ActorNameAsync(string userId, CancellationToken ct)
        => (await users.GetAsync(userId, ct))?.Name ?? userId;
}
