using System.Net;
using System.Text;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Infrastructure;
using OmniCore.Models;

namespace OmniCore.Tests;

public sealed class TokenCountingTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        public string? LastBody;
        public string? LastPath;

        protected override async System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            LastPath = request.RequestUri!.AbsolutePath;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return respond(request);
        }
    }

    private static ContextItem Item(string text) => new("i", ContextItemKind.ToolResult, text, 0, ContextPriority.Low,
        RetentionPolicy.RegenerateEachTurn,
        new ContextProvenance("i", ContributionCategory.ToolObservations, "test", ScopeLevel.Run, false));

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static LlamaCppTokenCounter Counter(StubHandler h, string? id = null) =>
        new("http://127.0.0.1:8080/", TokenizerId.Parse(id ?? "llamacpp:" + Guid.NewGuid()), new HeuristicTokenCounter(), () => new HttpClient(h, false));

    [Fact]
    public async System.Threading.Tasks.Task Exact_count_comes_from_tokenize()
    {
        var h = new StubHandler(_ => Json("{\"tokens\":[1,2,3,4,5,6,7]}"));
        var c = Counter(h);
        var n = await c.CountAsync(Item("hola mundo"), TestContext.Current.CancellationToken);
        Assert.Equal(7, n);
        Assert.Equal(TokenCountAccuracy.Exact, c.Accuracy);
        Assert.Equal("/tokenize", h.LastPath);
        Assert.Contains("\"content\":\"hola mundo\"", h.LastBody);
    }

    [Fact]
    public async System.Threading.Tasks.Task Endpoint_failure_falls_back_to_estimate_and_reports_estimated()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var c = Counter(h);
        var text = new string('a', 40);
        var n = await c.CountAsync(Item(text), TestContext.Current.CancellationToken);
        Assert.Equal(11, n);
        Assert.Equal(TokenCountAccuracy.Estimated, c.Accuracy);
    }

    [Fact]
    public async System.Threading.Tasks.Task Estimated_margin_is_ten_percent_rounded_up_and_configurable()
    {
        var ct = TestContext.Current.CancellationToken;
        var item = Item(new string('a', 40)); // 10 tokens base
        Assert.Equal(11, await new HeuristicTokenCounter().CountAsync(item, ct));
        Assert.Equal(10, await new HeuristicTokenCounter(0).CountAsync(item, ct));
        Assert.Equal(13, await new HeuristicTokenCounter(0.25).CountAsync(item, ct));
        Assert.Equal(2, await new HeuristicTokenCounter().CountAsync(Item("abcd"), ct)); // 1.1 -> 2
    }

    [Fact]
    public async System.Threading.Tasks.Task Cache_avoids_second_http_call()
    {
        var h = new StubHandler(_ => Json("{\"tokens\":[1,2,3]}"));
        var c = Counter(h);
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(3, await c.CountAsync(Item("same"), ct));
        Assert.Equal(3, await c.CountAsync(Item("same"), ct));
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async System.Threading.Tasks.Task Different_tokenizer_id_misses_cache()
    {
        var h = new StubHandler(_ => Json("{\"tokens\":[1,2,3]}"));
        var idA = "llamacpp:a" + Guid.NewGuid();
        var a = Counter(h, idA);
        var b = Counter(h, "llamacpp:b" + Guid.NewGuid());
        var ct = TestContext.Current.CancellationToken;
        await a.CountAsync(Item("same"), ct);
        await b.CountAsync(Item("same"), ct);
        Assert.Equal(2, h.Calls);
        await Counter(h, idA).CountAsync(Item("same"), ct); // same id, other instance: shared cache hit
        Assert.Equal(2, h.Calls);
    }
}
