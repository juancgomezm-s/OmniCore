namespace OmniCore.Qualification;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using OmniCore.Domain;

/// <summary>Versioned quick + bounded code-edit fixtures. Never claims general coding or native-tool reliability.</summary>
public static class FullProbeSuite
{
    public const string SuiteId = "omnicore-full";
    public const string SuiteVersion = "1.0.0";
    public static IReadOnlyList<CodeFixtureDefinition> Fixtures { get; } = Array.AsReadOnly(new[]
    {
        new CodeFixtureDefinition("add", "a - b", "a + b", 7, 4, 11, "Return the sum of a and b."),
        new CodeFixtureDefinition("subtract", "a + b", "a - b", 7, 4, 3, "Subtract b from a."),
        new CodeFixtureDefinition("multiply", "a + b", "a * b", 7, 4, 28, "Return the product of a and b."),
        new CodeFixtureDefinition("maximum", "a < b ? a : b", "a > b ? a : b", 7, 4, 7, "Return the larger argument."),
        new CodeFixtureDefinition("minimum", "a > b ? a : b", "a < b ? a : b", 7, 4, 4, "Return the smaller argument."),
        new CodeFixtureDefinition("absolute", "a", "a < 0 ? -a : a", -7, 0, 7, "Return the absolute value of a, without calling methods."),
        new CodeFixtureDefinition("nonnegative", "a", "a < 0 ? 0 : a", -3, 0, 0, "Clamp a to zero if negative, without calling methods."),
        new CodeFixtureDefinition("safe-divide", "a / b", "b == 0 ? 0 : a / b", 7, 0, 0, "Return zero for a zero divisor, otherwise divide a by b."),
        new CodeFixtureDefinition("remainder", "a / b", "a % b", 7, 4, 3, "Return the integer remainder of a divided by b."),
        new CodeFixtureDefinition("equal", "a != b ? 1 : 0", "a == b ? 1 : 0", 7, 7, 1, "Return 1 if the arguments are equal, otherwise 0."),
    });

    public static IReadOnlyList<Probe> Probes() => QuickProbeSuite.Probes().Concat(Fixtures.Select(fixture =>
    {
        var token = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fixture.Before)));
        var expected = JsonSerializer.Serialize(new CodeFixtureEdit("Logic.cs", token, "=> " + fixture.OldExpression + ";", "=> " + fixture.NewExpression + ";"),
            FullFixtureJson.Default.CodeFixtureEdit);
        return new Probe(ProbeId.WellKnown("code-" + fixture.Id), ProbeKind.CodeFixture,
            "Edit this C# file, Logic.cs:\n" + fixture.Before + "\nRequirement: " + fixture.Instruction
            + FormattableString.Invariant($"\nPinned test: Calculate({fixture.A}, {fixture.B}) must return {fixture.Expected}.")
            + "\nReply only with a JSON object with keys path, expectedVersion, oldText, newText."
            + " Use path Logic.cs and expectedVersion " + token + ". Use oldText equal to this exact span: => " + fixture.OldExpression + ";"
            + " Replace only that expression body, keeping => and the final semicolon in newText."
            + ". Preserve all other code. Use one space around operators, and the ternary operator when needed.",
            expected, 0m);
    })).ToArray();
}

public sealed record CodeFixtureDefinition(string Id, string OldExpression, string NewExpression, int A, int B,
    int Expected, string Instruction)
{
    public string Before => "public static class Logic { public static int Calculate(int a, int b) => " + OldExpression + "; }\n";
    public string After => "public static class Logic { public static int Calculate(int a, int b) => " + NewExpression + "; }\n";
}
public sealed record CodeFixtureEdit(string Path, string ExpectedVersion, string OldText, string NewText);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CodeFixtureEdit))]
public partial class FullFixtureJson : JsonSerializerContext;