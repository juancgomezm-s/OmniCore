namespace OmniCore.Cli;

using OmniCore.Client;

/// <summary>CLI delgado: delega la composición del mantenimiento al Host.</summary>
public static class JournalCommands
{
    public static Task<int> VerifyJournal(string[] args, TextWriter? output = null,
        Localization? selectedLocalization = null) =>
        OmniCore.Host.JournalCommandHost.VerifyJournal(args, output,
            text => (selectedLocalization ?? new Localization(LocaleSelectionResolver.Resolve(
                null, Environment.GetEnvironmentVariable("OMNI_LOCALE"), null).Locale))
                .Resolve(text.Key, text.Args));
}
