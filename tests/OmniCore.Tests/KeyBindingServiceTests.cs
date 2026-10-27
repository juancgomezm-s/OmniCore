using OmniCore.Client;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class KeyBindingServiceTests
{
    private static KeyChord Key(string text) { Assert.True(KeyChord.TryParse(text, out var key)); return key; }
    private static KeyBindingContext Context(params string[] enabled) => new(enabled.Select(key => new KeyValuePair<string, bool>(key, true)));
    private static KeyBindingDefinition User(string keys, string action, string when = "true", int priority = 0) =>
        new(keys, new(KeyBindingTargetKind.Action, action), when, priority, KeyBindingSource.User, "User");
    private static KeyBindingResolution Resolve(KeyBindingService service, string keys, params string[] context)
    { Assert.True(KeyChord.TryParseSequence(keys, out var sequence)); return service.Resolve(sequence, Context(context)); }

    [Theory]
    [InlineData("control+a", "Ctrl+A")]
    [InlineData("alt+shift+f12", "Alt+Shift+F12")]
    [InlineData("cmd+return", "Meta+Enter")]
    [InlineData("Escape", "Esc")]
    [InlineData("Ctrl+Plus", "Ctrl+Plus")]
    [InlineData(";", ";")]
    public void Portable_keys_are_normalized_without_locale_dependent_names(string input, string canonical) => Assert.Equal(canonical, Key(input).ToString());

    [Theory]
    [InlineData("Ctrl+Ctrl+C")]
    [InlineData("Ctrl+")]
    [InlineData("Hyper+A")]
    [InlineData("F0")]
    [InlineData("F25")]
    [InlineData("Ctrl+RunProcess")]
    public void Invalid_keys_are_rejected(string text) => Assert.False(KeyChord.TryParse(text, out _));

    [Theory]
    [InlineData("input && (!paletteOpen || awaitingPermission)", true)]
    [InlineData("input && paletteOpen", false)]
    [InlineData("false || input && true", true)]
    [InlineData("(false || input) && false", false)]
    [InlineData("!input || awaitingPermission", false)]
    public void Boolean_contexts_have_explicit_precedence_and_missing_keys_are_false(string when, bool matched)
    {
        var service = new KeyBindingService([User("F8", ClientActionIds.ViewPlan, when)]);
        Assert.Equal(matched ? KeyBindingResolutionStatus.Matched : KeyBindingResolutionStatus.None, Resolve(service, "F8", "input").Status);
    }

    [Theory]
    [InlineData("input == true")]
    [InlineData("input &&")]
    [InlineData("(input")]
    [InlineData("System.IO.File.Delete")]
    public void Invalid_or_unpublished_contexts_are_diagnosed_and_never_executed(string when)
    {
        var service = new KeyBindingService([User("F8", ClientActionIds.ViewPlan, when)]);
        Assert.Contains(service.Diagnostics, issue => issue.Code is "invalid_when" or "unknown_context");
        Assert.Equal(KeyBindingResolutionStatus.None, Resolve(service, "F8", "input").Status);
    }

    [Fact]
    public void Priority_precedes_source_and_user_precedes_extension_then_default()
    {
        var catalog = KeyBindingTargetCatalog.Core.WithTargets([new("Action:pilot.inspect", "pilot")]);
        var extension = new KeyBindingDefinition("F2", new(KeyBindingTargetKind.Action, "pilot.inspect"), Source: KeyBindingSource.Extension, SourceId: "pilot");
        var service = new KeyBindingService([extension], catalog);
        Assert.Equal("pilot.inspect", Resolve(service, "F2").Binding!.Definition.Target.Id);
        service = new([extension, User("F2", ClientActionIds.HelpShow)], catalog);
        Assert.Equal(ClientActionIds.HelpShow, Resolve(service, "F2").Binding!.Definition.Target.Id);
        service = new([extension with { Priority = 5 }, User("F2", ClientActionIds.HelpShow)], catalog);
        Assert.Equal("pilot.inspect", Resolve(service, "F2").Binding!.Definition.Target.Id);
    }

    [Fact]
    public void Equal_rank_conflicts_are_reported_instead_of_choosing_by_registration_order()
    {
        var definitions = new[] { User("Ctrl+P", ClientActionIds.ViewPlan), User("Ctrl+P", ClientActionIds.ViewTasks) };
        foreach (var ordered in new[] { definitions, definitions.Reverse() })
        {
            var service = new KeyBindingService(ordered);
            Assert.Contains(service.Diagnostics, issue => issue.Code == "conflict");
            Assert.Equal(KeyBindingResolutionStatus.Conflict, Resolve(service, "Ctrl+P").Status);
            Assert.Null(Resolve(service, "Ctrl+P").Binding);
        }
        var duplicate = new KeyBindingService([definitions[0], definitions[0]]);
        Assert.Equal(KeyBindingResolutionStatus.Matched, Resolve(duplicate, "Ctrl+P").Status);
    }

    [Fact]
    public void Extensions_can_only_bind_their_own_targets_and_cannot_remove_defaults()
    {
        var catalog = KeyBindingTargetCatalog.Core.WithTargets([new("Action:pilot.inspect", "pilot"), new("Action:other.inspect", "other"), new("Command:CANCEL", "pilot")]);
        var service = new KeyBindingService([
            new("Ctrl+1", new(KeyBindingTargetKind.Action, ClientActionIds.RunCancel), Source: KeyBindingSource.Extension, SourceId: "pilot"),
            new("Ctrl+2", new(KeyBindingTargetKind.Action, "other.inspect"), Source: KeyBindingSource.Extension, SourceId: "pilot"),
            new("Ctrl+4", new(KeyBindingTargetKind.Command, "CANCEL"), Source: KeyBindingSource.Extension, SourceId: "pilot"),
            new("", new(KeyBindingTargetKind.Action, "sidebar.toggle"), Source: KeyBindingSource.Extension, SourceId: "pilot", RemoveDefault: true),
            new("Ctrl+3", new(KeyBindingTargetKind.Action, "pilot.inspect"), Source: KeyBindingSource.Extension, SourceId: "pilot")], catalog);
        Assert.Equal(3, service.Diagnostics.Count(issue => issue.Code == "extension_ownership"));
        Assert.Contains(service.Diagnostics, issue => issue.Code == "invalid_removal");
        Assert.Equal(KeyBindingResolutionStatus.None, Resolve(service, "Ctrl+1", "runActive").Status);
        Assert.Equal("pilot.inspect", Resolve(service, "Ctrl+3").Binding!.Definition.Target.Id);
        Assert.Equal("sidebar.toggle", Resolve(service, "F2").Binding!.Definition.Target.Id);
    }

    [Theory]
    [InlineData("run.interrupt")]
    [InlineData("run.cancel")]
    public void Protected_actions_are_restored_when_removed_or_made_unreachable(string action)
    {
        foreach (var replacement in new[] { Array.Empty<KeyBindingDefinition>(), new[] { User("F9", action, "false") },
            new[] { User("F9", action, "paletteOpen") } })
        {
            var service = new KeyBindingService(new[] { User("", action) with { RemoveDefault = true } }.Concat(replacement));
            Assert.Contains(service.Diagnostics, issue => issue.Code == "protected_restored" && issue.Subject == action);
            Assert.Equal(ClientActionIds.RunInterrupt, Resolve(service, "Ctrl+C", "runActive").Binding!.Definition.Target.Id);
            Assert.Equal(ClientActionIds.RunCancel, Resolve(service, "Ctrl+C", "runActive", "interruptPending").Binding!.Definition.Target.Id);
        }
    }

    [Fact]
    public void Valid_remapping_is_preserved_and_masking_a_protected_key_is_diagnosed()
    {
        var service = new KeyBindingService([
            User("", ClientActionIds.RunInterrupt) with { RemoveDefault = true }, User("F8", ClientActionIds.RunInterrupt, "runActive"),
            User("", ClientActionIds.RunCancel) with { RemoveDefault = true }, User("F9", ClientActionIds.RunCancel, "runActive")]);
        Assert.DoesNotContain(service.Diagnostics, issue => issue.Code == "protected_restored");
        Assert.Equal(ClientActionIds.RunCancel, Resolve(service, "F9", "runActive").Binding!.Definition.Target.Id);
        Assert.Equal(KeyBindingResolutionStatus.None, Resolve(service, "Ctrl+C", "runActive").Status);
        service = new([User("Ctrl+C", ClientActionIds.HelpShow, "runActive", 100)]);
        Assert.Contains(service.Diagnostics, issue => issue.Code == "protected_collision_removed");
        Assert.Equal(ClientActionIds.RunInterrupt, Resolve(service, "Ctrl+C", "runActive").Binding!.Definition.Target.Id);
    }

    [Fact]
    public void Command_surface_remapping_protects_the_same_action_and_prefix_shadowing_is_visible()
    {
        var service = new KeyBindingService([
            User("", ClientActionIds.RunCancel) with { RemoveDefault = true },
            new("F9", new(KeyBindingTargetKind.Command, "cancel"), "runActive", Source: KeyBindingSource.User, SourceId: "User"),
            User("Ctrl+K", ClientActionIds.HelpShow), User("Ctrl+K Ctrl+P", ClientActionIds.ViewPlan)]);
        Assert.DoesNotContain(service.Diagnostics, issue => issue.Code == "protected_restored");
        Assert.Equal("cancel", Resolve(service, "F9", "runActive").Binding!.Definition.Target.Id);
        Assert.Contains(service.Diagnostics, issue => issue.Code == "prefix_shadowed" && issue.Subject == "Ctrl+K Ctrl+P");
        Assert.Equal(ClientActionIds.HelpShow, Resolve(service, "Ctrl+K").Binding!.Definition.Target.Id);
    }

    [Fact]
    public void Safety_repair_rechecks_both_actions_after_a_crossed_remapping()
    {
        var service = new KeyBindingService([
            User("", ClientActionIds.RunInterrupt) with { RemoveDefault = true },
            User("Ctrl+Shift+C", ClientActionIds.RunInterrupt, "runActive")]);
        Assert.Contains(service.Diagnostics, issue => issue.Code == "protected_restored" && issue.Subject == ClientActionIds.RunInterrupt);
        Assert.Contains(service.Diagnostics, issue => issue.Code == "protected_restored" && issue.Subject == ClientActionIds.RunCancel);
        Assert.Equal(ClientActionIds.RunInterrupt, Resolve(service, "Ctrl+C", "runActive").Binding!.Definition.Target.Id);
        Assert.Equal(ClientActionIds.RunCancel, Resolve(service, "Ctrl+Shift+C", "runActive").Binding!.Definition.Target.Id);
    }

    [Fact]
    public void Chord_sessions_reset_on_timeout_context_change_and_failed_prefix_without_swallowing_interrupt()
    {
        var service = new KeyBindingService([User("Ctrl+K F8", ClientActionIds.ViewPlan, "input")]);
        var now = DateTimeOffset.UtcNow;
        var session = new KeyBindingSession(service, TimeSpan.FromSeconds(1));
        Assert.Equal(KeyBindingResolutionStatus.Pending, session.Push(Key("Ctrl+K"), Context("input"), now).Status);
        Assert.Equal(ClientActionIds.ViewPlan, session.Push(Key("F8"), Context("input"), now.AddMilliseconds(10)).Binding!.Definition.Target.Id);
        session.Push(Key("Ctrl+K"), Context("input"), now);
        Assert.Equal(KeyBindingResolutionStatus.None, session.Push(Key("F8"), Context("input"), now.AddSeconds(2)).Status);
        session.Push(Key("Ctrl+K"), Context("input"), now);
        Assert.Equal(KeyBindingResolutionStatus.None, session.Push(Key("F8"), Context("input", "paletteOpen"), now).Status);
        session.Push(Key("Ctrl+K"), Context("input", "runActive"), now);
        Assert.Equal(ClientActionIds.RunInterrupt, session.Push(Key("Ctrl+C"), Context("input", "runActive"), now).Binding!.Definition.Target.Id);
    }

    [Theory]
    [InlineData(500, KeyBindingResolutionStatus.None)]
    [InlineData(2000, KeyBindingResolutionStatus.Matched)]
    public void Configured_chord_timeout_changes_resolution_and_explicit_false_equals_missing_context(int milliseconds, KeyBindingResolutionStatus expected)
    {
        var service = new KeyBindingService([User("Ctrl+K F8", ClientActionIds.ViewPlan, "input")]);
        var session = new KeyBindingSession(service, TimeSpan.FromMilliseconds(milliseconds));
        var now = DateTimeOffset.UtcNow;
        session.Push(Key("Ctrl+K"), Context("input"), now);
        var context = new KeyBindingContext([new("input", true), new("paletteOpen", false)]);
        Assert.Equal(expected, session.Push(Key("F8"), context, now.AddSeconds(1)).Status);
    }

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("it")]
    public async Task Command_bindings_dispatch_canonical_actions_and_literal_arguments_in_every_locale(string locale)
    {
        var handler = new RecordingHandler();
        var service = new KeyBindingService([new("Ctrl+P", new(KeyBindingTargetKind.Command, "plan", ["$(do-not-execute)"]),
            Source: KeyBindingSource.User, SourceId: "User")]);
        var result = Resolve(service, "Ctrl+P");
        await new KeyBindingDispatcher(handler).ExecuteAsync(result, new(null!, new Localization(locale)), TestContext.Current.CancellationToken);
        Assert.Equal(ClientActionIds.ViewPlan, Assert.Single(handler.Seen).ActionId);
        Assert.Equal(["$(do-not-execute)"], handler.Seen[0].Arguments);
        Assert.Equal("KeyBinding:User", handler.Seen[0].Origin);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new KeyBindingDispatcher(handler)
            .ExecuteAsync(new(KeyBindingResolutionStatus.Pending, null, []), new(null!, new Localization(locale)), TestContext.Current.CancellationToken).AsTask());
        Assert.Single(handler.Seen);
    }

    private sealed class RecordingHandler : IClientActionHandler
    {
        public List<ClientActionInvocation> Seen { get; } = [];
        public ValueTask<ClientActionResult> ExecuteAsync(ClientActionInvocation invocation, ClientContext context, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Seen.Add(invocation); return ValueTask.FromResult(new ClientActionResult("ok")); }
    }
}
