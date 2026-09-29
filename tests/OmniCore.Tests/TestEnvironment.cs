using System.Runtime.CompilerServices;

namespace OmniCore.Tests;

/// <summary>
/// Aísla los tests de los datos reales del usuario: antes de cualquier test, los directorios de
/// datos y de configuración de la plataforma (<c>%LOCALAPPDATA%\OmniCore</c>, <c>%APPDATA%\OmniCore</c>)
/// se reubican en un temporal del proceso. Ningún test lee la configuración ni escribe el journal,
/// las credenciales o los blobs del usuario.
/// </summary>
internal static class TestEnvironment
{
    public static string DataDirectory { get; private set; } = "";

    [ModuleInitializer]
    internal static void Isolate()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-tests-" + Environment.ProcessId);
        DataDirectory = Path.Combine(root, "data");
        Directory.CreateDirectory(DataDirectory);
        Environment.SetEnvironmentVariable(OmniCore.Infrastructure.DefaultPlatformPaths.DataDirVariable, DataDirectory);
        Environment.SetEnvironmentVariable(OmniCore.Infrastructure.DefaultPlatformPaths.ConfigDirVariable,
            Path.Combine(root, "config"));
    }
}
