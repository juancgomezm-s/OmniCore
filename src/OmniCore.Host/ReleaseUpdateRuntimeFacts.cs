namespace OmniCore.Host;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using OmniCore.Protocol;

/// <summary>The current configuration format generation; updates in this generation perform no data migration.</summary>
public static class ReleaseUpdateRuntimeFacts
{
    public const string ConfigurationContract = "1.0";

    public static ReleaseUpdateEnvironment Inspect()
    {
        string? nodeVersion = null;
        var node = new ReleaseInstallationInspector().Inspect().NodePath;
        if (node is not null)
        {
            var start = new ProcessStartInfo(node)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--version");
            start.Environment.Remove("NODE_OPTIONS");
            start.Environment.Remove("NODE_PATH");
            try
            {
                using var process = Process.Start(start);
                if (process is not null)
                {
                    if (!process.WaitForExit(2000)) { process.Kill(entireProcessTree: true); process.WaitForExit(2000); }
                    else if (process.ExitCode == 0)
                    {
                        var value = process.StandardOutput.ReadToEnd().Trim();
                        if (Regex.IsMatch(value, @"^v\d{1,3}\.\d{1,3}\.\d{1,3}$", RegexOptions.CultureInvariant)) nodeVersion = value[1..];
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        return new(RuntimeInformation.RuntimeIdentifier, Environment.Version.ToString(), nodeVersion,
            ProtocolVersions.V1, ConfigurationContract);
    }
}
