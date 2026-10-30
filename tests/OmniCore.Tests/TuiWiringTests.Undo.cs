namespace OmniCore.Tests;

using System.Text;

using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Tools;

using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

public sealed partial class TuiWiringTests
{
    private static string SeedUndoTarget(TuiFixture fx)
    {
        var before = Encoding.UTF8.GetBytes("before undo\n");
        var after = Encoding.UTF8.GetBytes("after undo\n");
        var path = Path.Combine(fx.Workspace, "undo-target.txt");
        File.WriteAllBytes(path, after);
        var task = fx.Decoded<RunCreated>().Last().RootTask;
        var lane = fx.Decoded<LaneCreated>().Single(item => item.TaskId == task).LaneId;
        var call = ToolCallId.New();
        using var scope = ExecutionScope.Begin(new(fx.Server.LastRunId()!, task, lane, TurnId.New(), call, ExecutionId.New()));
        var stream = new EventStream(fx.Server.AcquireStore(), fx.Server.AcquireCodecs(), fx.Server.LastSessionId()!);
        var pre = FilesystemPreimage.Capture(new(FileVersion.VersionToken(before), FileVersion.VersionToken(after), null), fx.Artifacts, before).BeforeStateRef;
        var post = FilesystemPreimage.CaptureAfter(fx.Artifacts, after);
        stream.Append(new ToolCallRequested(call, "undo-fixture", "filesystem.patch", "{}"));
        stream.Append(new ToolCallPrepared(call, "{}"));
        stream.Append(new PermissionEvaluated(call, PermissionDecision.Allow, "{}", null));
        stream.Append(new ToolCallAuthorized(call));
        stream.Append(new ToolCallStarted(call, EffectClass.NonIdempotent,
            FilesystemReconciliationMetadata.Encode("undo-target.txt", FileVersion.VersionToken(before), FileVersion.VersionToken(after)))
        { TargetRef = "undo-target.txt", BeforeStateRef = pre, Reversibility = Reversibility.Reversible }, DurabilityClass.Barrier);
        stream.Append(new ToolCallSucceeded(call, "{}") { AfterStateRef = post });
        return path;
    }

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("it")]
    public void Undo_review_opens_from_slash_command_and_Escape_has_no_effect(string locale) => RunTuiTest(fx =>
    {
        var path = SeedUndoTarget(fx);
        fx.StartTui(initialColumns: 100, initialRows: 35, localeSelection: new(locale, LocaleSelectionSource.CommandLine));
        fx.Invoke(() => fx.App.Composer!.Text = "/undo");
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay?.Id == "undo-review", "undo opens the same review action");
        fx.Read(() =>
        {
            Assert.Contains(new Localization(locale).Resolve("undo.title"), fx.App.Overlay!.Title);
#pragma warning disable CS0618
            Assert.Contains("undo-target.txt", Descendants(fx.App.Overlay!).OfType<TextView>().Single().Text.ToString());
#pragma warning restore CS0618
            Assert.True(Descendants(fx.App.Overlay!).OfType<Button>().Single(view => view.Id == "undo-confirm").Enabled);
        });
        KeyWithEffect(fx, KeyCode.Esc, () => fx.App.Overlay is null, "Escape cancels undo");
        Assert.Equal("after undo\n", File.ReadAllText(path));
        Assert.Empty(fx.Decoded<UndoStarted>());
    });

    [Fact]
    public void Undo_recovery_button_classifies_a_pending_conflict_without_retrying_writes() => RunTuiTest(fx =>
    {
        var path = SeedUndoTarget(fx);
        var store = fx.Server.AcquireStore(); var codecs = fx.Server.AcquireCodecs();
        var session = fx.Server.LastSessionId()!;
        var source = store.ReadFrom(session, 1).Single(evt => codecs.Decode(evt) is ToolCallStarted start && start.TargetRef == "undo-target.txt");
        var original = Assert.IsType<ToolCallStarted>(codecs.Decode(source));
        var execution = ExecutionId.New(); var command = CommandId.New();
        using (ExecutionScope.Begin(new(source.RunId, source.TaskId, source.LaneId, source.TurnId, original.ToolCallId, execution)))
        using (CausationScope.Begin(new CommandCausation(command)))
            new EventStream(store, codecs, session).Append(new UndoStarted(command, Guid.NewGuid().ToString("N"), original.ToolCallId,
                new(source.EventId, source.RunId, source.TaskId, source.LaneId, source.TurnId, source.ExecutionId, execution),
                [new("undo-target.txt", FileVersion.VersionToken(Encoding.UTF8.GetBytes("after undo\n")), FileVersion.VersionToken(Encoding.UTF8.GetBytes("before undo\n")))],
                fx.Decoded<WorkspaceRootEstablished>().Single().DurableIdentity), DurabilityClass.Barrier);
        File.WriteAllText(path, "foreign edit\n");
        fx.StartTui(initialColumns: 100, initialRows: 35);
        fx.Invoke(() => fx.App.ShowUndo());
        fx.Read(() => Assert.True(Descendants(fx.App.Overlay!).OfType<Button>().Single(view => view.Id == "undo-confirm").Enabled));
        fx.Invoke(() => Descendants(fx.App.Overlay!).OfType<Button>().Single(view => view.Id == "undo-confirm").InvokeCommand(Command.Accept));
        fx.Wait(() => fx.Decoded<UndoCompleted>().Any(), "recovery completion is durable");
        Assert.Equal(UndoOutcome.Unknown, Assert.Single(fx.Decoded<UndoCompleted>()).Outcome);
        Assert.Equal("foreign edit\n", File.ReadAllText(path));
    });

    [Fact]
    public void Undo_confirm_restores_only_the_reviewed_effect() => RunTuiTest(fx =>
    {
        var path = SeedUndoTarget(fx);
        fx.StartTui(initialColumns: 100, initialRows: 35);
        fx.Invoke(() => fx.App.ShowUndo());
        fx.Invoke(() => Descendants(fx.App.Overlay!).OfType<Button>().Single(view => view.Id == "undo-confirm").InvokeCommand(Command.Accept));
        fx.Wait(() => File.ReadAllText(path) == "before undo\n", "confirmed undo restores the original bytes");
        Assert.Single(fx.Decoded<UndoCompleted>());
    });
}
