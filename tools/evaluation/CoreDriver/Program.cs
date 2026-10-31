using System.Text.Json;
using OmniCore.Domain;
using OmniCore.Client;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

// No journal seeding or runtime replacement. All actions use production Host APIs.
var request = JsonDocument.Parse(File.ReadAllText(args[0])).RootElement;
var mode = request.GetProperty("mode").GetString();
var workspace = request.GetProperty("workspace").GetString()!;
var evaluationRoot = Path.GetFullPath(request.GetProperty("evaluationRoot").GetString()!);
var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
bool IsPrivate(string? path) => path is not null && Path.GetFullPath(path)
    .StartsWith(evaluationRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison);
if (!File.Exists(Path.Combine(evaluationRoot, "evaluation-root.json")) || !IsPrivate(workspace)
    || !IsPrivate(Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR"))
    || !IsPrivate(Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR")))
{
    Console.WriteLine("evaluation.isolated_paths_required");
    return 2;
}
var paths = OmniHost.CreatePlatformPaths();
if (mode == "prepare")
{
    using var reasoning = new ReasoningPreferenceStore(paths.UserDatabasePath);
    var current = reasoning.Read();
    reasoning.Set(new ReasoningRequest("medium", null), current.Revision);
    Console.WriteLine("Explicit medium reasoning configured in isolated evaluation profile.");
    return 0;
}
using var runtime = OmniCliRuntime.Create(workspace);
runtime.UiLocale = "en";
var localization = new Localization("en");
runtime.Localize = (key, values) => localization.Resolve(key, values);
runtime.UseConsoleInput = true;
runtime.SetWorkspaceTrusted(true);
var token = CancellationToken.None;
var client = runtime.Connect(token);
if (mode == "context")
    return await runtime.ActWithDeferredPermissionAsync(request.GetProperty("objective").GetString()!, Console.WriteLine, token);
if (mode == "resume")
    return await new TuiTurnHost(runtime).ExecuteAsync(request.GetProperty("objective").GetString()!, Console.WriteLine, token);
if (mode != "delegation") return 2;
var objective = request.GetProperty("objective").GetString()!;
var seed = await new TuiTurnHost(runtime).ExecuteAsync(
    "Prepare for a bounded delegated implementation. Inspect src/math.mjs, do not edit anything, and await the compiled workflow request.", Console.WriteLine, token);
if (seed != 0) { Console.WriteLine("evaluation.run.admission_failed"); return 2; }
if (client is not ITrustedUserActionClient trusted) return 2;
var authorize = trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), """
    {"cmd":"run.mode.select","mode":"orq","effort":"ultracode","adaptive":true,
     "allowedModes":"plan,act,orq","maxAgents":4,"maxDepth":3,"maxTurns":12,
     "maxToolCalls":36,"maxElapsedSeconds":300}
    """), token);
if (authorize.Status != "ok") { Console.WriteLine("evaluation.workflow.authority_rejected"); return 2; }
var workflowArgs = new[] { objective, "--verify-executable", request.GetProperty("node").GetString()!,
    "--verify-argv-json", "[\"--preserve-symlinks\",\"--preserve-symlinks-main\",\"tests/agentic-check.mjs\"]" };
var commandId = Ids.NewV7();
var invoke = client.Send(WireEnvelope.Command(commandId,
    CommandInvocationJson.Encode(new CommandInvocation("core:orq-auth", workflowArgs, "cli"))), token);
if (invoke.Status != "ok") { Console.WriteLine("evaluation.workflow.request_rejected"); return 2; }
return await runtime.ExecuteWorkflowAsync(new WorkflowRequestDto("core:explore-implement-verify", "1", workflowArgs),
    commandId, Console.WriteLine, token);
