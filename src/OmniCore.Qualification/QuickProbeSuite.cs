namespace OmniCore.Qualification;

using OmniCore.Domain;

/// <summary>
/// Suite `quick` inicial de cualificación (ADR-0007 §6, M5). Tres probes deterministas de
/// lectura/razonamiento estructurado, puntuados por reglas exactas sin LLM juez. La suite
/// completa futura tendrá 10–20 probes; esta es la base mínima y no declara la suite completa
/// como terminada.
/// </summary>
public static class QuickProbeSuite
{
    /// <summary>Id de la suite para BenchmarkIdentity.</summary>
    public const string SuiteId = "omnicore-quick";

    /// <summary>Versión de la suite; incrementa ante un cambio de probes o umbrales.</summary>
    public const string SuiteVersion = "1.0.0";

    public static IReadOnlyList<Probe> Probes() =>
    [
        new Probe(
            ProbeId.WellKnown("reading-basic"),
            ProbeKind.Reading,
            "Read the sentence: \"The quick brown fox jumps over the lazy dog.\" Which animal is mentioned first? Reply with only the animal's name, no punctuation.",
            "fox",
            maxCostUsd: 0.00m),
        new Probe(
            ProbeId.WellKnown("reasoning-arithmetic"),
            ProbeKind.Reasoning,
            "Solve the arithmetic problem 17 + 25 in your head. Reply with only the final number, no units, no punctuation, no explanation.",
            "42",
            maxCostUsd: 0.00m),
        new Probe(
            ProbeId.WellKnown("structured-output"),
            ProbeKind.StructuredOutput,
            "Respond with a single JSON object and nothing else, exactly in this shape and with these values: {\"status\":\"ok\",\"count\":1}. Do not add any prose, markdown, or extra keys.",
            "{\"status\":\"ok\",\"count\":1}",
            maxCostUsd: 0.00m),
    ];
}
