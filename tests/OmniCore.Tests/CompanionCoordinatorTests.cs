using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Protocol;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed class CompanionCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "omni-companion-tests-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private CompanionCoordinator Hub() => new(_root, () => _now);
    private CompanionInstance Instance(string id = "receiver", string session = "session", string? run = "run") =>
        new(id, "project", session, run, "Disponible", _now, [new("choice", "Continuar?", "Permission", ["allow", "deny"], ["Permitir", "Rechazar"])]);
    private static CompanionDelivery Delivery(string target = "receiver", string session = "session", string? run = "run") =>
        new(Guid.NewGuid().ToString(), target, session, run, "instruction", "Revisa el resultado");

    [Fact]
    public void Separate_connections_route_exact_target_and_claim_once()
    {
        var sender = Hub(); var receiver = Hub(); receiver.Heartbeat(Instance()); receiver.Heartbeat(Instance("other"));
        var message = Delivery(); sender.Send(message); sender.Send(message);
        Assert.Null(receiver.Claim("other", "session", "run", false));
        var claimed = receiver.Claim("receiver", "session", "run", false); Assert.NotNull(claimed);
        Assert.Equal(message.Id, claimed.Id); Assert.Null(sender.Claim("receiver", "session", "run", false));
        receiver.Complete(claimed, true, "Admitida");
        Assert.Equal("accepted", Assert.Single(sender.Read().Deliveries).Status);
    }
    [Fact]
    public void Busy_principal_queues_instructions_but_can_answer_exact_pending_choice()
    {
        var hub = Hub(); hub.Heartbeat(Instance()); hub.Send(Delivery());
        var answer = Delivery() with { Kind = "answer", Text = "", Interaction = "choice", Option = "allow" }; hub.Send(answer);
        Assert.Equal(answer.Id, hub.Claim("receiver", "session", "run", true)!.Id);
        Assert.Null(hub.Claim("receiver", "session", "run", true));
        Assert.Equal("instruction", hub.Claim("receiver", "session", "run", false)!.Kind);
    }
    [Fact]
    public void Session_change_disconnect_and_expired_heartbeat_fail_closed()
    {
        var hub = Hub(); hub.Heartbeat(Instance()); hub.Send(Delivery());
        Assert.Null(hub.Claim("receiver", "another-session", "run", false));
        Assert.Equal("rejected", Assert.Single(hub.Read().Deliveries).Status);
        hub.Heartbeat(Instance(session: "another-session")); Assert.Throws<InvalidOperationException>(() => hub.Send(Delivery()));
        _now += TimeSpan.FromSeconds(16); Assert.Empty(hub.Read().Instances);
        Assert.Throws<InvalidOperationException>(() => hub.Send(Delivery(session: "another-session")));
        hub.Heartbeat(Instance()); hub.Disconnect("receiver"); Assert.Throws<InvalidOperationException>(() => hub.Send(Delivery()));
    }
    [Fact]
    public void Invalid_or_stale_decision_never_becomes_permission_approval()
    {
        var hub = Hub(); hub.Heartbeat(Instance());
        var answer = Delivery() with { Kind = "answer", Interaction = "choice", Option = "sí" };
        Assert.Throws<InvalidOperationException>(() => hub.Send(answer));
        Assert.Throws<ArgumentException>(() => hub.Send(Delivery() with { Kind = "shell" }));
        Assert.Throws<ArgumentException>(() => hub.Send(Delivery() with { Text = new string('x', 8001) }));
        Assert.Empty(hub.Read().Deliveries);
    }
    [Fact]
    public void Disconnected_claim_is_unknown_and_queued_delivery_is_rejected_without_replay()
    {
        var hub = Hub(); hub.Heartbeat(Instance()); var claimed = Delivery(); var queued = Delivery();
        hub.Send(claimed); Assert.NotNull(hub.Claim("receiver", "session", "run", false)); hub.Send(queued);
        hub.Disconnect("receiver"); var deliveries = hub.Read().Deliveries;
        Assert.Equal("unknown", Assert.Single(deliveries, d => d.Id == claimed.Id).Status);
        Assert.Equal("rejected", Assert.Single(deliveries, d => d.Id == queued.Id).Status);
        hub.Heartbeat(Instance()); Assert.Null(hub.Claim("receiver", "session", "run", false));
    }
    [Fact]
    public void Resolved_notices_and_quiet_preference_survive_another_connection()
    {
        var hub = Hub(); hub.Heartbeat(Instance());
        hub.Notify(new("decision/session/choice", "receiver", "session", "Decisión pendiente", _now));
        hub.Heartbeat(Instance() with { Decisions = [] });
        Assert.True(Assert.Single(hub.Read().Notices).Resolved); Assert.True(hub.Read().Notices[0].Read);
        hub.SetQuiet(true); Assert.True(Hub().ReadQuiet()); hub.SetQuiet(false); Assert.False(Hub().ReadQuiet());
    }
    [Fact]
    public void Disposed_operation_releases_coordinator_database_directory()
    {
        Hub().Heartbeat(Instance());

        Directory.Delete(Path.Combine(_root, "companion"), recursive: true);

        Assert.False(Directory.Exists(Path.Combine(_root, "companion")));
    }
    [Fact]
    public void Failed_bootstrap_releases_connection_and_preserves_sqlite_error()
    {
        var directory = Path.Combine(_root, "companion");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "coordinator.db");
        // A nonempty invalid header fails while opening/initializing the database,
        // before Heartbeat can run any application statements.
        File.WriteAllBytes(path, Enumerable.Repeat((byte)0x5A, 512).ToArray());

        var error = Assert.Throws<SqliteException>(() => Hub().Heartbeat(Instance()));

        Assert.Equal(26, error.SqliteErrorCode); // SQLITE_NOTADB, preserved by Open's cleanup.
        Directory.Delete(directory, recursive: true);
        Assert.False(Directory.Exists(directory));
    }
    [Fact]
    public void Companion_mailbox_measurement()
    {
        var destination = Environment.GetEnvironmentVariable("OMNI_COMPANION_MEASUREMENTS"); if (destination is null) return;
        var hub = Hub(); hub.Heartbeat(Instance()); hub.Read();
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime; var allocated = GC.GetTotalAllocatedBytes();
        var samples = new List<double>();
        for (var n = 0; n < 100; n++)
        {
            var watch = Stopwatch.StartNew(); hub.Heartbeat(Instance()); hub.Read(); hub.ReadQuiet(); samples.Add(watch.Elapsed.TotalMilliseconds);
        }
        process.Refresh();
        File.WriteAllText(destination, System.Text.Json.JsonSerializer.Serialize(new
        {
            ticks = samples.Count, meanMs = samples.Average(), p95Ms = samples.Order().ElementAt(94),
            cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds, allocatedBytes = GC.GetTotalAllocatedBytes() - allocated,
            privateBytes = process.PrivateMemorySize64,
            storageBytes = Directory.GetFiles(Path.Combine(_root, "companion")).Sum(file => new FileInfo(file).Length),
            outputBytes = 0, note = "100 isolated mailbox polls, no model and no renderer; production interval is two seconds."
        }));
        Assert.Equal(100, samples.Count);
    }
    [Fact]
    public void Memory_is_editable_shared_versioned_and_bounded()
    {
        var a = Hub(); var b = Hub(); var initial = a.ReadMemory();
        var saved = a.SaveMemory("Nombre preferido: Juan\nPreguntar por el viaje", initial.Version);
        Assert.Equal(saved, b.ReadMemory());
        Assert.Throws<InvalidOperationException>(() => b.SaveMemory("Sobrescribir", initial.Version));
        Assert.Throws<ArgumentException>(() => a.SaveMemory(new string('é', 9000), saved.Version));
        var forgotten = b.SaveMemory("", saved.Version); Assert.Equal("", File.ReadAllText(forgotten.Path));
    }
    [Fact]
    public void Notices_deduplicate_and_mark_read_across_instances_without_chat_access()
    {
        var a = Hub(); var b = Hub(); var notice = new CompanionNotice("decision/one", "receiver", "session", "Decisión pendiente", _now);
        a.Notify(notice); b.Notify(notice); Assert.Single(b.Read().Notices); b.MarkRead(notice.Id); Assert.True(a.Read().Notices[0].Read);
        a.SaveChat("private/session", Enumerable.Range(0, 100).Select(i => new CompanionChatMessage("user", i.ToString())));
        Assert.Equal(24, b.Chat("private/session").Length); Assert.Empty(b.Chat("another/session"));
    }
    [Fact]
    public void Restrict_persists_without_changing_identity_xp_or_mood_and_has_short_sad_reaction()
    {
        var pet = new CompanionPetService(_root, clock: () => _now);
        var before = pet.Act(new("discover"), "discover");
        var after = pet.Act(new("preferences", Restricted: true), "restrict");
        Assert.True(after.Preferences.Restricted); Assert.Equal(before.Xp, after.Xp); Assert.Equal(before.Mood, after.Mood);
        Assert.Equal(before.Species, after.Species); Assert.Equal("sad", after.Reaction!.AnimationId);
        Assert.True(new CompanionPetService(_root).Status().Preferences.Restricted);
        Assert.Equal(18, CompanionVisualPolicy.SpriteFrame("sad", 0, false)); Assert.Equal(19, CompanionVisualPolicy.SpriteFrame("sad", 1, false));
        _now += TimeSpan.FromSeconds(4); Assert.Null(pet.Status().Reaction);
        Assert.False(pet.Act(new("preferences", Restricted: false), "release").Preferences.Restricted);
    }
    [Fact]
    public async Task Chat_is_on_demand_no_tools_bounded_and_does_not_modify_memory()
    {
        var provider = new ChatProvider(); var hub = Hub(); var memory = hub.ReadMemory();
        Assert.Equal(0, provider.Calls);
        var reply = await CompanionConversation.Reply(provider, new(new("test"), 12000, ToolMode.Direct, null, maxOutputTokens: 512),
            "Omni", "Nombre: Juan", [new("user", "Hola")], CancellationToken.None);
        Assert.Equal("Hola, Juan", reply); Assert.Equal(1, provider.Calls); Assert.Empty(provider.Request!.Tools);
        Assert.Contains("Nombre: Juan", provider.Request.Instructions); Assert.Equal(512, provider.Request.Model.MaxOutputTokens);
        Assert.Equal(memory, hub.ReadMemory());
    }
    private sealed class ChatProvider : IModelProvider
    {
        public int Calls; public ModelRequest? Request;
        public ProviderCapabilities Capabilities => throw new NotSupportedException();
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken token)
        {
            Calls++; Request = request; await System.Threading.Tasks.Task.Yield(); token.ThrowIfCancellationRequested();
            yield return new ResponseCompleted(new([new TextBlock("Hola, Juan")], StopReason.EndTurn,
                new TokenUsage(10, 5, 0, 0, 0), null, new ProviderMetadata("test", "test", null)));
        }
    }
    [Fact]
    public void Worker_mailbox_probe()
    {
        var root = Environment.GetEnvironmentVariable("OMNI_COMPANION_TEST_MAILBOX"); if (root is null) return;
        var hub = new CompanionCoordinator(root); var item = hub.Claim("receiver", "session", "run", false);
        Assert.NotNull(item); hub.Complete(item, true, "Recibida en otro proceso");
    }
    [Fact]
    public async Task Actual_child_process_receives_mail_without_window_focus()
    {
        var hub = Hub(); hub.Heartbeat(Instance()); hub.Send(Delivery());
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(CompanionCoordinatorTests).Assembly.Location);
        start.ArgumentList.Add("-method"); start.ArgumentList.Add("*CompanionCoordinatorTests.Worker_mailbox_probe"); start.ArgumentList.Add("-noColor");
        start.Environment["OMNI_COMPANION_TEST_MAILBOX"] = _root;
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken); var error = child.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await child.WaitForExitAsync(timeout.Token);
        Assert.True(child.ExitCode == 0, await output + await error);
        Assert.Equal("accepted", Assert.Single(hub.Read().Deliveries).Status);
    }
    public void Dispose()
    {
        // Only this fixture's pet service uses a pool; coordinator connections must
        // already be closed, including in the deletion regressions above.
        var petPath = Path.Combine(_root, "companion", "pet.db");
        if (File.Exists(petPath))
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = petPath, DefaultTimeout = 10 }.ToString());
            SqliteConnection.ClearPool(connection);
        }
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
