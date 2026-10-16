namespace OmniCore.Host;

/// <summary>Recoverable turn outcomes shared by terminal adapters and the runtime.</summary>
public static class TurnExitCodes
{
    /// <summary>No input was admitted because another live Host owns the selected Session.</summary>
    public const int SessionBusy = 5;
}
