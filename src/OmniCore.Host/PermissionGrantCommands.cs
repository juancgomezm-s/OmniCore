namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Security;

/// <summary>Comandos tipados de administración de grants. El Engine nunca interpreta texto slash.</summary>
public abstract record PermissionGrantCommand;
public sealed record ListPermissionGrantsCommand : PermissionGrantCommand;
public sealed record RevokePermissionGrantCommand(string GrantId) : PermissionGrantCommand;

public sealed record PermissionGrantSummary(string Id, string ToolId, string ClaimsKey, string Lifetime, string? Run);

public sealed record PermissionGrantCommandResult(
    IReadOnlyList<PermissionGrantSummary> Grants,
    bool Revoked,
    string? GrantId);

/// <summary>Handler del Host para listar/revocar grants de un workspace concreto.</summary>
public sealed class PermissionGrantCommandHandler
{
    private readonly IPermissionGrantStore _store;
    private readonly WorkspaceId _workspace;
    private readonly RunId? _run;

    public PermissionGrantCommandHandler(IPermissionGrantStore store, WorkspaceId workspace, RunId? run = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _run = run;
    }

    public PermissionGrantCommandResult Handle(PermissionGrantCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        return command switch
        {
            ListPermissionGrantsCommand => new PermissionGrantCommandResult(
                _store.List(_workspace, _run).Select(g => new PermissionGrantSummary(g.Id.ToString(), g.ToolId,
                    g.ClaimsKey, g.Lifetime.ToString(), g.Run?.ToString())).ToArray(), false, null),
            RevokePermissionGrantCommand revoke => Revoke(revoke, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
    }

    private PermissionGrantCommandResult Revoke(RevokePermissionGrantCommand command,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(command.GrantId, out var value))
            throw new FormatException("GrantId inválido.");
        var id = new GrantId(value);
        return new PermissionGrantCommandResult(Array.Empty<PermissionGrantSummary>(),
            _store.Revoke(_workspace, id, cancellationToken), id.ToString());
    }
}
