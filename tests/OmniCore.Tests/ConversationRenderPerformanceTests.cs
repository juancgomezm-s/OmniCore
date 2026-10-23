using OmniCore.Cli;
using OmniCore.Client;

namespace OmniCore.Tests;

public sealed class ConversationRenderPerformanceTests
{
    [Fact]
    public void Unchanged_large_history_checks_do_not_copy_message_text_or_allocate()
    {
        var text = new string('x', 16_384);
        var blocks = Enumerable.Range(0, 100)
            .Select(i => new ConversationBlock(i.ToString(), ConversationRole.Assistant, text, null)).ToArray();
        var rendered = blocks.ToArray();
        for (var i = 0; i < 100; i++) Assert.True(TuiApp.SameConversationContent(rendered, blocks));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var unchanged = true;
        for (var i = 0; i < 1_000; i++) unchanged &= TuiApp.SameConversationContent(rendered, blocks);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(unchanged);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Content_changes_including_replaced_list_entries_are_detected_and_equivalent_blocks_are_reused()
    {
        var blocks = new List<ConversationBlock> { new("a", ConversationRole.Assistant, "Original", null) };
        var rendered = blocks.ToArray();
        Assert.False(TuiApp.SameConversationContent(null, blocks));
        Assert.True(TuiApp.SameConversationContent(rendered, blocks));
        blocks[0] = new("b", ConversationRole.Assistant, "Original", null);
        Assert.True(TuiApp.SameConversationContent(rendered, blocks));
        blocks[0] = new("b", ConversationRole.Assistant, "Edited", null);
        Assert.False(TuiApp.SameConversationContent(rendered, blocks));
        blocks[0] = new("b", ConversationRole.Tool, "Original", null);
        Assert.False(TuiApp.SameConversationContent(rendered, blocks));
        blocks[0] = new("b", ConversationRole.Assistant, "Original", "filesystem.read");
        Assert.False(TuiApp.SameConversationContent(rendered, blocks));
        blocks.Clear();
        Assert.False(TuiApp.SameConversationContent(rendered, blocks));
    }
}
