using System.Runtime.CompilerServices;
using Hyveman.Server.Storage;

namespace Hyveman.Server.Tests;

/// <summary>
/// The server calls DapperConfig.Register() from Program.cs at startup; tests never run
/// Program.cs, so register the snake_case type maps before the first Dapper query.
/// </summary>
internal static class TestModuleInitializer
{
    [ModuleInitializer]
    public static void Init()
    {
        DapperConfig.Register();
    }
}
