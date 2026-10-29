using System.Security.Cryptography;
using System.Text;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Host;

public sealed partial class OmniServer
{
    /// <summary>Creates a durable blank conversation without selecting it or starting a provider.</summary>
    public CompanionCreatedSession CreateCompanionSession(string title, string operationId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 120 || title.Any(char.IsControl) || !Guid.TryParse(operationId, out _))
            throw new ArgumentException("Nombre de sesión inválido (1–120 caracteres).");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("companion.session/" + operationId));
        var target = SessionId.Parse(new Guid(bytes[..16]).ToString());
        using var detachedAttribution = ExecutionScope.Begin(new());
        lock (_modeAuthorityMutationGate)
        {
            token.ThrowIfCancellationRequested();
            var owned = _store as SqliteEventStore;
            var acquired = owned?.AcquireSessionWriterLease(target) == true;
            try
            {
                var history = _store.ReadFrom(target, 1);
                var previous = history.Select(_codecs.Decode).OfType<SessionTitleChanged>().LastOrDefault();
                if (previous is not null && previous.Title != title) throw new InvalidOperationException("La creación ya tiene otro nombre.");
                if (history.Count == 0) StartSession(target);
                if (previous is null)
                    new EventStream(_store, _codecs, target).Append(new SessionTitleChanged(target, title, _store.CurrentSequence(target)), DurabilityClass.Barrier);
                return new(target.ToString(), title, _workspaceRoot ?? Path.GetFullPath("."));
            }
            finally { if (acquired) owned!.ReleaseSessionWriterLease(target); }
        }
    }
}
