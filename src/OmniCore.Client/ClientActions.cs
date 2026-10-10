namespace OmniCore.Client;

using OmniCore.Protocol;

/// <summary>Ids de las <c>ClientAction</c>s (ADR-0025 §1): la unidad de comportamiento del cliente.</summary>
public static class ClientActionIds
{
    public const string HelpShow = "help.show";
    public const string AppExit = "app.exit";
    public const string ViewPlan = "view.plan";
    public const string ViewTasks = "view.tasks";
    public const string ViewEvents = "view.events";
    public const string ContextInspect = "context.inspect";
    public const string ToolsInspect = "tools.inspect";
    public const string PermissionsList = "permissions.list";
    public const string RunInterrupt = "run.interrupt";
    public const string RunCancel = "run.cancel";
}

/// <summary>Una acción del cliente: su título es una clave de localización (ADR-0040).</summary>
public sealed record ClientActionDescriptor(string Id, string TitleKey);

/// <summary>
/// Superficie textual de una acción (ADR-0024 §2, <c>ClientCommand</c>): <c>/plan</c>, <c>/tasks</c>…
/// Un command es solo un nombre para una acción; no existe una segunda lógica (ADR-0025 §1).
/// </summary>
public sealed record ClientCommandDescriptor(string Name, string ActionId, string DescriptionKey);

/// <summary>Invocación tipada de una acción: lo que el cliente ejecuta tras resolver un command, tecla o paleta.</summary>
public sealed record ClientActionInvocation(string ActionId, IReadOnlyList<string> Arguments, string Origin);

/// <summary>Lo que una acción necesita del cliente que la ejecuta. Nunca incluye tipos de UI.</summary>
/// <param name="Client">Frontera con el servidor (ADR-0019): comandos y consultas, nada más.</param>
/// <param name="Text">Recursos de localización activos.</param>
/// <param name="Permissions">Listado de grants del workspace; solo lo aporta un cliente que lo tiene a mano.</param>
public sealed record ClientContext(IOmniClient Client, Localization Text, Func<string>? Permissions = null);

/// <summary>Resultado de una acción: texto para mostrar y, si procede, la petición de cerrar el cliente.</summary>
public sealed record ClientActionResult(string Text, bool ExitRequested = false);

/// <summary>Ejecuta una <see cref="ClientActionInvocation"/> contra el servidor (ADR-0025 §1).</summary>
public interface IClientActionHandler
{
    ValueTask<ClientActionResult> ExecuteAsync(ClientActionInvocation invocation, ClientContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// <c>ClientCommandRegistry</c> mínimo de M1 (ADR-0024 §3, ADR-0025): resuelve el texto <c>/nombre</c> a una
/// <see cref="ClientActionInvocation"/>. El Engine nunca interpreta <c>/</c> (invariante 19); estos nombres
/// están además reservados en el Host para que ninguna otra fuente los registre (ADR-0024 §4).
/// </summary>
public sealed class ClientCommandRegistry
{
    public static ClientCommandRegistry Default { get; } = new(
        [
            new("help", ClientActionIds.HelpShow, "client.command.help"),
            new("exit", ClientActionIds.AppExit, "client.command.exit"),
            new("plan", ClientActionIds.ViewPlan, "client.command.plan"),
            new("tasks", ClientActionIds.ViewTasks, "client.command.tasks"),
            new("events", ClientActionIds.ViewEvents, "client.command.events"),
            new("context", ClientActionIds.ContextInspect, "client.command.context"),
            new("tools", ClientActionIds.ToolsInspect, "client.command.tools"),
            new("permissions", ClientActionIds.PermissionsList, "client.command.permissions"),
            new("interrupt", ClientActionIds.RunInterrupt, "client.command.interrupt"),
            new("cancel", ClientActionIds.RunCancel, "client.command.cancel"),
        ]);

    private readonly Dictionary<string, ClientCommandDescriptor> _byName;

    public ClientCommandRegistry(IReadOnlyList<ClientCommandDescriptor> commands)
    {
        Commands = commands;
        _byName = new Dictionary<string, ClientCommandDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in commands)
            if (!_byName.TryAdd(command.Name, command))
                throw new InvalidOperationException("Duplicate client command: " + command.Name);
        Actions = commands.Select(command => command.ActionId).Distinct(StringComparer.Ordinal)
            .Select(id => new ClientActionDescriptor(id, "client.action." + id)).ToArray();
    }

    public IReadOnlyList<ClientCommandDescriptor> Commands { get; }

    public IReadOnlyList<ClientActionDescriptor> Actions { get; }

    /// <summary>Resuelve una invocación de slash command; false si no es un command del cliente.</summary>
    public bool TryResolve(CommandInvocation invocation, out ClientActionInvocation? action)
    {
        action = null;
        if (invocation is null || !_byName.TryGetValue(invocation.Name, out var command)) return false;
        action = new ClientActionInvocation(command.ActionId, invocation.Arguments, invocation.InvocationOrigin);
        return true;
    }
}

/// <summary>
/// Handler por defecto: las vistas de solo lectura (consultas al servidor) y las dos acciones que emiten un
/// <c>WireCommand</c>, la misma ruta que cualquier otra superficie (ADR-0025 §1). La TUI intercepta
/// <c>run.cancel</c> y <c>run.interrupt</c> antes para cancelar también el Turn que ella lleva en curso.
/// </summary>
public sealed class ClientActionHandler : IClientActionHandler
{
    private const int MaxMessageLength = 1800;
    private const int RecentEvents = 20;

    public ValueTask<ClientActionResult> ExecuteAsync(ClientActionInvocation invocation, ClientContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(invocation.ActionId switch
        {
            ClientActionIds.HelpShow => Help(context),
            ClientActionIds.AppExit => new ClientActionResult(context.Text.Resolve("client.exit.bye"), ExitRequested: true),
            ClientActionIds.ViewPlan => Query(context, "workingState"),
            ClientActionIds.ContextInspect => Query(context, "context"),
            ClientActionIds.ToolsInspect => Query(context, "tools"),
            ClientActionIds.ViewTasks => Tasks(context, cancellationToken),
            ClientActionIds.ViewEvents => Events(context),
            ClientActionIds.PermissionsList => new ClientActionResult(context.Permissions?.Invoke()
                ?? context.Text.Resolve("client.permissions.unavailable")),
            ClientActionIds.RunInterrupt => Send(context, "run.interrupt"),
            ClientActionIds.RunCancel => Send(context, "run.cancel"),
            _ => throw new InvalidOperationException("Unknown client action: " + invocation.ActionId),
        });
    }

    private static ClientActionResult Help(ClientContext context)
    {
        var lines = new List<string> { context.Text.Resolve("client.help.title") };
        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in ClientCommandRegistry.Default.Commands)
        {
            shown.Add(command.Name);
            lines.Add("  /" + command.Name + " — " + context.Text.Resolve(command.DescriptionKey));
        }
        // Los commands del Host (ADR-0024 §3) vienen del mismo catálogo que usa el autocompletado.
        foreach (var entry in HostCommands(context))
            if (shown.Add(entry.Name)) lines.Add("  /" + entry.Name + " — " + entry.Description);
        lines.Add(context.Text.Resolve("client.help.footer"));
        return new ClientActionResult(string.Join("\n", lines));
    }

    private static IEnumerable<(string Name, string Description)> HostCommands(ClientContext context)
    {
        var json = context.Client.Query("commands", CancellationToken.None)?.Json;
        if (string.IsNullOrWhiteSpace(json)) yield break;
        System.Text.Json.JsonDocument? document = null;
        try { document = System.Text.Json.JsonDocument.Parse(json); }
        catch (System.Text.Json.JsonException) { }
        using (document)
        {
            if (document is null || !document.RootElement.TryGetProperty("catalog", out var catalog)
                || !catalog.TryGetProperty("commands", out var commands)
                || commands.ValueKind != System.Text.Json.JsonValueKind.Array) yield break;
            foreach (var command in commands.EnumerateArray())
            {
                if (!command.TryGetProperty("name", out var name) || name.GetString() is not { Length: > 0 } text) continue;
                var description = command.TryGetProperty("description", out var value) ? value.GetString() ?? "" : "";
                yield return (text, description);
            }
        }
    }

    private static ClientActionResult Query(ClientContext context, string name)
    {
        var json = context.Client.Query(name, CancellationToken.None)?.Json;
        if (json is null) return new ClientActionResult(context.Text.Resolve("client.no_data"));
        return new ClientActionResult(json.Length <= MaxMessageLength ? json : json[..MaxMessageLength] + "…");
    }

    private static ClientActionResult Tasks(ClientContext context, CancellationToken cancellationToken)
    {
        var json = context.Client.Query("agents", cancellationToken)?.Json;
        var snapshot = json is null || json == "null" ? null : AgentsJson.Decode(json);
        return new ClientActionResult(AgentPresentation.Describe(snapshot, context.Text.Locale));
    }

    private static ClientActionResult Events(ClientContext context)
    {
        var lines = new List<string>();
        foreach (var envelope in context.Client.SubscribeSince(0))
        {
            if (envelope.MessageType != MessageTypes.Event) continue;
            var fields = JsonObj.Parse(envelope.PayloadJson);
            if (fields.TryGetValue("type", out var type))
                lines.Add((fields.TryGetValue("seq", out var seq) ? seq : "-") + "  " + type);
        }
        if (lines.Count == 0) return new ClientActionResult(context.Text.Resolve("client.events.empty"));
        var recent = lines.Skip(Math.Max(0, lines.Count - RecentEvents)).ToArray();
        return new ClientActionResult(context.Text.Resolve("client.events.heading",
                new Dictionary<string, string> { ["count"] = recent.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["total"] = lines.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            + "\n" + string.Join("\n", recent));
    }

    private static ClientActionResult Send(ClientContext context, string command)
    {
        var ack = context.Client.Send(WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", command) + "}"),
            CancellationToken.None);
        return new ClientActionResult(ack.Status == "ok"
            ? context.Text.Resolve("client.command.sent", "command", command)
            : context.Text.ResolveWire(ack.Error ?? command));
    }
}
