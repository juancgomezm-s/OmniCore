namespace OmniCore.Client;

using System.Text.Json;
using OmniCore.Protocol;

public sealed partial class ClientActionHandler
{
    private static ClientActionResult ScopedMemory(ClientActionInvocation invocation, ClientContext context)
    {
        var scope = invocation.Arguments[0];
        var queryName = "memory." + scope;
        if (invocation.Arguments.Count == 1 || invocation.Arguments is [_, "candidates"])
            return Query(context, queryName);
        if (invocation.Arguments.Count < 3 || invocation.Arguments[1] is not ("add" or "propose" or "promote" or "reject" or "forget")
            || invocation.Arguments[1] is "promote" or "reject" or "forget" && invocation.Arguments.Count != 3)
            return new(context.Text.Resolve("client.memory.scoped_usage"));
        if (context.Client is not ITrustedUserActionClient trusted)
            return new(context.Text.Resolve("client.memory.trusted_required"));
        var json = context.Client.Query(queryName, CancellationToken.None)?.Json;
        if (json is null) return new(context.Text.Resolve("client.no_data"));
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("available", out var available) || available.ValueKind != JsonValueKind.True)
                return new(root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                    ? context.Text.ResolveWire(error.GetString()!) : context.Text.Resolve("client.no_data"));
            var operation = invocation.Arguments[1] == "add" ? "propose" : invocation.Arguments[1];
            var field = operation switch { "propose" => "content", "forget" => "memoryId", _ => "candidateId" };
            var value = operation == "propose" ? string.Join(" ", invocation.Arguments.Skip(2)) : invocation.Arguments[2];
            var payload = "{" + JsonObj.Field("cmd", "memory.scoped." + operation) + "," + JsonObj.Field("scope", scope)
                + "," + JsonObj.Field(field, value) + "," + JsonObj.Field("expectedSessionId", root.GetProperty("session").GetString() ?? "")
                + "," + JsonObj.Field("expectedScopeId", root.GetProperty("scopeId").GetString() ?? "")
                + "," + JsonObj.Field("expectedRevision", root.GetProperty("revision").GetString() ?? "") + "}";
            var ack = trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), payload), CancellationToken.None);
            if (ack.Status != "ok") return new(ack.Error is { } message
                ? context.Text.ResolveWire(message) : context.Text.Resolve("client.memory.rejected"));
            var updated = context.Client.Query(queryName, CancellationToken.None)?.Json;
            if (updated is null) return new(context.Text.Resolve("client.no_data"));
            var result = new ClientActionResult(MemoryInspectionPresentation.Format(updated, context.Text));
            using var current = JsonDocument.Parse(updated);
            var needsConfirmation = operation == "propose"
                && current.RootElement.TryGetProperty("candidates", out var candidates)
                && candidates.EnumerateArray().Any(item => item.GetProperty("decision").GetString() == "ask"
                    && item.GetProperty("content").GetString() == value.Trim());
            return needsConfirmation ? new(context.Text.Resolve("client.memory.confirm_hint") + "\n" + result.Text) : result;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return new(context.Text.Resolve("client.no_data"));
        }
    }
}
