using OmniCore.Protocol;

namespace OmniCore.Client;

public sealed record PixelCell(string Glyph, string Foreground, string Background);
/// <summary>Generic declarative pixels: a terminal maps cells, graphical clients can map AppearanceId instead.</summary>
public sealed record PixelWidgetModel(string Title, IReadOnlyList<IReadOnlyList<PixelCell>> Pixels,
    IReadOnlyList<WidgetRowModel> Rows) : WidgetModel;

public sealed class CompanionPetSidebarWidget(CompanionPetSnapshot pet, string animation = "idle", long frame = 0,
    bool textual = false, string locale = "es") : ISidebarWidget
{
    public string Id => "core.companion";
    public string Title => CompanionPetPresentation.Word("pet", locale) + " · " + CompanionPetPresentation.AnimationName(animation, locale);
    public ThemeRole Accent => ThemeRole.Success;
    public int DefaultPriority => 35;
    public WidgetRelevance Evaluate(ClientState state) => pet.Discovered && pet.Preferences.Visible ? WidgetRelevance.Normal : WidgetRelevance.None;
    public WidgetModel Build(ClientState state, WidgetSize size)
    {
        var rows = new[] { new WidgetRowModel(CompanionPetPresentation.Summary(pet, locale), ThemeRole.Primary),
            new WidgetRowModel(CompanionPetPresentation.Word("pet_action", locale), ThemeRole.Info, "companion.pet", "pet") };
        if (size == WidgetSize.Compact || textual) return new ListWidgetModel(Title, rows);
        return new PixelWidgetModel(Title, CompanionSpriteCatalog.Render(pet, animation, frame), rows);
    }
}

public static class CompanionPetPresentation
{
    public static string Word(string id, string locale = "es") => (locale.StartsWith("es", StringComparison.Ordinal), id) switch
    {
        (true, "ox") => "Buey", (true, "fox") => "Zorro", (true, "owl") => "Búho", (true, "beaver") => "Castor",
        (true, "cat") => "Gato", (true, "capybara") => "Capibara", (true, "egg") => "Huevo",
        (true, "pet") => "Mascota", (true, "pet_action") => "Acariciar",
        (true, "idle") => "Reposo", (true, "working") => "Trabajando", (true, "waiting") => "Esperando",
        (true, "success") => "¡Bien!", (true, "failure") => "Sorpresa", (true, "sleep") => "Durmiendo",
        (true, "play") => "De paseo",
        (true, "baby") => "Bebé", (true, "young") => "Joven", (true, "adult") => "Adulto", (true, "legendary") => "Legendario",
        (false, "pet_action") => "Pet", _ => char.ToUpperInvariant(id[0]) + id[1..],
    };
    public static string AnimationName(string animation, string locale) => animation == "pet"
        ? locale.StartsWith("es", StringComparison.Ordinal) ? "Caricia" : "Petted" : Word(animation, locale);
    public static string Summary(CompanionPetSnapshot pet, string locale = "es") => pet.Species is null
        ? $"{pet.Name} · {Word("egg", locale)} · {pet.Interactions}/{pet.HatchAfter}"
        : $"{pet.Name} · {Word(pet.Species, locale)} · L{pet.Level} · {pet.Mood}/100";
    public static string Animation(CompanionPetSnapshot pet, bool working, bool waiting, DateTimeOffset lastActive, DateTimeOffset now)
    {
        if (pet.Reaction is { } reaction && reaction.Until > now) return reaction.AnimationId;
        if (waiting) return "waiting";
        if (working) return "working";
        return now - lastActive >= TimeSpan.FromMinutes(5) ? "sleep" : "idle";
    }
    public static CompanionPetSnapshot Execute(IOmniClient client, string argument, string clientId = "terminal")
    {
        var parts = argument.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var action = parts.Length == 0 ? "discover" : parts[0].ToLowerInvariant();
        var request = action switch
        {
            "reduced" => new CompanionPetAction("preferences", ClientId: clientId, ReducedMotion: true),
            "motion" => new CompanionPetAction("preferences", ClientId: clientId, ReducedMotion: false),
            _ => new CompanionPetAction(action, parts.Length > 1 ? parts[1] : null, clientId),
        };
        var json = CompanionPetProtocol.EncodeAction(request);
        var ack = client.Send(WireEnvelope.Command(WireEnvelope.NewId(), "{\"cmd\":\"companion.pet\"," + json[1..]), CancellationToken.None);
        if (ack.Status != "ok") throw new InvalidOperationException(ack.Error ?? "companion_action_failed");
        return CompanionPetProtocol.Decode(client.Query("companion.pet:" + clientId, CancellationToken.None)?.Json ?? "null")
            ?? throw new InvalidOperationException("companion_unavailable");
    }
}
