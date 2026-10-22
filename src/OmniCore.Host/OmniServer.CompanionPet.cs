using System.Text.Json;
using OmniCore.Protocol;

namespace OmniCore.Host;

public sealed partial class OmniServer
{
    private readonly object _companionGate = new();
    private CompanionPetService? _companion;
    internal CompanionPetService CompanionService
    {
        get
        {
            lock (_companionGate) return _companion ??= new(_platformPathsFactory().DataDirectory,
                CompanionPetService.ReadOptions(_platformPathsFactory().DataDirectory));
        }
    }
    internal void SynchronizeCompanion()
    {
        // This optional cosmetic projection cannot change conversation/execution outcomes.
        try { if (_lastSessionId is { } session) CompanionService.Synchronize(_store, _codecs, session); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or Microsoft.Data.Sqlite.SqliteException or ArgumentException or InvalidOperationException) { }
    }
    private SessionQueryResult QueryCompanion(string name)
    {
        SynchronizeCompanion();
        if (name.StartsWith("companion.pet.events:", StringComparison.Ordinal))
        {
            var after = long.Parse(name[21..], System.Globalization.CultureInfo.InvariantCulture);
            var snapshot = CompanionService.Status();
            return new(name, snapshot.Revision > after
                ? CompanionPetProtocol.EncodeChange(new(snapshot.Revision, snapshot)) : "null");
        }
        var client = name.StartsWith("companion.pet:", StringComparison.Ordinal) ? name[14..] : "terminal";
        return new(name, CompanionPetProtocol.Encode(CompanionService.Status(client)));
    }
    private CommandAck ChangeCompanion(WireEnvelope command)
    {
        try
        {
            SynchronizeCompanion();
            var action = CompanionPetProtocol.DecodeAction(command.PayloadJson)
                ?? throw new ArgumentException("Missing companion action.");
            CompanionService.Act(action, command.MessageId);
            return new(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted());
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or JsonException
            or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        { return new(command.MessageId, "error", error.Message, RuntimeCommandOutcome.Rejected()); }
    }
    internal ExtensionView CompanionExtensionView()
    {
        SynchronizeCompanion(); var state = CompanionService.Status();
        if (!state.Discovered) throw new InvalidOperationException("companion_not_discovered");
        using var doc = JsonDocument.Parse(CompanionPetProtocol.Encode(state));
        return new("core.companion.pet", "core.companion", "indicator", state.Revision, "ready", [], [],
            Metadata: doc.RootElement.Clone(), Indicators:
            [new("level", state.Name + " · " + (state.Species ?? "egg"), "ready", state.Level),
             new("mood", "Mood", "ready", state.Mood, "/100")]);
    }
}
