using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Protocol;

namespace OmniCoreLiveAcceptance;

internal sealed partial class AcceptanceRunner
{
    // These are user intents, not tool instructions or a substitute production command tool.
    // Expected names are private grading data and are not sent next to the test questions.
    private static readonly (string Name, string Intent)[] CommandIntents =
    [
        ("help", "Muéstrame la ayuda general de los comandos disponibles."),
        ("exit", "Quiero cerrar el cliente interactivo."),
        ("preferences", "Abre el centro de preferencias de la interfaz."),
        ("tools", "Inspecciona el catálogo de herramientas disponibles y sus permisos."),
        ("models", "Abre el selector interactivo de modelos, no el comando CLI de administración."),
        ("model", "Usa la administración CLI para listar los modelos registrados."),
        ("tasks", "Muéstrame las tareas y ejecuciones de agentes en el panel de la sesión."),
        ("events", "Muéstrame los eventos recientes de la sesión."),
        ("context", "Inspecciona el contexto que acompaña a la conversación."),
        ("commands", "Inspecciona el catálogo de comandos registrados y sus fuentes."),
        ("permissions", "Lista los permisos concedidos al proyecto."),
        ("skills", "Lista las habilidades descubiertas, sin activar ninguna."),
        ("memory", "Muéstrame los registros de memoria de la sesión, sin añadir ni borrar nada."),
        ("extensions", "Lista las extensiones registradas del proyecto."),
        ("cancel", "Cancela por completo el Run actual de prueba."),
        ("interrupt", "Interrumpe el Run de prueba para que espere nueva entrada."),
        ("agents", "Abre la vista interactiva de agentes disponibles."),
        ("delegate", "Quiero delegar una tarea nueva a un agente hijo."),
        ("agent", "Quiero gestionar un agente individual usando su identificador."),
        ("join", "Quiero esperar y recoger el resultado de una delegación existente."),
        ("mode", "Cambia el modo de la conversación mediante el comando corto interactivo."),
        ("runmode", "Quiero usar el comando explícito del modo de ejecución del Run."),
        ("reasoning", "Quiero elegir el esfuerzo de razonamiento del modelo."),
        ("animation", "Quiero cambiar el indicador animado de actividad."),
        ("sidebar", "Quiero configurar la visibilidad del panel lateral."),
        ("fuente", "Quiero ajustar la fuente tipográfica de la terminal."),
        ("login", "Quiero iniciar la autenticación de una cuenta ChatGPT."),
        ("logout", "Quiero desconectar una cuenta ChatGPT."),
        ("doctor", "Quiero ejecutar el diagnóstico general de la instalación."),
        ("trust", "Quiero marcar como confiable un proyecto temporal que revisé."),
        ("resolve", "Quiero ver y contestar una interacción pendiente del Host."),
        ("session", "Quiero administrar la purga de una sesión temporal por identificador."),
        ("worktree", "Quiero inspeccionar los worktrees del proyecto."),
        ("ask", "Quiero hacer una consulta de exploración al modelo desde la CLI."),
        ("act", "Quiero encargar una tarea con cambios al modelo desde la CLI."),
        ("plan", "Muéstrame el plan actual de la sesión."),
        ("explain", "Quiero explicar el contexto de README.md mediante el comando principal de explicación."),
        ("orq-auth", "Quiero gestionar la autorización explícita del modo de orquestación."),
        ("explore", "Quiero usar el alias heredado de exploración de README.md, no el comando principal explain."),
        ("resume", "Quiero reanudar un turno suspendido."),
        ("fanout", "Quiero repartir un encargo a varios agentes en paralelo."),
        ("verify-journal", "Quiero verificar la integridad del journal."),
        ("audit", "Quiero administrar la retención y purga del registro de auditoría."),
        ("gc", "Quiero recolectar artefactos sin referencias y vencidos."),
        ("tui", "Quiero iniciar la interfaz de terminal de OmniCore."),
    ];

    public async Task<IReadOnlyList<CommandEvidence>> RunCommandsAsync(Options options,
        PreflightReport preflight, string evidenceDirectory)
    {
        if (!CommandIntents.Select(item => item.Name).Order().SequenceEqual(
            HumanCommandAliases.Entries.Select(item => item.Canonical).Order()))
            throw new InvalidOperationException("command_intent_inventory_drift");
        var provider = ConnectModelProvider(options.Model, preflight.ModelId);
        var selection = new ModelSelection(new ModelIdValue(preflight.ModelId), preflight.ContextWindow!.Value,
            ToolMode.Direct, reasoning: null, maxOutputTokens: preflight.RequestMaxOutputTokens);
        var textResources = Localization.Spanish();
        var menu = CommandCatalogPresentation.Read(null, textResources);
        var catalog = HumanCommandAliases.Entries.Select(entry => new
        {
            name = entry.Canonical, es = entry.Spanish, fr = entry.French, it = entry.Italian,
            description = (entry.Canonical switch
                {
                    "model" => textResources.Resolve("cli.usage.model"),
                    "ask" => textResources.Resolve("cli.usage.ask"),
                    "act" => textResources.Resolve("cli.usage.act"),
                    "explain" or "explore" => textResources.Resolve("cli.usage.explain")
                        + " | " + textResources.Resolve("commands.explain.help"),
                    _ => null,
                }) ?? menu.FirstOrDefault(c => c.Name == entry.Canonical)?.Description,
        }).ToArray();
        var rows = new List<CommandEvidence>();
        var generationCount = 0;
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var fixture = Path.Combine(evidenceDirectory, "command-fixture");
        Directory.CreateDirectory(fixture);

        foreach (var batch in CommandIntents.Select((item, index) => (item, index)).Chunk(15))
        {
            var prompt = JsonSerializer.Serialize(new { catalog,
                tasks = batch.Select(item => new { id = "c" + item.index, intent = item.item.Intent }) });
            var request = new ModelRequest(selection, [new ModelMessage(MessageRole.User, [new TextBlock(prompt)])],
                "Choose the correct OmniCore command for each Spanish intent using the real alias catalog. "
                + "Respond ONLY with a JSON array of objects {id, command, arguments}, where command is its canonical name "
                + "and arguments is an array of strings. Commands are client/CLI actions, not native model tools. "
                + "Do not claim execution. For memory listing use arguments [\"session\"]. For client view/control actions use [].",
                [], ToolChoice.None(), null, null, null, null);
            var started = DateTimeOffset.UtcNow;
            using var caseTimeout = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
            caseTimeout.CancelAfter(TimeSpan.FromSeconds(120));
            var response = await GetResponseAsync(provider, request, caseTimeout.Token, () => generationCount++);
            var text = string.Concat(response.Content.OfType<TextBlock>().Select(block => block.Text)).Trim();
            if (text.StartsWith("```", StringComparison.Ordinal))
                text = text[(text.IndexOf('\n') + 1)..text.LastIndexOf("```", StringComparison.Ordinal)].Trim();
            using var parsed = JsonDocument.Parse(text);
            if (parsed.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("command_response_not_array");
            foreach (var (item, index) in batch)
            {
                var matching = parsed.RootElement.EnumerateArray().Where(value => value.TryGetProperty("id", out var id)
                    && id.GetString() == "c" + index).ToArray();
                var chosen = matching.Length == 1 && matching[0].TryGetProperty("command", out var name)
                    && name.ValueKind == JsonValueKind.String ? name.GetString() ?? "" : "";
                chosen = HumanCommandAliases.CanonicalName(chosen.TrimStart('/'));
                var correct = chosen == item.Name;
                var arguments = matching.Length == 1 && matching[0].TryGetProperty("arguments", out var args)
                    && args.ValueKind == JsonValueKind.Array && args.EnumerateArray().All(a => a.ValueKind == JsonValueKind.String)
                        ? args.EnumerateArray().Select(a => a.GetString()!).ToArray() : null;
                var status = "interpretation_only";
                var detail = "No client execution authority is inferred from generated text.";
                var effect = false;
                if (correct && ClientCommandRegistry.Default.Commands.Any(c => c.Name == chosen))
                {
                    var allowed = chosen == "memory" ? new[] { "session" } : Array.Empty<string>();
                    if (arguments is null || !arguments.SequenceEqual(allowed))
                    { status = "failed"; detail = "model_arguments_do_not_match_safe_readonly_or_control_case"; }
                    else
                    {
                        // Explicit test-driver dispatch, never masquerading as a model tool.
                        (effect, detail) = await ExecuteClientFixture(chosen, arguments, fixture, caseTimeout.Token);
                        status = effect ? "client_action_executed_by_fixture_driver" : "failed";
                    }
                }
                var row = new CommandEvidence(item.Name, item.Intent, chosen, arguments, correct,
                    status, effect, detail, false, started, DateTimeOffset.UtcNow, generationCount);
                rows.Add(row);
                File.WriteAllText(Path.Combine(evidenceDirectory, "command-" + item.Name + ".json"),
                    JsonSerializer.Serialize(row, JsonOptions.Indented));
            }
        }
        return rows;
    }

    private static async Task<(bool Verified, string Detail)> ExecuteClientFixture(string name, string[] arguments,
        string root, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var artifacts = new FileArtifactStore(Path.Combine(directory, "artifacts"));
        var server = new OmniServer(store, codecs, new InMemoryAuditSink(), Path.Combine(directory, "last-session"),
            artifacts, platformPathsFactory: () => new DefaultPlatformPaths(Path.Combine(directory, "data")));
        server.ConfigureWorkspaceRoot(directory);
        var seed = server.SendUserAction(WireEnvelope.Command(Ids.NewV7(),
            "{\"cmd\":\"session.input\",\"text\":\"acceptance fixture only\"}"), cancellationToken);
        if (seed.Status != "ok") return (false, "fixture_session_start_failed");
        if (!ClientCommandRegistry.Default.TryResolve(new(name, arguments, "Typed"), out var action))
            return (false, "command_not_resolved");
        var result = await new ClientActionHandler().ExecuteAsync(action!,
            new ClientContext(server, new Localization("es"), () => "[]"), cancellationToken);
        var facts = store.ReadFrom(server.LastSessionId()!, 1).Select(codecs.Decode).ToArray();
        var verified = name switch
        {
            "exit" => result.ExitRequested,
            "cancel" => facts.OfType<RunCancelled>().Any(),
            "interrupt" => facts.OfType<RunAwaitingInput>().Any(),
            "tools" => result.Tools?.Tools.Count > 0,
            "permissions" => result.Text == "[]",
            "help" => ClientCommandRegistry.Default.Commands.All(command => result.Text.Contains(
                "/" + command.Name, StringComparison.Ordinal)),
            "events" => result.Text.Contains("run.started", StringComparison.Ordinal),
            "tasks" => server.Query("agents", cancellationToken)?.Json is { } agents
                && agents != "null" && result.Text == AgentPresentation.Describe(AgentsJson.Decode(agents), "es"),
            "context" => result.Text == "{\"snapshot\":null}",
            "memory" => VerifyFixtureQuery("memory.session", "records", JsonValueKind.Array),
            "skills" => VerifyFixtureQuery("skills", "skills", JsonValueKind.Array),
            "extensions" => VerifyFixtureQuery("extensions", "extensions", JsonValueKind.Array),
            "commands" => VerifyFixtureQuery("commands", "commands", JsonValueKind.Array),
            "plan" => VerifyFixtureQuery("workingState", "workingState", JsonValueKind.String),
            _ => false,
        };
        return (verified, "actual_ClientActionHandler_and_OmniServer; output_sha256=" + Hash(result.Text)
            + "; output_characters=" + result.Text.Length + "; fixture_only; not_native_model_invocation");

        bool VerifyFixtureQuery(string queryName, string field, JsonValueKind kind)
        {
            var json = server.Query(queryName, cancellationToken)?.Json;
            if (json is null) return false;
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(field, out var value) || value.ValueKind != kind) return false;
            if (queryName is "skills" or "extensions" or "memory.session")
                if (document.RootElement.GetProperty("session").GetString() != server.LastSessionId()!.ToString()) return false;
            return result.Text == (json.Length <= 1800 ? json : json[..1800] + "…");
        }
    }
}

internal sealed record CommandEvidence(string ExpectedCommand, string Intent, string ModelCommand,
    string[]? ModelArguments, bool InterpretationPassed, string ExecutionStatus, bool FixtureEffectVerified,
    string Verification, bool NativeModelToolInvocation, DateTimeOffset StartedAt, DateTimeOffset CompletedAt,
    int GenerationCountSoFar);
