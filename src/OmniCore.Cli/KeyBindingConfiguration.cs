using OmniCore.Client;
using OmniCore.Host;

namespace OmniCore.Cli;

public sealed record KeyBindingSourceDiagnostic(string Code, string Source, int Line, int Column, string Subject, string? Detail = null);
public sealed record ConfiguredKeyBindings(KeyBindingService Service, KeyBindingConfigurationSnapshot Configuration,
    IReadOnlyList<KeyBindingSourceDiagnostic> Diagnostics);

/// <summary>Typed User-configuration adapter. Does not intercept TUI input or change existing shortcuts.</summary>
public static class KeyBindingConfiguration
{
    public static ConfiguredKeyBindings Load(CancellationToken cancellationToken,
        string? dataDirectoryOverride = null, string? configDirectoryOverride = null,
        IEnumerable<KeyBindingDefinition>? extensions = null, KeyBindingTargetCatalog? catalog = null)
    {
        var snapshot = KeyBindingConfigurationReader.LoadUser(dataDirectoryOverride, configDirectoryOverride, cancellationToken);
        var bindings = snapshot.Bindings.Select(binding =>
        {
            var remove = binding.Action?.StartsWith('-') == true;
            var target = new KeyBindingTarget(binding.Action is not null ? KeyBindingTargetKind.Action : KeyBindingTargetKind.Command,
                remove ? binding.Action![1..] : binding.Action ?? binding.Command!, binding.Arguments);
            return new KeyBindingDefinition(binding.Keys ?? "", target, binding.When ?? "true", binding.Priority,
                KeyBindingSource.User, "User", remove);
        });
        cancellationToken.ThrowIfCancellationRequested();
        var service = new KeyBindingService((extensions ?? []).Select(binding => binding with { Source = KeyBindingSource.Extension })
            .Concat(bindings), catalog);
        var diagnostics = snapshot.Issues.Select(issue => new KeyBindingSourceDiagnostic(issue.Code, snapshot.Path,
            issue.Line, issue.Column, issue.Field ?? snapshot.Path)).ToList();
        foreach (var issue in service.Diagnostics)
        {
            var entry = issue.SourceId == "User" || issue.Code == "protected_restored"
                ? snapshot.Bindings.FirstOrDefault(binding => issue.Subject == binding.Keys
                    || issue.Subject == (binding.Action ?? binding.Command)?.TrimStart('-')
                    || issue.Subject == (binding.Keys ?? "") + " → " + (binding.Action ?? binding.Command)) : null;
            diagnostics.Add(new(issue.Code, entry is null ? issue.SourceId : snapshot.Path,
                entry?.Line ?? 0, entry?.Column ?? 0, issue.Subject, issue.Detail));
        }
        return new(service, snapshot, diagnostics.AsReadOnly());
    }
}
