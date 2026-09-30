namespace OmniCore.Cli;

/// <summary>CLI delgado: delega la composición del mantenimiento al Host.</summary>
public static class MaintenanceCommands
{
    public static Task<int> SessionPurge(string[] args, TextWriter? output = null) =>
        OmniCore.Host.MaintenanceCommandHost.SessionPurge(args, output);

    public static Task<int> Gc(string[] args, TextWriter? output = null) =>
        OmniCore.Host.MaintenanceCommandHost.Gc(args, output);

    public static Task<int> AuditPurge(string[] args, TextWriter? output = null) =>
        OmniCore.Host.MaintenanceCommandHost.AuditPurge(args, output);
}
