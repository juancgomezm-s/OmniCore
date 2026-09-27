using OmniCore.Protocol;

namespace OmniCore.Cli;

/// <summary>
/// Renderer JSON (ADR-0030 §4): emite NDJSON de WireEnvelopes tal cual + un registro final
/// RunOutcome { exitCode, outcome, runId }. Contrato de máquina: expone eventos del protocolo
/// (LocalizedText sin traducir, ADR-0040 §6), no estado de presentación.
/// </summary>
public sealed class JsonRenderer
{
    public void Emit(WireEnvelope envelope)
    {
        Console.WriteLine(envelope.ToJson());
    }

    public void EmitOutcome(int exitCode, string outcome, string runId)
    {
        var fields = new string[] {
            "\"exitCode\":" + exitCode,
            "\"outcome\":\"" + outcome + "\"",
            "\"runId\":\"" + runId + "\"",
        };
        Console.WriteLine("{\"type\":\"RunOutcome\"," + string.Join(",", fields) + "}");
    }
}