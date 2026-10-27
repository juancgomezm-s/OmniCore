using System.Collections.Immutable;

namespace OmniCore.Client;

/// <summary>Immutable client-side resolver. Resolution never executes an action or grants authority.</summary>
public sealed class KeyBindingService
{
    private readonly ImmutableArray<KeyBinding> _bindings;
    public ImmutableArray<KeyBinding> Bindings => _bindings;
    public ImmutableArray<KeyBindingDiagnostic> Diagnostics { get; }

    public KeyBindingService(IEnumerable<KeyBindingDefinition>? contributions = null, KeyBindingTargetCatalog? catalog = null,
        IEnumerable<string>? additionalContextKeys = null)
    {
        catalog ??= KeyBindingTargetCatalog.Core;
        var issues = new List<KeyBindingDiagnostic>();
        var names = KeyBindingDefaults.ContextKeys.Concat(additionalContextKeys ?? []).Distinct(StringComparer.Ordinal).ToArray();
        if (names.Length > 12) throw new ArgumentException("At most twelve published context keys are supported.", nameof(additionalContextKeys));
        var contexts = Enumerable.Range(0, 1 << names.Length).Select(mask => new KeyBindingContext(names.Select((name, index) =>
            new KeyValuePair<string, bool>(name, (mask & (1 << index)) != 0)))).ToArray();
        var definitions = (contributions ?? []).Take(513).ToArray();
        if (definitions.Length > 512)
        { issues.Add(new("too_many_bindings", "User")); definitions = []; }
        var active = new List<KeyBinding>();
        KeyBinding? Compile(KeyBindingDefinition definition)
        {
            var subject = definition.Keys + " → " + definition.Target.Id;
            KeyBindingDiagnostic Issue(string code, string? detail = null) => new(code, subject, detail, definition.SourceId);
            if (!Enum.IsDefined(definition.Source) || !Enum.IsDefined(definition.Target.Kind)
                || string.IsNullOrWhiteSpace(definition.SourceId) || definition.SourceId.Length > 512
                || definition.Target.Arguments.Length > 16 || definition.Target.Arguments.Any(arg => arg is null || arg.Length > 4096))
            { issues.Add(Issue("invalid_binding")); return null; }
            var owner = catalog.Owner(definition.Target);
            if (owner is null) { issues.Add(Issue("unknown_target")); return null; }
            if (definition.Source == KeyBindingSource.Extension && (KeyBindingTargetCatalog.Core.Owner(definition.Target) == "Core"
                || definition.Target.EffectiveActionId is { } coreAction
                    && KeyBindingTargetCatalog.Core.Owner(new(KeyBindingTargetKind.Action, coreAction)) == "Core"
                || owner == "Core" || owner != definition.SourceId))
            { issues.Add(Issue("extension_ownership", definition.SourceId)); return null; }
            if (!KeyChord.TryParseSequence(definition.Keys, out var keys))
            { issues.Add(Issue("invalid_keys")); return null; }
            try
            {
                var condition = KeyBindingCondition.Parse(definition.When);
                if (condition.Variables.Except(names, StringComparer.Ordinal).Any())
                { issues.Add(Issue("unknown_context")); return null; }
                return new(definition, keys, condition);
            }
            catch (FormatException error) { issues.Add(Issue("invalid_when", error.Message)); return null; }
        }
        foreach (var definition in KeyBindingDefaults.All)
            active.Add(Compile(definition) ?? throw new ArgumentException("The target catalog must include built-in actions.", nameof(catalog)));
        foreach (var definition in definitions)
        {
            if (definition.RemoveDefault)
            {
                if (definition.Source != KeyBindingSource.User || catalog.Owner(definition.Target) is null
                    || definition.Keys.Length != 0 || definition.When != "true")
                { issues.Add(new("invalid_removal", definition.Target.Id, SourceId: definition.SourceId)); continue; }
                active.RemoveAll(binding => binding.Definition.Source == KeyBindingSource.Default
                    && binding.Definition.Target.CatalogKey == definition.Target.CatalogKey);
            }
            else if (Compile(definition) is { } binding) active.Add(binding);
        }

        bool Overlap(KeyBinding a, KeyBinding b) => contexts.Any(context => a.Condition.Evaluate(context) && b.Condition.Evaluate(context));
        bool Covered(string action) => contexts.Where(context => context["runActive"]
                && (action != ClientActionIds.RunInterrupt || !context["interruptPending"]))
            .All(context => active.Where(binding => binding.Definition.Target.EffectiveActionId == action && binding.Condition.Evaluate(context))
                .Any(binding => ResolveCore(active, binding.Sequence, context) is { Status: KeyBindingResolutionStatus.Matched, Binding: { } winner }
                    && winner.Definition.Target.EffectiveActionId == action));
        // Restoring one action may evict a custom binding that was the other's only route.
        // Recheck both before publishing the immutable registry.
        for (var repair = 0; repair < 3; repair++)
        foreach (var action in new[] { ClientActionIds.RunInterrupt, ClientActionIds.RunCancel })
        {
            if (Covered(action)) continue;
            issues.Add(new("protected_restored", action));
            foreach (var definition in KeyBindingDefaults.Safety.Where(binding => binding.Target.Id == action))
            {
                var restored = Compile(definition)!;
                foreach (var masked in active.Where(binding => binding.Sequence.SequenceEqual(restored.Sequence)
                    && !binding.Definition.Target.SameAs(restored.Definition.Target) && Overlap(binding, restored)).ToArray())
                {
                    active.Remove(masked);
                    issues.Add(new("protected_collision_removed", masked.Definition.Keys, masked.Definition.Target.Id, masked.Definition.SourceId));
                }
                if (!active.Any(binding => binding.Sequence.SequenceEqual(restored.Sequence)
                    && binding.Definition.Target.SameAs(restored.Definition.Target) && binding.Definition.When == restored.Definition.When))
                    active.Add(restored);
            }
        }
        // A protected binding must remain reachable even when a user supplied contradictory contexts.
        if (!Covered(ClientActionIds.RunInterrupt) || !Covered(ClientActionIds.RunCancel))
            throw new InvalidOperationException("Protected keyboard bindings could not be restored.");
        for (var i = 0; i < active.Count; i++)
        for (var j = i + 1; j < active.Count; j++)
        {
            var a = active[i]; var b = active[j];
            if (a.Sequence.SequenceEqual(b.Sequence) && a.Definition.Priority == b.Definition.Priority
                && a.Definition.Source == b.Definition.Source && !a.Definition.Target.SameAs(b.Definition.Target) && Overlap(a, b))
                issues.Add(new("conflict", a.Definition.Keys, a.Definition.Target.Id + " / " + b.Definition.Target.Id, a.Definition.SourceId));
            var shorter = a.Sequence.Length < b.Sequence.Length ? a : b;
            var longer = ReferenceEquals(shorter, a) ? b : a;
            if (shorter.Sequence.Length < longer.Sequence.Length && longer.Sequence.Take(shorter.Sequence.Length).SequenceEqual(shorter.Sequence)
                && Overlap(shorter, longer)) issues.Add(new("prefix_shadowed", longer.Definition.Keys, shorter.Definition.Keys, longer.Definition.SourceId));
        }
        _bindings = active.ToImmutableArray(); Diagnostics = issues.Distinct().ToImmutableArray();
    }

    public KeyBindingResolution Resolve(IReadOnlyList<KeyChord> sequence, KeyBindingContext context) =>
        ResolveCore(_bindings, sequence, context);

    private static KeyBindingResolution ResolveCore(IEnumerable<KeyBinding> bindings, IReadOnlyList<KeyChord> sequence, KeyBindingContext context)
    {
        if (sequence.Count is < 1 or > 4) return new(KeyBindingResolutionStatus.None, null, []);
        var eligible = bindings.Where(binding => binding.Sequence.Length >= sequence.Count
            && binding.Condition.Evaluate(context) && sequence.Select((key, index) => key == binding.Sequence[index]).All(match => match)).ToArray();
        var complete = eligible.Where(binding => binding.Sequence.Length == sequence.Count).ToArray();
        if (complete.Length == 0) return new(eligible.Length == 0 ? KeyBindingResolutionStatus.None : KeyBindingResolutionStatus.Pending, null, eligible.ToImmutableArray());
        var priority = complete.Max(binding => binding.Definition.Priority);
        var source = complete.Where(binding => binding.Definition.Priority == priority).Max(binding => binding.Definition.Source);
        var winners = complete.Where(binding => binding.Definition.Priority == priority && binding.Definition.Source == source)
            .OrderBy(binding => binding.Definition.SourceId, StringComparer.Ordinal).ToImmutableArray();
        if (winners.Any(binding => !binding.Definition.Target.SameAs(winners[0].Definition.Target)))
            return new(KeyBindingResolutionStatus.Conflict, null, winners);
        // Complete bindings resolve immediately; longer chords sharing that prefix do not delay interrupt/cancel.
        return new(KeyBindingResolutionStatus.Matched, winners[0], winners);
    }
}

/// <summary>Per-client chord state. Context changes/timeouts discard prefixes; a failed chord retries the last key alone.</summary>
public sealed class KeyBindingSession(KeyBindingService service, TimeSpan timeout)
{
    private readonly TimeSpan _timeout = timeout > TimeSpan.Zero && timeout <= TimeSpan.FromSeconds(10)
        ? timeout : throw new ArgumentOutOfRangeException(nameof(timeout));
    private readonly List<KeyChord> _prefix = [];
    private KeyBindingContext? _context;
    private DateTimeOffset _last;
    public void Reset() { _prefix.Clear(); _context = null; }
    public KeyBindingResolution Push(KeyChord key, KeyBindingContext context, DateTimeOffset now)
    {
        if (_context is not null && (!_context.SameAs(context) || now < _last || now - _last >= _timeout)) Reset();
        _context = context; _last = now; _prefix.Add(key);
        var result = service.Resolve(_prefix, context);
        if (result.Status == KeyBindingResolutionStatus.None && _prefix.Count > 1)
        { _prefix.Clear(); _prefix.Add(key); result = service.Resolve(_prefix, context); }
        if (result.Status != KeyBindingResolutionStatus.Pending) Reset();
        return result;
    }
}
