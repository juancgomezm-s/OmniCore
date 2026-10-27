using OmniCore.Protocol;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    // Shared by typed commands and the palette. No composer mutation or alternate authority path.
    private bool RunHostCommand(CommandInvocation invocation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ack = _client.Send(WireEnvelope.Command(Ids.NewV7(), CommandInvocationJson.Encode(invocation)), cancellationToken);
        if (ack.Status != "ok" || ack.Outcome?.Kind == RuntimeCommandOutcomeKind.Rejected)
        { ShowMessage("/" + invocation.Name + ": " + CommandError(ack.Error)); return false; }
        var outcome = _client.Query("commandOutcome", cancellationToken)?.Json ?? "{}";
        var text = JsonObj.Parse(outcome);
        if (text.TryGetValue("outcome", out _) && TryReadWorkflowOutcome(outcome, out var workflow, out var workflowCommandId))
        {
            if (_turnHost is not null) return StartModelTurn("", workflow: workflow, workflowCommandId: workflowCommandId);
            ShowMessage(_localization.Resolve("shell.this_workflow_command_requires_the_local_host")); return false;
        }
        if (text.TryGetValue("outcome", out _) && TryReadOutcome(outcome, out var expanded))
        {
            string? forcedToolName = null;
            using (var document = System.Text.Json.JsonDocument.Parse(outcome))
                if (document.RootElement.GetProperty("outcome").TryGetProperty("toolName", out var toolName)
                    && toolName.GetString() is { Length: > 0 } selectedTool) forcedToolName = selectedTool;
            return _turnHost is not null ? StartModelTurn(expanded, extensionToolName: forcedToolName) : SubmitSessionInput(expanded);
        }
        return true;
    }
}
