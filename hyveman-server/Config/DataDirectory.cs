using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Hyveman.Server.Config;

/// <summary>
/// Resolves the data directory (§5.1): --data-dir CLI arg → HYVEMAN_DATA_DIR env →
/// ServerOptions.DataDir in config/server.json → %ProgramData%\Hyveman\server.
/// Bootstraps missing subdirs and (on first run) the server key K.
/// </summary>
public static class DataDirectory
{
    public const string DefaultDataDir = @"%ProgramData%\Hyveman\server";

    public static string Resolve(string? cliDataDir)
    {
        if (!string.IsNullOrWhiteSpace(cliDataDir)) return Environment.ExpandEnvironmentVariables(cliDataDir);
        var env = Environment.GetEnvironmentVariable("HYVEMAN_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(env)) return Environment.ExpandEnvironmentVariables(env);
        var configFile = ConfigFilePath(Environment.ExpandEnvironmentVariables(DefaultDataDir));
        if (File.Exists(configFile))
        {
            // Existing install → honor its configured DataDir.
            var opt = TryReadConfig(configFile);
            if (!string.IsNullOrWhiteSpace(opt?.DataDir)) return Environment.ExpandEnvironmentVariables(opt.DataDir);
        }
        return Environment.ExpandEnvironmentVariables(DefaultDataDir);
    }

    public static string ConfigFilePath(string dataDir) => Path.Combine(dataDir, "config", "server.json");
    public static string KeyFilePath(string dataDir) => Path.Combine(dataDir, "config", "key");
    public static string RpIdFilePath(string dataDir) => Path.Combine(dataDir, "config", "rp_id.txt");
    public static string DbPath(string dataDir) => Path.Combine(dataDir, "hyveman.db");

    private static ServerOptions? TryReadConfig(string path)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<ServerOptions>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Create data-dir skeleton with ACLs (SYSTEM + Administrators on Windows).</summary>
    public static void Bootstrap(string dataDir)
    {
        foreach (var sub in new[] { "", "config", "backup", "backup/daily", "backup/weekly", "backup/monthly", "logs", "state" })
        {
            var dir = sub.Length == 0 ? dataDir : Path.Combine(dataDir, sub);
            Directory.CreateDirectory(dir);
            RestrictAcl(dir);
        }
    }

#pragma warning disable CA1416   // guarded by OperatingSystem.IsWindows() inside
    public static void RestrictAcl(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var di = new DirectoryInfo(path);
            var sec = di.GetAccessControl();
            // Reset to explicit ACEs only.
            sec.SetAccessRuleProtection(true, false);
            var admin = new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
            var system = new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.LocalSystemSid, null);
            AddAce(sec, admin);
            AddAce(sec, system);
            // The running user stays able to use the dir (production runs as SYSTEM; this also
            // keeps non-elevated dev runs working). Everyone/Users are still excluded.
            AddAce(sec, System.Security.Principal.WindowsIdentity.GetCurrent().User!);
            di.SetAccessControl(sec);
        }
        catch
        {
            // Non-fatal: ACL hardening is best-effort (e.g. non-admin dev runs).
        }
    }

    private static void AddAce(System.Security.AccessControl.DirectorySecurity sec, System.Security.Principal.SecurityIdentifier sid)
        => sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            sid, System.Security.AccessControl.FileSystemRights.FullControl,
            System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));

    /// <summary>
    /// Generate (or keep) the 256-bit server key K (§5.2). Never written again once present.
    /// </summary>
    public static byte[] LoadOrCreateKey(string dataDir)
    {
        var keyFile = KeyFilePath(dataDir);
        if (File.Exists(keyFile))
        {
            var bytes = File.ReadAllBytes(keyFile);
            if (bytes.Length == 32) return bytes;
            throw new InvalidOperationException(
                $"Server key {keyFile} has invalid length {bytes.Length} (expected 32). Refusing to overwrite; restore the key from backup or delete the file (which loses all vault secrets).");
        }
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(keyFile, key);
        RestrictAclFile(keyFile);
        return key;
    }

    public static void RestrictAclFile(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var fi = new FileInfo(path);
            var sec = fi.GetAccessControl();
            sec.SetAccessRuleProtection(true, false);
            var admin = new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
            var system = new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.LocalSystemSid, null);
            foreach (var sid in new[] { admin, system, System.Security.Principal.WindowsIdentity.GetCurrent().User! })
            {
                sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    sid, System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.InheritanceFlags.None,
                    System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
            }
            fi.SetAccessControl(sec);
        }
        catch
        {
        }
    }

    /// <summary>Load the WebAuthn RP ID from config/rp_id.txt; generate a placeholder if absent (must be set before passkey use).</summary>
    public static string LoadRpId(string dataDir)
    {
        var path = RpIdFilePath(dataDir);
        if (File.Exists(path))
        {
            var v = File.ReadAllText(path).Trim();
            if (v.Length > 0) return v;
        }
        // Hostname fallback for bootstrap; setup wizard warns the operator to pin it.
        var host = Dns.GetHostName().ToLowerInvariant();
        File.WriteAllText(path, host);
        RestrictAclFile(path);
        return host;
    }

    public static string ResolveTrustedNetworkLocal()
    {
        // Best-effort: report the machine's own non-loopback IPv4s (used by the setup wizard's
        // "trusted network" check in addition to loopback).
        var ips = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                        ips.Add(ua.Address.ToString());
            }
        }
        catch { }
        return string.Join(",", ips);
    }
}
