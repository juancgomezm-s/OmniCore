namespace OmniCoreLiveAcceptance;

using System.Text.Json;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            var runner = new AcceptanceRunner();
            if (options.SelfTest)
            {
                if (options.CaseId == "native-cli")
                {
                    var safety = AcceptanceRunner.RunNativeCliSafetyCheck();
                    Console.WriteLine(JsonSerializer.Serialize(safety, JsonOptions.Indented));
                    return safety.CanonicalArgumentsAccepted && safety.InjectedArgumentsRejected ? 0 : 1;
                }
                var selfTest = runner.RunSelfTest(options.CaseId);
                Console.WriteLine(JsonSerializer.Serialize(selfTest, JsonOptions.Indented));
                return selfTest.Failed > 0 ? 1 : 0;
            }
            var preflight = runner.Preflight(options.Model);
            if (options.Preflight)
            {
                Console.WriteLine(JsonSerializer.Serialize(preflight, JsonOptions.Indented));
                return preflight.Ready ? 0 : 2;
            }
            if (!options.AllowLive)
            {
                Console.Error.WriteLine("Live acceptance requires --allow-live.");
                return 2;
            }
            if (!preflight.Ready)
            {
                Console.WriteLine(JsonSerializer.Serialize(preflight, JsonOptions.Indented));
                return 2;
            }

            var evidenceDirectory = runner.CreateEvidenceDirectory(options.Model);
            if (options.NativeCli || options.CaseId == "native-cli-maintenance")
            {
                var native = await runner.RunNativeCliCommandsAsync(options, preflight, evidenceDirectory, CancellationToken.None);
                Console.WriteLine(JsonSerializer.Serialize(native, JsonOptions.Indented));
                return native.Status == "success" ? 0 : 1;
            }
            if (options.Probe)
            {
                var probe = await runner.RunTransportProbeAsync(preflight, evidenceDirectory);
                Console.WriteLine(JsonSerializer.Serialize(probe, JsonOptions.Indented));
                return probe.Completed ? 0 : 1;
            }
            if (options.Commands)
            {
                var commands = await runner.RunCommandsAsync(options, preflight, evidenceDirectory);
                Console.WriteLine(JsonSerializer.Serialize(commands, JsonOptions.Indented));
                return commands.Any(item => !item.InterpretationPassed || item.ExecutionStatus == "failed") ? 1 : 0;
            }
            var summary = await runner.RunAsync(options, preflight, evidenceDirectory).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(summary, JsonOptions.Indented));
            return summary.Failed > 0 || summary.Blocked > 0 ? 1 : 0;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine(Options.Usage);
            return 2;
        }
        catch (Exception exception)
        {
            // Do not print arbitrary provider/configuration exception text: it may contain
            // endpoint, user data, or authorization details. The case evidence records only type.
            Console.Error.WriteLine("Acceptance runner failed safely: " + exception.GetType().Name);
            return 1;
        }
    }
}

internal sealed record Options(
    bool SelfTest,
    bool Preflight,
    bool AllowLive,
    bool All,
    string? CaseId,
    string Model,
    bool AllowFixtureProcess,
    bool AutoSelect,
    bool Commands,
    bool Probe,
    bool NativeCli)
{
    public const string Usage = "Usage: OmniCoreLiveAcceptance --self-test [--case <tool-id>] | --preflight [--model luna|qwen] | "
        + "--case <tool-id|core-tools|model-command-boundary|native-cli-maintenance> [--model luna|qwen] --allow-live | "
        + "--all|--commands|--probe|--native-cli [--model luna|qwen] --allow-live [--auto-select] [--allow-fixture-process]";

    public static Options Parse(string[] args)
    {
        bool selfTest = false, preflight = false, allowLive = false, all = false, allowFixtureProcess = false, autoSelect = false, commands = false, probe = false, nativeCli = false;
        string? caseId = null;
        string model = "luna";
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--self-test": selfTest = true; break;
                case "--preflight": preflight = true; break;
                case "--allow-live": allowLive = true; break;
                case "--all": all = true; break;
                case "--commands": commands = true; break;
                case "--probe": probe = true; break;
                case "--native-cli": nativeCli = true; break;
                case "--allow-fixture-process": allowFixtureProcess = true; break;
                case "--auto-select": autoSelect = true; break;
                case "--case" when index + 1 < args.Length: caseId = args[++index]; break;
                case "--model" when index + 1 < args.Length: model = args[++index]; break;
                default: throw new ArgumentException("Unknown or incomplete option: " + args[index]);
            }
        }

        if (model is not ("luna" or "qwen"))
            throw new ArgumentException("--model must be luna or qwen; no model fallback is available.");
        if (selfTest && (preflight || all || commands || probe || nativeCli || allowLive || model != "luna"))
            throw new ArgumentException("--self-test is offline and accepts only an optional --case.");
        if (preflight && (all || commands || probe || nativeCli || caseId is not null || allowLive))
            throw new ArgumentException("--preflight is an offline inspection and cannot be combined with live execution options.");
        if (!selfTest && !preflight && (all ? 1 : 0) + (commands ? 1 : 0) + (probe ? 1 : 0) + (nativeCli ? 1 : 0) + (caseId is not null ? 1 : 0) != 1)
            throw new ArgumentException("Choose exactly one of --all, --commands, --probe, --native-cli or --case.");
        if (selfTest && caseId is not null && caseId != "native-cli" && !AcceptanceRunner.KnownCaseIds.Contains(caseId, StringComparer.Ordinal))
            throw new ArgumentException("Unknown self-test case.");
        if ((allowFixtureProcess || autoSelect) && !allowLive)
            throw new ArgumentException("Process consent and model selection modes require explicit --allow-live.");
        return new Options(selfTest, preflight, allowLive, all, caseId, model, allowFixtureProcess, autoSelect, commands, probe, nativeCli);
    }
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}
