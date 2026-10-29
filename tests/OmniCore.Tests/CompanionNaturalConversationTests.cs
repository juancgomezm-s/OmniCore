using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Protocol;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed class CompanionNaturalConversationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "omni-natural-pet-" + Guid.NewGuid().ToString("N"));
    private CompanionCoordinator Hub() => new(_root);
    private CompanionInstance Peer(string name) => new(name, Path.Combine(_root, name), "session-" + name, "run-" + name, "Disponible", DateTimeOffset.UtcNow, [], SupportsLateral: true);

    [Fact]
    public void Project_names_are_shared_and_repeated_lookups_do_not_advance_the_next_project_number()
    {
        var hub = Hub(); Assert.Equal("Proyecto 101", hub.ProjectLabel(Peer("Alpha").Workspace));
        for (var i = 0; i < 20; i++) Assert.Equal("Proyecto 101", new CompanionCoordinator(_root).ProjectLabel(Peer("Alpha").Workspace));
        Assert.Equal("Proyecto 102", hub.ProjectLabel(Peer("Beta").Workspace));
    }

    [Theory]
    [InlineData(true, 262144, 32768)]
    [InlineData(false, 262144, 262144)]
    [InlineData(true, 8192, 8192)]
    [InlineData(false, 1000000, 1000000)]
    public void Context_uses_local_32k_or_the_remote_model_capacity(bool local, long declared, long expected)
    { Assert.Equal(expected, CompanionContext.Capacity(local, declared)); }

    [Fact]
    public async Task Context_keeps_more_than_twelve_messages_and_does_not_cut_long_user_messages_or_answers()
    {
        var text = new string('x', 12000); var history = Enumerable.Range(0, 30).Select(i => new CompanionChatMessage(i % 2 == 0 ? "user" : "assistant", "Mensaje " + i)).Append(new("user", text)).ToArray();
        var answer = new string('a', 6000);
        var provider = new CompanionToolsTests.ScriptedProvider([CompanionToolsTests.Response([new TextBlock(answer)])]);
        Assert.Equal(answer, await CompanionConversation.Reply(provider, new(new("local"), 28672, ToolMode.Direct, null), "Omni", "", history, TestContext.Current.CancellationToken));
        Assert.Equal(31, provider.Requests[0].Messages.Count);
        Assert.Equal(text, Assert.IsType<TextBlock>(provider.Requests[0].Messages[^1].Content[0]).Text);
    }

    [Fact]
    public async Task Progressive_compaction_checkpoints_old_exchanges_and_retains_the_complete_archive()
    {
        var hub = Hub();
        for (var i = 0; i < 60; i++) hub.AppendDialogue(new(i % 2 == 0 ? "user" : "assistant", "Registro " + i + " " + new string('x', 3000), "entry-" + i));
        hub.AppendDialogue(new("user", "Última pregunta protegida", "last"));
        var history = hub.DialogueContext(32768);
        var provider = new CompanionToolsTests.ScriptedProvider([CompanionToolsTests.Response([new TextBlock("Respuesta")])]);
        await CompanionConversation.Reply(provider, new(new("local"), 28672, ToolMode.Direct, null), "Omni", "", history,
            TestContext.Current.CancellationToken, checkpoint: hub.SaveConversationCheckpoint);
        Assert.NotEmpty(hub.ConversationSummary());
        Assert.True(provider.Requests[0].Messages.Count < history.Length);
        Assert.Equal(61, hub.DialogueTail(100).Length);
        Assert.True(hub.DialogueContext(32768).Length < history.Length);
        Assert.Equal("Última pregunta protegida", Assert.IsType<TextBlock>(provider.Requests[0].Messages[^1].Content[0]).Text);
        hub.SaveConversationCheckpoint("resumen anterior", "entry-0");
        Assert.NotEqual("resumen anterior", hub.ConversationSummary());
    }

    [Fact]
    public void Grouped_notices_share_one_conversation_and_are_read_only_after_presentation_is_acknowledged()
    {
        var hub = Hub(); hub.Heartbeat(Peer("Alpha")); var peer = Peer("Alpha");
        hub.Notify(new("turn/one", peer.Id, peer.Session, "Terminó un turno", DateTimeOffset.UtcNow.AddSeconds(-2)));
        hub.Notify(new("turn/two", peer.Id, peer.Session, "Terminó sus cambios", DateTimeOffset.UtcNow));
        Assert.Single(hub.PendingNotices());
        var presented = hub.PresentNotices(acknowledge: false); Assert.Single(presented); Assert.Single(hub.PendingNotices());
        Assert.Contains(hub.ProjectLabel(peer.Workspace), presented[0].Text);
        Assert.Equal(peer.Id, hub.NoticeRoute(presented[0].NoticeId!)!.Id);
        hub.PresentNotices(acknowledge: false); Assert.Single(hub.DialogueTail());
        new CompanionCoordinator(_root).AcknowledgeNotices(presented.Select(n => n.NoticeId!));
        Assert.Empty(hub.PendingNotices()); Assert.Single(new CompanionCoordinator(_root).DialogueTail());
    }

    [Fact]
    public void Pending_notice_count_covers_more_than_the_recent_snapshot_and_acknowledges_only_presented_batches()
    {
        var hub = Hub(); var peer = Peer("Alpha"); hub.Heartbeat(peer);
        for (var i = 0; i < 110; i++) hub.Notify(new("unique-" + i, peer.Id, peer.Session, "Aviso " + i, DateTimeOffset.UtcNow.AddMilliseconds(i)));
        Assert.Equal(100, hub.Read().Notices.Length);
        Assert.Equal(110, hub.PendingNotices().Length);
        var batch = hub.PresentNotices(acknowledge: false);
        Assert.Equal(8, batch.Length); Assert.Equal(110, hub.PendingNotices().Length);
        hub.AcknowledgeNotices(batch.Select(n => n.NoticeId!));
        Assert.Equal(102, hub.PendingNotices().Length);
        var remaining = hub.PresentNotices(); Assert.Equal(8, remaining.Length);
        Assert.Equal(94, hub.PendingNotices().Length);
        Assert.Equal(16, hub.DialogueTail().Length);
    }

    [Fact]
    public void Natural_project_names_and_pronouns_use_exact_notice_bindings_without_overriding_an_explicit_destination()
    {
        var hub = Hub(); var alpha = Peer("Alpha"); var beta = Peer("Beta"); hub.Heartbeat(alpha); hub.Heartbeat(beta);
        hub.Notify(new("turn/a", alpha.Id, alpha.Session, "Terminó", DateTimeOffset.UtcNow));
        var label = hub.ProjectLabel(alpha.Workspace);
        var literal = new CompanionChatTools(hub, "Dile a " + label + " que valide contra main");
        var sent = literal.Execute(CompanionToolsTests.Call("agent.message", "{\"target\":\"" + label + "\",\"text\":\"valide contra main\"}"), TestContext.Current.CancellationToken);
        Assert.True(sent.Ok); Assert.Equal(alpha.Id, sent.Delivery!.Target);
        var reference = new CompanionChatTools(hub, "Dile que revise las pruebas", focusNoticeId: "turn/a");
        var answer = reference.Execute(CompanionToolsTests.Call("agent.message", "{\"target\":\"aviso actual\",\"text\":\"revise las pruebas\"}"), TestContext.Current.CancellationToken);
        Assert.True(answer.Ok); Assert.Equal(alpha.Id, answer.Delivery!.Target);
        var wrong = new CompanionChatTools(hub, "Dile a " + hub.ProjectLabel(beta.Workspace) + " que revise las pruebas", focusNoticeId: "turn/a");
        Assert.False(wrong.Execute(CompanionToolsTests.Call("agent.message", "{\"target\":\"aviso actual\",\"text\":\"revise las pruebas\"}"), TestContext.Current.CancellationToken).Ok);
        hub.Heartbeat(alpha with { Run = "different-run" });
        Assert.False(new CompanionChatTools(hub, "Dile que revise las pruebas", focusNoticeId: "turn/a")
            .Execute(CompanionToolsTests.Call("agent.message", "{\"target\":\"aviso actual\",\"text\":\"revise las pruebas\"}"), TestContext.Current.CancellationToken).Ok);
        Assert.Equal(2, hub.MaintainMemory("followup").Length);
        hub.Complete(sent.Delivery, true, "Recibida");
        Assert.Contains(hub.MaintainMemory("followup"), f => f.Source == "delivery/" + sent.Delivery.Id && f.Status == "received");
    }

    [Fact]
    public void Long_instructions_keep_the_full_delivery_with_atomic_followup_and_disconnect_status()
    {
        var hub = Hub(); var peer = Peer("Alpha"); hub.Heartbeat(peer); var label = hub.ProjectLabel(peer.Workspace);
        var text = new string('x', 3000);
        var tools = new CompanionChatTools(hub, "Dile a " + label + " " + text);
        var sent = tools.Execute(CompanionToolsTests.Call("agent.message", "{\"target\":\"" + label + "\",\"text\":\"" + text + "\"}"), TestContext.Current.CancellationToken);
        Assert.True(sent.Ok); Assert.Equal(text, sent.Delivery!.Text);
        var fact = Assert.Single(hub.MaintainMemory("followup")); Assert.Equal("queued", fact.Status); Assert.True(fact.Text.Length <= 2000);
        hub.EditMemory(fact, "Esperar revisión", fact.Project);
        hub.Disconnect(peer.Id); hub.Read();
        Assert.Equal("rejected", Assert.Single(hub.MaintainMemory("followup")).Status);
    }

    [Fact]
    public void Categorized_memory_can_be_searched_edited_forgotten_and_followups_resolved()
    {
        var hub = Hub(); hub.Remember("Mi nombre es Juan");
        var work = hub.RememberCategorized("Validar cambios contra main", "work", "Proyecto 106");
        hub.RememberCategorized("Validar otro repositorio", "work", "Proyecto 203");
        var follow = hub.RememberCategorized("Esperar revisión", "followup", "Proyecto 106");
        var found = hub.SearchMemory("validar", "work", "Proyecto 106"); Assert.Single(found); Assert.Equal(work.Id, found[0].Id);
        hub.EditMemory(work, "Revisar publicación", "Proyecto 106");
        Assert.Throws<InvalidOperationException>(() => hub.EditMemory(work, "Edición desactualizada", "Proyecto 106"));
        Assert.Contains(hub.SearchMemory("publicacion", "work", "Proyecto 106"), f => f.Id == work.Id);
        hub.ResolveMemory(follow.Id); Assert.Equal("resolved", Assert.Single(hub.MaintainMemory("followup")).Status);
        Assert.Empty(hub.SearchMemory("", "followup", "Proyecto 106"));
        Assert.True(hub.Forget(work.Id)); Assert.DoesNotContain(hub.MaintainMemory("work"), f => f.Project == "Proyecto 106");
        Assert.Contains("Mi nombre es Juan", hub.ReadMemory().Text);
    }

    [Fact]
    public void Legacy_chats_migrate_once_and_later_appends_from_other_windows_are_not_overwritten()
    {
        var hub = Hub(); hub.SaveChat("legacy", [new("user", "Anterior")]); hub.ImportLegacyChat("legacy"); hub.ImportLegacyChat("legacy");
        Assert.Single(hub.DialogueTail());
        var other = new CompanionCoordinator(_root); other.AppendDialogue(new("notice", "Aviso de otra ventana", "notice-one"));
        hub.AppendDialogue(new("user", "Mensaje nuevo", "user-one")); other.AppendDialogue(new("assistant", "Respuesta", "reply-one"));
        hub.AppendDialogue(new("assistant", "Respuesta", "reply-one")); Assert.Equal(4, hub.DialogueTail().Length);
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
