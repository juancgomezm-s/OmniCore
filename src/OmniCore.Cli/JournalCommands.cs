namespace OmniCore.Cli;

/// <summary>CLI delgado: delega la composición del mantenimiento al Host.</summary>
public static class JournalCommands
{
    public static Task<int> VerifyJournal(string[] args, TextWriter? output = null) =>
        OmniCore.Host.JournalCommandHost.VerifyJournal(args, output);
}
