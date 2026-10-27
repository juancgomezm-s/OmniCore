using OmniCore.Client;
using OmniCore.Protocol;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    private IReadOnlyDictionary<string, CommandPaletteEntry> _hostKeyBindingCommands =
        new Dictionary<string, CommandPaletteEntry>(StringComparer.Ordinal);

    private IReadOnlyDictionary<string, CommandPaletteEntry> ReadHostKeyBindingCommands(CancellationToken cancellationToken) =>
        CommandPalette.Build(_client.Query("commands", cancellationToken)?.Json, _localization, new([]), false)
            .Where(entry => entry.Target.Kind == KeyBindingTargetKind.Command && !string.IsNullOrWhiteSpace(entry.Source))
            .ToDictionary(entry => entry.Target.Id, StringComparer.Ordinal);

    private ExtensionKeyBindingSnapshot ReadExtensionKeyBindings(CancellationToken cancellationToken) =>
        ExtensionKeyBindingProtocol.Decode(_client.Query("keybindings.extensions", cancellationToken)?.Json
            ?? throw new InvalidOperationException("extension_keybindings_unavailable"));

    private sealed class TuiHostCommandHandler(TuiApp app, KeyBindingResolution resolved) : IClientCommandHandler
    {
        public ValueTask<ClientActionResult> ExecuteAsync(CommandInvocation invocation, ClientContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Keyboard commands cannot dismiss a form or the server's pending approval.
            if (app._overlay is not null || app._activeInteraction is not null) return ValueTask.FromResult(new ClientActionResult(""));
            var session = app._cursorSession; var run = app._sidebarProjection.Snapshot?.RunId;
            app.PollEvents();
            if (app._overlay is not null || app._activeInteraction is not null) return ValueTask.FromResult(new ClientActionResult(""));
            var binding = resolved.Binding!;
            var currentResolution = app._keyBindings.Service.Resolve(binding.Sequence, app.KeyboardContext());
            var expectedExtensionBinding = binding.Definition.Source == KeyBindingSource.Extension
                ? app._extensionKeyBindingSnapshot.Bindings.FirstOrDefault(candidate =>
                    candidate.Command == invocation.Name && candidate.Source == binding.Definition.SourceId
                    && candidate.Keys == binding.Definition.Keys
                    && candidate.Arguments.SequenceEqual(binding.Definition.Target.Arguments, StringComparer.Ordinal)
                    && binding.Definition.When == "input && !overlayOpen && !awaitingPermission && (" + candidate.When + ")")
                : null;
            if (session != app._cursorSession || run != app._sidebarProjection.Snapshot?.RunId || app.HasActiveModelWork
                || currentResolution.Status != KeyBindingResolutionStatus.Matched || !ReferenceEquals(currentResolution.Binding, binding)
                || !app._hostKeyBindingCommands.TryGetValue(invocation.Name, out var expected)
                || binding.Definition.Source == KeyBindingSource.Extension && expectedExtensionBinding is null)
            { app.ShowMessage(app.KeyBindingText("host_unavailable")); return ValueTask.FromResult(new ClientActionResult("")); }
            CommandPaletteEntry? current;
            try
            {
                var commands = app.ReadHostKeyBindingCommands(cancellationToken);
                if (!commands.TryGetValue(invocation.Name, out current) || !current.Enabled
                    || current.Source != expected.Source || current.SourceFingerprint != expected.SourceFingerprint
                    || string.IsNullOrEmpty(expected.SourceFingerprint) && current.Description != expected.Description
                    || !(current.ArgumentHints ?? []).SequenceEqual(expected.ArgumentHints ?? []))
                { app.ShowMessage(app.KeyBindingText("host_unavailable")); return ValueTask.FromResult(new ClientActionResult("")); }
                if (expectedExtensionBinding is not null)
                {
                    var currentBindings = app.ReadExtensionKeyBindings(cancellationToken);
                    if (!currentBindings.Bindings.Any(candidate => candidate.BindingFingerprint == expectedExtensionBinding.BindingFingerprint
                        && candidate.Source == expectedExtensionBinding.Source && candidate.Command == invocation.Name
                        && candidate.Keys == expectedExtensionBinding.Keys
                        && candidate.Arguments.SequenceEqual(invocation.Arguments, StringComparer.Ordinal)))
                    { app.ShowMessage(app.KeyBindingText("host_unavailable")); return ValueTask.FromResult(new ClientActionResult("")); }
                }
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException
                or System.Text.Json.JsonException)
            { app.ShowMessage(app.KeyBindingText("host_unavailable")); return ValueTask.FromResult(new ClientActionResult("")); }
            try { app.RunHostCommand(invocation, cancellationToken); app.PollEvents(); }
            catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
            { app.ShowMessage(app.KeyBindingText("host_result_uncertain")); }
            return ValueTask.FromResult(new ClientActionResult(""));
        }
    }
}
