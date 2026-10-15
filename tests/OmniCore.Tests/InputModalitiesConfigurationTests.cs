using OmniCore.Host;

namespace OmniCore.Tests;

public sealed class InputModalitiesConfigurationTests
{
    private const string Providers = "providers:\n  scripted: { family: OpenAIResponses, baseUrl: https://fixture.invalid/v1, auth: none, billingMode: Local }\n";

    [Fact]
    public void Configured_input_modalities_load_into_the_effective_model_profile()
    {
        var loaded = new ConfigLoader().Load(Providers, "models:\n  visual-model:\n    provider: scripted\n    inputModalities: [text, image]\n");

        Assert.Equal(new[] { "text", "image" }, loaded.Registry.Model("visual-model")!.InputModalities);
    }

    [Theory]
    [InlineData("image", "config.wrongType")]
    [InlineData("[]", "config.emptyCollection")]
    [InlineData("[image, image]", "config.outOfRange")]
    [InlineData("[image, unsupported modality]", "config.wrongType")]
    public void Invalid_input_modality_yaml_is_rejected_at_the_field(string value, string diagnosticKey)
    {
        var error = Assert.Throws<ConfigValidationException>(() => new ConfigLoader().Load(Providers,
            "models:\n  visual-model:\n    provider: scripted\n    inputModalities: " + value + "\n"));

        Assert.Contains(error.Diagnostics, diagnostic => diagnostic.KeyPath == "models.visual-model.inputModalities"
            && diagnostic.Message.Key == diagnosticKey && diagnostic.Line > 0 && diagnostic.Column > 0);
    }
}
