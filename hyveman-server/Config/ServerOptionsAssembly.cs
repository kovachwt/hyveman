using System.Reflection;

namespace Hyveman.Server.Config;

public static class ServerOptionsAssembly
{
    public static readonly string Version =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
}
