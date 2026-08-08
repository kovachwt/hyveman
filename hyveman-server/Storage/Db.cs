namespace Hyveman.Server.Storage;

/// <summary>
/// Facade over the SQLite factory/writer and all repositories — the single storage entry
/// point used by ingest, poller, alerts, UI, and maintenance services (§6.4).
/// </summary>
public sealed class Db
{
    public Db(SqliteFactory factory, SqliteWriter writer)
    {
        Factory = factory;
        Writer = writer;
        Sources = new Repos.SourceRepository(factory, writer);
        Tokens = new Repos.TokenRepository(factory);
        Events = new Repos.EventRepository(factory);
        Hosts = new Repos.HostRepository(factory);
        Components = new Repos.ComponentRepository(factory);
        Heartbeats = new Repos.HeartbeatRepository(factory);
        Alerts = new Repos.AlertRepository(factory);
        Channels = new Repos.ChannelRepository(factory);
        Credentials = new Repos.CredentialRepository(factory);
        Passkeys = new Repos.PasskeyRepository(factory);
        Audit = new Repos.AuditRepository(factory, writer);
    }

    public SqliteFactory Factory { get; }
    public SqliteWriter Writer { get; }
    public Repos.SourceRepository Sources { get; }
    public Repos.TokenRepository Tokens { get; }
    public Repos.EventRepository Events { get; }
    public Repos.HostRepository Hosts { get; }
    public Repos.ComponentRepository Components { get; }
    public Repos.HeartbeatRepository Heartbeats { get; }
    public Repos.AlertRepository Alerts { get; }
    public Repos.ChannelRepository Channels { get; }
    public Repos.CredentialRepository Credentials { get; }
    public Repos.PasskeyRepository Passkeys { get; }
    public Repos.AuditRepository Audit { get; }
}
