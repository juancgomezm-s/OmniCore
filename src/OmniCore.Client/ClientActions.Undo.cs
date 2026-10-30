namespace OmniCore.Client;

using OmniCore.Protocol;

public sealed partial class ClientActionHandler
{
    private static ClientActionResult Undo(ClientActionInvocation invocation, ClientContext context, CancellationToken token)
    {
        var args = invocation.Arguments;
        try
        {
            if (args.Count == 0 || args.Count == 1 && Guid.TryParseExact(args[0], "D", out _))
            {
                var json = context.Client.Query(args.Count == 0 ? UndoProtocol.Query : UndoProtocol.QueryFor(args[0]), token)?.Json;
                var review = json is null ? null : UndoProtocol.Decode(json);
                return new(UndoPresentation.Format(review, context.Text));
            }
            if (args.Count != 4 || args[0] != "apply" || args[3] != "--confirm"
                || !Guid.TryParseExact(args[1], "D", out _) || args[2].Length != 64 || !args[2].All(Uri.IsHexDigit)
                || context.Client is not ITrustedUserActionClient trusted)
                return new(context.Text.Resolve("undo.confirm_required"));
            var current = UndoProtocol.Decode(context.Client.Query(UndoProtocol.QueryFor(args[1]), token)?.Json ?? "null");
            if (current?.SessionId is null || current.Revision != args[2]) return new(context.Text.Resolve("undo.stale"));
            var payload = "{" + JsonObj.Field("cmd", UndoProtocol.Apply) + "," + JsonObj.Field("sessionId", current.SessionId)
                + "," + JsonObj.Field("effectId", args[1]) + "," + JsonObj.Field("revision", args[2])
                + "," + JsonObj.Field("confirmed", "true") + ",\"expectedRunId\":"
                + (current.RunId is null ? "null" : "\"" + current.RunId + "\"") + "}";
            var result = trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), payload), token);
            if (result.Status == "ok" && current.RecoveryRequired)
            {
                var recovered = UndoProtocol.Decode(context.Client.Query(UndoProtocol.QueryFor(args[1]), token)?.Json ?? "null");
                return new(context.Text.Resolve(recovered?.Outcome == "Unknown" ? "undo.recovery_unknown" : "undo.recovery_done"));
            }
            return new(context.Text.ResolveWire(result.Status == "ok" ? "undo.done" : result.Error ?? "undo.unknown"));
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException
            or System.Text.Json.JsonException or UnauthorizedAccessException)
        { return new(context.Text.Resolve("undo.unavailable")); }
    }
}

public static class UndoPresentation
{
    public static string Format(UndoReview? review, Localization text)
    {
        if (review?.Error is { } error) return text.ResolveWire(error);
        if (review?.RecoveryRequired == true) return text.Resolve("undo.recovery_required") + "\n\n"
            + string.Join("\n", review.Files.Select(file => SessionPreferencesPresentation.SafeText(file.Path) + " · " + text.Resolve("undo.state." + file.State)))
            + "\n\n" + review.Revision;
        if (review is null || review.Files.Count == 0) return text.Resolve("undo.empty");
        return text.Resolve("undo.help") + "\n\n" + string.Join("\n", review.Files.Select(file =>
            file.EffectId + "\n  " + SessionPreferencesPresentation.SafeText(file.Path)
            + "\n    " + text.Resolve("undo.operation." + file.Operation) + " · " + text.Resolve("undo.state." + file.State)
            + " · " + file.RestoreBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + " B"
            + "\n" + (file.Diff is { Available: true } diff
                ? string.Join("\n", FileDiffPresentation.Build(diff, maximumRows: 120, locale: text.Locale).Rows
                    .Select(row => "    " + SessionPreferencesPresentation.SafeText(row.Text, 512)))
                : "    " + text.Resolve("undo.diff.unavailable"))))
            + "\n\n" + review.Revision;
    }
}
