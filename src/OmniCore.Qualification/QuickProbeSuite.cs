namespace OmniCore.Qualification;

using OmniCore.Domain;

/// <summary>
/// Suite `quick` de cualificación (ADR-0007 §6, M5). Diez probes deterministas de lectura,
/// razonamiento y salida estructurada, puntuados por reglas exactas sin LLM juez. Esta cobertura
/// no mide tool calling, coding, recuperación de errores, planificación ni mutaciones; esos traits
/// no se infieren de preguntas en lenguaje natural.
/// </summary>
public static class QuickProbeSuite
{
    /// <summary>Id de la suite para BenchmarkIdentity.</summary>
    public const string SuiteId = "omnicore-quick";

    /// <summary>Versión de la suite; incluye correcciones del oracle de puntuación.</summary>
    public const string SuiteVersion = "1.1.1";

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
            ProbeId.WellKnown("reading-order"),
            ProbeKind.Reading,
            "Read the sentence: \"Mira, Sol, and Tavi entered the room in that order.\" Reply with only the name that entered second.",
            "Sol",
            maxCostUsd: 0.00m),
        new Probe(
            ProbeId.WellKnown("reasoning-subtraction"),
            ProbeKind.Reasoning,
            "A shelf has 31 books. Remove 8, then add 5. Reply with only the final number.",
            "28",
            maxCostUsd: 0.00m),
        new Probe(
            ProbeId.WellKnown("reasoning-comparison"),
            ProbeKind.Reasoning,
            "Ava has 4 tokens, Bo has 9, and Cy has 6. Reply with only the name of the person with the fewest tokens.",
            "Ava",
            maxCostUsd: 0.00m),
        new Probe(
            ProbeId.WellKnown("reasoning-sequence"),
            ProbeKind.Reasoning,
            "Continue the sequence 3, 6, 12, 24. Reply with only the next number.",
            "48",
            maxCostUsd: 0.00m),
        new Probe(
            ProbeId.WellKnown("structured-output-extra-key"),
            ProbeKind.StructuredOutput,
            "Return exactly one JSON object with keys \"city\" and \"n\", values \"Lima\" and 3. No markdown or extra keys.",
            "{\"city\":\"Lima\",\"n\":3}",
            maxCostUsd: 0.00m),
        new Probe(
            ProbeId.WellKnown("structured-output-boolean"),
            ProbeKind.StructuredOutput,
            "Return exactly this JSON value and nothing else: {\"ready\":false,\"items\":[\"a\",\"b\"]}",
            "{\"ready\":false,\"items\":[\"a\",\"b\"]}",
            maxCostUsd: 0.00m),
        new Probe(
            ProbeId.WellKnown("instruction-no-explanation"),
            ProbeKind.Reasoning,
            "What is 6 times 7? Reply with only the number, with no words, punctuation, or explanation.",
            "42",
            maxCostUsd: 0.00m),
    ];
}
