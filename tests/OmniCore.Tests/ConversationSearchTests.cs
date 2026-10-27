using OmniCore.Client;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class ConversationSearchTests
{
    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("it")]
    public void Search_resources_are_complete_and_action_has_an_effective_shortcut(string locale)
    {
        var text = new Localization(locale);
        foreach (var key in ConversationSearchResources.Keys)
        {
            var translated = text.ResolveDetailed(key);
            Assert.False(translated.UsedFallback); Assert.Equal(locale, translated.ResourceLocale);
            Assert.NotEqual(key, translated.Text);
        }
        var context = new KeyBindingContext([]);
        var entry = Assert.Single(CommandPalette.Build(null, text, context, false), row => row.Target.Id == "session.search");
        Assert.Equal("Ctrl+F", Assert.Single(CommandPalette.Bindings(entry, new KeyBindingService([]), context)).Definition.Keys);
    }

    [Fact]
    public void Search_keeps_original_snippets_identity_and_offsets_without_mutating_source()
    {
        var original = "Primera LÍNEA\r\n**Prueba** 😀 prueba\nfin";
        ConversationBlock[] blocks = [new("event-one", ConversationRole.Assistant, original, null)];
        var result = ConversationSearch.Search(blocks, "PRUEBA");
        Assert.Equal(2, result.Hits.Count); Assert.False(result.Limited);
        Assert.All(result.Hits, hit =>
        {
            Assert.Equal("event-one", hit.BlockId); Assert.Equal(ConversationRole.Assistant, hit.Role);
            Assert.Equal(original, hit.Snippet); Assert.Equal(0, hit.SnippetOffset);
            Assert.Equal("prueba", original.Substring(hit.Offset, hit.Length), ignoreCase: true);
        });
        Assert.Equal(original, blocks[0].Text);
        Assert.Empty(ConversationSearch.Search(blocks, "primera linea").Hits); // No normalization of source evidence.
        Assert.Empty(ConversationSearch.Search([], "prueba").Hits);
    }

    [Fact]
    public void Snippets_keep_surrogate_pairs_intact_at_both_boundaries()
    {
        var original = "😀" + new string('x', 79) + "match" + new string('y', 79) + "😀end";
        var hit = Assert.Single(ConversationSearch.Search([new("a", ConversationRole.User, original, null)], "match").Hits);
        Assert.StartsWith("😀", hit.Snippet); Assert.EndsWith("😀", hit.Snippet);
        Assert.Equal(original.Substring(hit.SnippetOffset, hit.Snippet.Length), hit.Snippet);
    }

    [Fact]
    public void Empty_long_and_literal_queries_are_bounded_and_never_executed()
    {
        ConversationBlock[] blocks = [new("a", ConversationRole.User, "/* $(command) [a-z]+", null)];
        Assert.Empty(ConversationSearch.Search(blocks, " ").Hits);
        Assert.True(ConversationSearch.Search(blocks, new string('a', 257)).QueryTooLong);
        Assert.Single(ConversationSearch.Search(blocks, "$(command)").Hits);
        Assert.Single(ConversationSearch.Search(blocks, "[a-z]+").Hits);
        Assert.Empty(ConversationSearch.Search(blocks, "\\w+").Hits);
    }

    [Fact]
    public void Result_character_and_block_limits_are_visible_instead_of_claiming_a_complete_search()
    {
        var many = ConversationSearch.Search([new("a", ConversationRole.User, string.Concat(Enumerable.Repeat("hit ", 1000)), null)], "hit");
        Assert.Equal(ConversationSearch.MaximumResults, many.Hits.Count); Assert.True(many.Limited);
        var large = ConversationSearch.Search([new("a", ConversationRole.User,
            new string('x', ConversationSearch.MaximumCharacters) + "needle", null)], "needle");
        Assert.Empty(large.Hits); Assert.True(large.Limited);
        var empty = Enumerable.Range(0, ConversationSearch.MaximumBlocks + 1)
            .Select(index => new ConversationBlock(index.ToString(), ConversationRole.User, "", null)).ToArray();
        Assert.True(ConversationSearch.Search(empty, "needle").Limited);
    }

    [Fact]
    public void Search_cannot_recover_child_or_ambiguous_text_excluded_by_the_client_projection()
    {
        var projection = new ClientProjection(new("en"));
        var state = ClientState.Empty();
        foreach (var scope in new[] { "child", "ambiguous" })
            state = projection.Apply(state, WireEnvelope.Event(Ids.NewV7(), "{"
                + JsonObj.Field("type", "user_input.received") + "," + JsonObj.Field("conversationScope", scope)
                + "," + JsonObj.Field("text", "hidden needle") + "}"));
        Assert.Empty(ConversationSearch.Search(state.Conversation.Blocks, "needle").Hits);
    }
}
