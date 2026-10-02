namespace OmniCore.Qualification;

using OmniCore.Domain;

/// <summary>
/// Suite `quick` de cualificación (ADR-0007 §7, M5): diez probes deterministas de
/// lectura/razonamiento estructurado, puntuados por reglas exactas sin LLM juez.
/// </summary>
public static class QuickProbeSuite
{
    /// <summary>Id de la suite para BenchmarkIdentity.</summary>
    public const string SuiteId = "omnicore-quick";

    /// <summary>Versión mayor nueva porque la suite y su conjunto de tareas cambiaron.</summary>
    public const string SuiteVersion = "2.0.0";

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
        new Probe(
            ProbeId.WellKnown("reading-last-animal"),
            ProbeKind.Reading,
            "Read the sentence: \"The quick brown fox jumps over the lazy dog.\" Which animal is mentioned last? Reply with only the animal's name, no punctuation.",
            "dog",
            maxCostUsd: 0.00m),
        new Probe(
            ProbeId.WellKnown("reading-week-length"),
            ProbeKind.Reading,
            "How many days are in a standard week? Reply with only the number, no punctuation.",
            "7",
            maxCostUsd: 0.00m),
        new Probe(
            ProbeId.WellKnown("reasoning-addition-small"),
            ProbeKind.Reasoning,
            "Solve 9 plus 8. Reply with only the final number, no punctuation or explanation.",
            "17",
            maxCostUsd: 0.00m),
        new Probe(
            ProbeId.WellKnown("reasoning-subtraction"),
            ProbeKind.Reasoning,
            "Solve 50 minus 8. Reply with only the final number, no punctuation or explanation.",
            "42",
            maxCostUsd: 0.00m),
        new Probe(
            ProbeId.WellKnown("reasoning-multiplication"),
            ProbeKind.Reasoning,
            "Solve 6 multiplied by 7. Reply with only the final number, no punctuation or explanation.",
            "42",
            maxCostUsd: 0.00m),
        new Probe(
            ProbeId.WellKnown("reasoning-sequence"),
            ProbeKind.Reasoning,
            "What number comes next in this sequence: 2, 4, 6? Reply with only the number, no punctuation or explanation.",
            "8",
            maxCostUsd: 0.00m),
        new Probe(
            ProbeId.WellKnown("structured-output-valid-count"),
            ProbeKind.StructuredOutput,
            "Respond with one JSON object only, exactly with the values shown: {\"valid\":true,\"count\":2}. Do not add prose, markdown, or extra keys.",
            "{\"valid\":true,\"count\":2}",
            maxCostUsd: 0.00m),
    ];
}
