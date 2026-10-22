using System.Text.Json;
using OmniCore.Domain;
using OmniCore.Protocol;

namespace OmniCore.Host;

public sealed partial class OmniServer
{
    private readonly Queue<(string Workspace, ExtensionNotification Notice)> _extensionNotifications = new();
    private long _extensionNotificationSequence;
    private readonly Dictionary<string, (DateTimeOffset Window, int Count)> _extensionNoticeRates = new(StringComparer.Ordinal);

    private JsonElement PublishExtensionNotification(ExtensionPackageState source, string? session, JsonElement input)
    {
        var id = input.GetProperty("id").GetString(); var level = input.GetProperty("level").GetString();
        var text = input.GetProperty("text").GetString();
        if (_workspaceRoot is null || id is not { Length: > 0 and <= 128 } || id.Any(char.IsControl)
            || level is not ("info" or "warning" or "error") || text is not { Length: > 0 and <= 2048 }
            || text.Any(character => char.IsControl(character) && character is not ('\n' or '\t')))
            throw new ArgumentException("extension.notification.invalid");
        var workspace = WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(_workspaceRoot)).ToString();
        lock (_extensionNotifications)
        {
            var prior = _extensionNotifications.LastOrDefault(item => item.Workspace == workspace && item.Notice.SessionId == session
                && item.Notice.ExtensionId == source.Id && item.Notice.PackageSha256 == source.PackageSha256 && item.Notice.Id == id).Notice;
            if (prior is not null) return JsonSerializer.SerializeToElement(new ExtensionNotificationPage(prior.Sequence, false, []), ExtensionJson.Default.ExtensionNotificationPage);
            var now = DateTimeOffset.UtcNow; var rate = _extensionNoticeRates.GetValueOrDefault(source.Id);
            if (now - rate.Window > TimeSpan.FromMinutes(1)) rate = (now, 0);
            if (rate.Count >= 20) throw new InvalidOperationException("extension.notification.rate_limit");
            if (_extensionNoticeRates.Count >= 128 && !_extensionNoticeRates.ContainsKey(source.Id)) _extensionNoticeRates.Remove(_extensionNoticeRates.Keys.First());
            _extensionNoticeRates[source.Id] = (rate.Window, rate.Count + 1);
            var notification = new ExtensionNotification(++_extensionNotificationSequence, id, source.Id, session, level,
                new RedactionPolicy().Redact(text), source.ManifestSha256, source.PackageSha256);
            _extensionNotifications.Enqueue((workspace, notification));
            while (_extensionNotifications.Count > 64) _extensionNotifications.Dequeue();
            return JsonSerializer.SerializeToElement(new ExtensionNotificationPage(notification.Sequence, false, []), ExtensionJson.Default.ExtensionNotificationPage);
        }
    }

    private SessionQueryResult QueryExtensionNotifications(string name)
    {
        var text = name[(name.IndexOf(':') + 1)..];
        if (!long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var after) || after < 0)
            throw new ArgumentException("extension.notification.cursor_invalid");
        var workspace = _workspaceRoot is null ? "" : WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(_workspaceRoot)).ToString();
        lock (_extensionNotifications)
        {
            if (after > _extensionNotificationSequence) throw new ArgumentException("extension.notification.cursor_invalid");
            var items = _extensionNotifications.Where(item => item.Workspace == workspace && item.Notice.SessionId == _lastSessionId?.ToString()
                && item.Notice.Sequence > after).Select(item => item.Notice).ToArray();
            var gap = _extensionNotifications.Count == 64 && after < _extensionNotifications.Peek().Notice.Sequence - 1;
            return new(name, JsonSerializer.Serialize(new ExtensionNotificationPage(_extensionNotificationSequence, gap, items), ExtensionJson.Default.ExtensionNotificationPage));
        }
    }
}
