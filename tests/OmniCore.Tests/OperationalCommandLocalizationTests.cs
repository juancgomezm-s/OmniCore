using OmniCore.Client;
using OmniCore.Cli;
using OmniCore.Host;
using OmniCore.Protocol;

namespace OmniCore.Tests;

[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class OperationalCommandLocalizationTests
{
    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("it")]
    public void Operational_catalog_has_real_entries_for_every_locale(string locale)
    {
        var localization = new Localization(locale);

        Assert.NotEmpty(OperationalCommandResources.Keys);
        Assert.NotEmpty(OperationalHostResources.Keys);
        foreach (var key in OperationalCommandResources.Keys.Concat(OperationalHostResources.Keys))
        {
            var resolved = localization.ResolveDetailed(key);
            Assert.Equal(locale, resolved.ResourceLocale);
            Assert.False(resolved.UsedFallback);
            Assert.NotEqual(key, resolved.Text);
        }
    }

    [Fact]
    public async Task Maintenance_and_journal_cli_errors_use_selected_locale()
    {
        var french = new StringWriter();
        var gcCode = await MaintenanceCommands.Gc(["gc", "--bad"], french, new Localization("fr"));
        Assert.Equal(2, gcCode);
        Assert.Contains("option ou argument inconnu", french.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Usage : omni gc", french.ToString());
        Assert.DoesNotContain("cli.maintenance", french.ToString());

        var italian = new StringWriter();
        var journalCode = await JournalCommands.VerifyJournal(["verify-journal", "--bad"], italian,
            new Localization("it"));
        Assert.Equal(2, journalCode);
        Assert.Contains("opzione sconosciuta", italian.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Uso: omni verify-journal", italian.ToString());
        Assert.DoesNotContain("cli.journal", italian.ToString());
    }

    [Fact]
    public void Plain_renderer_localizes_its_own_interaction_chrome()
    {
        var projection = new ClientProjection(new Localization("fr"));
        var interactionId = Guid.CreateVersion7().ToString();
        var envelope = WireEnvelope.Event(Ids.NewV7(), "{" + JsonObj.Field("type", "interaction.requested") + ","
            + JsonObj.Field("interactionId", interactionId) + ","
            + JsonObj.Field("kind", "ReconciliationConflict") + ","
            + JsonObj.Field("options", "resolution_applied,resolution_not_applied") + ","
            + JsonObj.Field("subject", "{}") + "}");
        var state = projection.Apply(ClientState.Empty(), envelope);
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            new PlainRenderer("fr").Render(state);
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.Contains("interaction en attente", output.ToString());
        Assert.Contains("résoudre avec : omni resolve " + interactionId, output.ToString());
    }

    [Theory]
    [InlineData("es", "¿Aceptas el resultado?", "Aceptar", "Pedir cambios")]
    [InlineData("en", "Accept the result?", "Accept", "Request changes")]
    [InlineData("fr", "Acceptez-vous le résultat ?", "Accepter", "Demander des modifications")]
    [InlineData("it", "Accetti il risultato?", "Accetta", "Richiedi modifiche")]
    public void Acceptance_overlay_uses_localized_copy_and_stable_wire_ids(string locale, string title,
        string acceptLabel, string reviseLabel)
    {
        var projection = new ClientProjection(new Localization(locale));
        var interactionId = Guid.CreateVersion7().ToString();
        var envelope = WireEnvelope.Event(Ids.NewV7(), "{" + JsonObj.Field("type", "interaction.requested") + ","
            + JsonObj.Field("interactionId", interactionId) + ","
            + JsonObj.Field("kind", "AcceptanceConfirmation") + ","
            + JsonObj.Field("options", "accept,revise") + ","
            + JsonObj.Field("subject", "{}") + "}");

        var overlay = Assert.Single(projection.Apply(ClientState.Empty(), envelope).Overlays);

        Assert.Equal("AcceptanceConfirmation", overlay.Kind);
        Assert.Equal(title, overlay.Title);
        Assert.Equal(new[] { "accept", "revise" }, overlay.OptionIds);
        Assert.Equal(new[] { acceptLabel, reviseLabel }, overlay.Options);
    }

    [Fact]
    public void Plain_questionnaire_localizes_labels_without_changing_choice_ids()
    {
        var prompt = new QuestionnairePromptDto("Title", null, new[]
        {
            new QuestionFieldDto("question-id", "Prompt", null, "SingleChoice",
                new[] { new QuestionOptionDto("stable-option-id", "Option", null) }, null,
                Required: true, MinSelections: 1, MaxSelections: 1, MaxTextLength: null),
        });
        var originalIn = Console.In;
        var originalOut = Console.Out;
        using var input = new StringReader("stable-option-id\n");
        using var output = new StringWriter();
        try
        {
            Console.SetIn(input);
            Console.SetOut(output);
            var response = PlainQuestionnaireForm.Read(prompt, new Localization("fr"));

            Assert.False(response!.Cancelled);
            Assert.Equal("stable-option-id", Assert.Single(response.Answers).SelectedOptionIds.Single());
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
        }

        Assert.Contains("Saisissez :cancel à tout moment", output.ToString());
        Assert.Contains("obligatoire", output.ToString());
        Assert.Contains("Sélection (IDs séparés par des virgules)", output.ToString());
    }

    [Theory]
    [InlineData("es", "Restaurable", "Restaurado")]
    [InlineData("en", "Restorable", "Restored")]
    [InlineData("fr", "Restaurable", "Restauré")]
    [InlineData("it", "Ripristinabile", "Ripristinato")]
    public void Worktree_known_states_resolve_the_specific_resource_in_all_locales(string locale,
        string ready, string applied)
    {
        var localization = new Localization(locale);
        Assert.Equal(ready, WorktreeCommands.UndoState(localization, "Ready"));
        Assert.Equal(applied, WorktreeCommands.UndoFileOutcome(localization, "Applied"));
    }

    [Theory]
    [InlineData("es", "Desactualizado", "Razonamiento", "Tiempo agotado", "Sólo parches")]
    [InlineData("en", "Stale", "Reasoning", "Timed out", "Patches only")]
    [InlineData("fr", "Obsolète", "Raisonnement", "Délai dépassé", "Correctifs uniquement")]
    [InlineData("it", "Obsoleto", "Ragionamento", "Tempo scaduto", "Solo patch")]
    public void Model_policy_known_states_and_types_are_translated_but_unknown_values_are_preserved(
        string locale, string qualificationState, string probeKind, string probeStatus, string category)
    {
        var localization = new Localization(locale);
        Assert.Equal(qualificationState, ModelPolicyCommands.LocalizeModelValue(localization,
            "qualificationState", "Stale"));
        Assert.Equal(probeKind, ModelPolicyCommands.LocalizeModelValue(localization, "probeKind", "Reasoning"));
        Assert.Equal(probeStatus, ModelPolicyCommands.LocalizeModelValue(localization, "probeStatus", "Timeout"));
        Assert.Equal(category, ModelPolicyCommands.LocalizeModelValue(localization, "category", "PatchOnly"));
        Assert.Equal("ProviderSpecificState", ModelPolicyCommands.LocalizeModelValue(localization,
            "qualificationState", "ProviderSpecificState"));
    }

    [Theory]
    [InlineData("es", "creada", "actualizada", "eliminada")]
    [InlineData("en", "created", "updated", "deleted")]
    [InlineData("fr", "créée", "mise à jour", "supprimée")]
    [InlineData("it", "creata", "aggiornata", "eliminata")]
    public void Model_policy_history_change_kinds_are_localized_without_changing_unknown_values(
        string locale, string created, string updated, string deleted)
    {
        var localization = new Localization(locale);

        Assert.Equal(created, ModelPolicyCommands.LocalizeChangeKind(localization, "created"));
        Assert.Equal(updated, ModelPolicyCommands.LocalizeChangeKind(localization, "updated"));
        Assert.Equal(deleted, ModelPolicyCommands.LocalizeChangeKind(localization, "deleted"));
        Assert.Equal("provider_change", ModelPolicyCommands.LocalizeChangeKind(localization, "provider_change"));
    }

    [Theory]
    [InlineData("es", "Equilibrado", "Usuario", "Chat compatible con OpenAI")]
    [InlineData("en", "Balanced", "User", "OpenAI-compatible chat")]
    [InlineData("fr", "Équilibré", "Utilisateur", "Chat compatible avec OpenAI")]
    [InlineData("it", "Bilanciato", "Utente", "Chat compatibile con OpenAI")]
    public void Doctor_displays_localize_known_configuration_and_provider_states(string locale,
        string profile, string userScope, string providerFamily)
    {
        var localization = new Localization(locale);
        string Localize(string key, IReadOnlyDictionary<string, string> args) => localization.Resolve(key, args);

        Assert.Equal(profile, OmniCliRuntime.DoctorValueLabel("permissionProfile", "Balanced", Localize));
        Assert.Equal(userScope, OmniCliRuntime.DoctorValueLabel("configScope", "User", Localize));
        Assert.Equal(providerFamily, OmniCliRuntime.DoctorValueLabel("providerFamily", "OpenAiChatCompatible", Localize));
        Assert.Equal("FutureProfile", OmniCliRuntime.DoctorValueLabel("permissionProfile", "FutureProfile", Localize));
    }
}
