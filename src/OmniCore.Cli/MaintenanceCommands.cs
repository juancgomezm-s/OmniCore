namespace OmniCore.Cli;

using OmniCore.Client;

/// <summary>CLI delgado: delega la composición del mantenimiento al Host.</summary>
public static class MaintenanceCommands
{
    public static Task<int> SessionPurge(string[] args, TextWriter? output = null,
        Localization? selectedLocalization = null) =>
        OmniCore.Host.MaintenanceCommandHost.SessionPurge(args, output,
            text => Resolve(selectedLocalization, text));

    public static Task<int> Gc(string[] args, TextWriter? output = null,
        Localization? selectedLocalization = null) =>
        OmniCore.Host.MaintenanceCommandHost.Gc(args, output,
            text => Resolve(selectedLocalization, text));

    public static Task<int> AuditPurge(string[] args, TextWriter? output = null,
        Localization? selectedLocalization = null) =>
        OmniCore.Host.MaintenanceCommandHost.AuditPurge(args, output,
            text => Resolve(selectedLocalization, text));

    private static string Resolve(Localization? localization, OmniCore.Host.CommandOutputText text)
    {
        var loc = localization ?? new Localization(LocaleSelectionResolver.Resolve(
            null, Environment.GetEnvironmentVariable("OMNI_LOCALE"), null).Locale);
        return loc.Resolve(text.Key, text.Args);
    }
}
