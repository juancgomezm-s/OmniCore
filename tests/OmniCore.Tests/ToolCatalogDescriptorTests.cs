using System.Text.Json;
using System.Text.RegularExpressions;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Tools;

namespace OmniCore.Tests;

public sealed class ToolCatalogDescriptorTests
{
    private static readonly Regex SpanishOnlyText = new(
        @"[áéíóúñ¿¡]|\b(lee|lista|busca|ejecuta|escribe|aplica|propone|resuelve|dentro|archivo|archivos|contenido|directorio|carpeta|usuario|requiere|mediante|comando|riesgo)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void Duplicate_tool_registration_is_rejected_without_replacing_existing_tool()
    {
        var existing = FakeTool.Read("fake.duplicate");
        var catalog = new FakeCatalog().Add(existing);
        var replacement = FakeTool.Write("fake.duplicate");

        var rejected = Assert.Throws<ToolRegistrationRejected>(() => catalog.Add(replacement));

        Assert.Equal("fake.duplicate", rejected.ToolId.ToString());
        Assert.Contains("ToolRegistrationRejected", rejected.Message, StringComparison.Ordinal);
        Assert.Same(existing, catalog.Find(new ToolId("fake.duplicate")));
    }

    [Fact]
    public void Protected_builtin_tool_cannot_be_replaced()
    {
        var protectedTool = new ArtifactReadTool(new UnusedArtifactStore(), _ => true);
        Assert.Equal(ToolProtection.Protected, protectedTool.Descriptor.Protection);
        var catalog = new FakeCatalog().Add(protectedTool);

        var rejected = Assert.Throws<ToolRegistrationRejected>(() =>
            catalog.Add(FakeTool.Write("artifact.read")));

        Assert.Equal("artifact.read", rejected.ToolId.ToString());
        Assert.Same(protectedTool, catalog.Find(new ToolId("artifact.read")));
    }

    [Fact]
    public void Built_in_tool_and_parameter_descriptions_sent_to_model_are_english()
    {
        var artifactRead = new ArtifactReadTool(new UnusedArtifactStore(), _ => true);
        var host = new HostTools(new PathBoundaryValidator(), new PlanService(),
            includeSimulationTools: true, includeMutationTools: true, includeProcessTools: true,
            artifactReadTool: artifactRead);

        foreach (var definition in host.Catalog().Definitions())
        {
            AssertEnglish(definition.Name, definition.Description);
            using var schema = JsonDocument.Parse(definition.InputSchemaJson);
            foreach (var description in SchemaDescriptions(schema.RootElement))
            {
                AssertEnglish(definition.Name + " schema", description);
            }
        }
    }

    private static IEnumerable<string> SchemaDescriptions(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("description") && property.Value.ValueKind == JsonValueKind.String)
                {
                    yield return property.Value.GetString() ?? string.Empty;
                }

                foreach (var nested in SchemaDescriptions(property.Value))
                {
                    yield return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in SchemaDescriptions(item))
                {
                    yield return nested;
                }
            }
        }
    }

    private static void AssertEnglish(string source, string text)
    {
        Assert.False(SpanishOnlyText.IsMatch(text),
            $"Model-facing tool description contains Spanish-only text ({source}): {text}");
    }

    private sealed class UnusedArtifactStore : IArtifactStore
    {
        public ArtifactRef PutText(string content, string mediaType, ArtifactKind kind, Sensitivity sensitivity) =>
            throw new NotSupportedException("Descriptor test only.");

        public string? GetText(ContentHash hash) => throw new NotSupportedException("Descriptor test only.");

        public bool Verify(ContentHash hash, long expectedSize) => throw new NotSupportedException("Descriptor test only.");
    }
}
