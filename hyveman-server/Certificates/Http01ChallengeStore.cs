using System.Collections.Concurrent;

namespace Hyveman.Server.Certificates;

/// <summary>
/// In-memory store of pending http-01 challenge responses
/// (<c>/.well-known/acme-challenge/&lt;token&gt;</c> → key authorization). Populated by
/// <see cref="AcmeCertificateManager"/> right before it asks Let's Encrypt to validate,
/// served by <see cref="AcmeHttpMiddleware"/>, and cleared as soon as the order finishes.
/// Tokens are only valid for the minutes an order is in flight — nothing is persisted.
/// </summary>
public sealed class Http01ChallengeStore
{
    private readonly ConcurrentDictionary<string, string> _tokens = new(StringComparer.Ordinal);

    public void Set(string token, string keyAuthorization) => _tokens[token] = keyAuthorization;

    public bool TryGet(string token, out string keyAuthorization) => _tokens.TryGetValue(token, out keyAuthorization!);

    public void Remove(string token) => _tokens.TryRemove(token, out _);

    public int Count => _tokens.Count;
}
