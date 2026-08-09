using System.Text.Json;

namespace Hyveman.Protocol;

/// <summary>Builds protocol response bodies. Every 2xx and error envelope carries
/// the server's current v and the reserved commands array (PROTOCOL §16).</summary>
public static class ProtocolEnvelope
{
    /// <summary>Serializes any protocol response DTO with the protocol options.</summary>
    public static string Serialize(object dto) => JsonSerializer.Serialize(dto, ProtocolJson.Options);

    public static ErrorEnvelope Error(string code, string? message = null, int[]? supported = null) => new()
    {
        V = ProtocolVersion.Current,
        Error = new ProtocolError { Code = code, Message = message ?? code, Supported = supported },
        Commands = [],
    };

    /// <summary>Version errors must use the server's current version and list
    /// error.supported (PROTOCOL §3); they must not echo the client version.</summary>
    public static ErrorEnvelope VersionError(string code, string message) =>
        Error(code, message, ProtocolVersion.Supported);
}
