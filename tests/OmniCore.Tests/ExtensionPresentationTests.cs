using System.Text.Json;
using OmniCore.Client;
using OmniCore.Host;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed partial class ExtensionPlatformTests
{
    internal static string PreferenceExample(string target)
    {
        var sdk = Path.Combine(RepoRoot, "sdk", "extensions");
        Directory.CreateDirectory(target);
        File.Copy(Path.Combine(sdk, "examples", "preferences", "omnicore-extension.yaml"), Path.Combine(target, "omnicore-extension.yaml"));
        foreach (var file in Directory.GetFiles(Path.Combine(sdk, "dist"), "*.js", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, "dist", Path.GetRelativePath(Path.Combine(sdk, "dist"), file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination);
        }
        return target;
    }
    private static ExtensionView ReadPreferenceView(Fixture fixture) => ExtensionProtocol.Decode(fixture.Server.Query(ViewQuery("example.preferences", "preferences"), CancellationToken.None)!.Json);
    private static CommandAck SubmitForm(Fixture fixture, ExtensionView view, string values, bool human = true)
    {
        using var document = JsonDocument.Parse(values);
        var submission = new ExtensionFormSubmission("example.preferences", "preferences", "save", view.Revision, view.Scope!, document.RootElement.Clone());
        var command = WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"extensions.package\",\"action\":\"submit-view\","
            + JsonObj.Field("extensionId", view.ExtensionId) + "," + JsonObj.Field("submission", JsonSerializer.Serialize(submission, ExtensionJson.Default.ExtensionFormSubmission)) + "}");
        return human ? fixture.Server.SendUserAction(command, CancellationToken.None) : fixture.Server.Send(command, CancellationToken.None);
    }
    private const string ValidForm = """{"title":"Nuevo título","interval":3.5,"enabled":false,"detail":"wide","api":"1.0"}""";

    [Fact]
    public void Real_Node_form_saves_only_with_human_authority_and_consumes_its_snapshot()
    {
        using var fixture = new Fixture(); fixture.Packages.Install(PreferenceExample(Path.Combine(fixture.Root, "preferences-example")), CancellationToken.None);
        fixture.Packages.Change("example.preferences", "enable", true);
        var view = ReadPreferenceView(fixture);
        Assert.Equal("form", view.Kind); Assert.NotNull(view.Scope!.SnapshotId); Assert.True(Assert.Single(view.Actions).Enabled);
        Assert.Equal("error", SubmitForm(fixture, view, ValidForm, human: false).Status);
        Assert.Equal("ok", SubmitForm(fixture, view, ValidForm).Status);
        var next = ReadPreferenceView(fixture);
        Assert.Equal("Nuevo título", next.Fields!.Single(field => field.Id == "title").Value!.Value.GetString());
        Assert.Equal(3.5m, next.Fields!.Single(field => field.Id == "interval").Value!.Value.GetDecimal());
        Assert.NotEqual(view.Scope.SnapshotId, next.Scope!.SnapshotId);
        Assert.Equal(view.Revision, next.Revision); // Even a reset/eviction that reuses revision 1 cannot replay the old nonce.
        Assert.Equal("error", SubmitForm(fixture, view, ValidForm).Status);
    }

    [Theory]
    [InlineData("{\"title\":\"\",\"interval\":3.5,\"enabled\":false,\"detail\":\"wide\",\"api\":\"1.0\"}")]
    [InlineData("{\"title\":\"A\",\"interval\":\"3.5\",\"enabled\":false,\"detail\":\"wide\",\"api\":\"1.0\"}")]
    [InlineData("{\"title\":\"A\",\"interval\":3.5,\"enabled\":false,\"detail\":\"invalid\",\"api\":\"1.0\"}")]
    [InlineData("{\"title\":\"A\",\"interval\":3.5,\"enabled\":false,\"detail\":\"wide\",\"api\":\"2.0\"}")]
    [InlineData("{\"title\":\"A\",\"interval\":3.5,\"enabled\":false,\"detail\":\"wide\",\"api\":\"1.0\",\"extra\":true}")]
    public void Form_rejects_invalid_required_types_choices_readonly_and_unknown_fields_without_partial_write(string values)
    {
        using var fixture = new Fixture(); fixture.Packages.Install(PreferenceExample(Path.Combine(fixture.Root, "preferences-example")), CancellationToken.None);
        fixture.Packages.Change("example.preferences", "enable", true);
        var view = ReadPreferenceView(fixture);
        Assert.Equal("error", SubmitForm(fixture, view, values).Status);
        var next = ReadPreferenceView(fixture);
        Assert.Equal("OmniCore", next.Fields!.Single(field => field.Id == "title").Value!.Value.GetString());
    }

    [Fact]
    public void Form_rejects_session_change_and_revoked_or_changed_package()
    {
        using var fixture = new Fixture(); fixture.Packages.Install(PreferenceExample(Path.Combine(fixture.Root, "preferences-example")), CancellationToken.None);
        fixture.Packages.Change("example.preferences", "enable", true);
        var old = ReadPreferenceView(fixture);
        fixture.Server.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"conversation.new\"}"), CancellationToken.None);
        Assert.Equal("error", SubmitForm(fixture, old, ValidForm).Status);
        var current = ReadPreferenceView(fixture); fixture.Packages.Change("example.preferences", "disable");
        Assert.Equal("error", SubmitForm(fixture, current, ValidForm).Status);
        fixture.Packages.Change("example.preferences", "enable", true); current = ReadPreferenceView(fixture);
        File.AppendAllText(Path.Combine(fixture.Data, "extensions", "example.preferences", "dist", "examples", "preferences.js"), "\n// changed");
        Assert.Equal("error", SubmitForm(fixture, current, ValidForm).Status);
    }

    [Fact]
    public void Typed_contract_rejects_duplicate_ids_invalid_defaults_and_ambiguous_options()
    {
        var view = new ExtensionView("example.form", "example", "form", 1, "ready", [], [],
            Fields: [new("count", "Count", "number")]);
        Assert.Throws<JsonException>(() => ExtensionProtocol.Decode(ExtensionProtocol.Encode(view with { Fields = [new("count", "Count", "number"), new("count", "Again", "number")] })));
        using var text = JsonDocument.Parse("\"not a number\"");
        Assert.Throws<JsonException>(() => ExtensionProtocol.Decode(ExtensionProtocol.Encode(view with { Fields = [new("count", "Count", "number", text.RootElement)] })));
        Assert.Throws<JsonException>(() => ExtensionProtocol.Decode(ExtensionProtocol.Encode(view with { Fields = [new("choice", "Choice", "choice", Options: ["a", "a"])] })));
    }

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("it")]
    public void Indicator_values_and_localized_states_use_existing_semantic_widget_roles(string locale)
    {
        var view = new ExtensionView("example.health", "example", "indicator", 1, "ready", [], [],
            Indicators: [new("unknown", "Quota", "unknown"), new("warning", "Latency", "warning", 2.5m, "s")]);
        var widget = ExtensionViewProjection.IndicatorWidget(view, new Localization(locale));
        var model = Assert.IsType<ListWidgetModel>(widget.Build(ClientState.Empty(), WidgetSize.Expanded));
        Assert.Contains("—", model.Rows[0].Text); Assert.Equal(ThemeRole.Muted, model.Rows[0].Role);
        Assert.Equal(ThemeRole.Attention, model.Rows[1].Role);
        Assert.DoesNotContain("extension.indicator.", string.Join(" ", model.Rows.Select(row => row.Text)));
    }
}
