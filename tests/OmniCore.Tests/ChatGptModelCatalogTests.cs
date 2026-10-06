namespace OmniCore.Tests;

using System.Net;
using System.Text.Json;
using OmniCore.Host;
using OmniCore.Models;

public sealed class ChatGptModelCatalogTests
{
    [Fact]
    public void Parser_preserves_server_order_limits_and_excludes_hidden_models()
    {
        using var json = JsonDocument.Parse("""
            {"models":[{"slug":"second","display_name":"Second","visibility":"list","context_window":32768},
            {"slug":"internal","visibility":"hide"},{"slug":"first","visibility":"list","is_default":true}]}
            """);
        var models = ChatGptModelCatalog.Parse(json.RootElement);
        Assert.Equal(new[] { "second", "first" }, models.Select(m => m.Id));
        Assert.Equal("Second", models[0].DisplayName);
        Assert.Equal(32768, models[0].ContextWindow);
        Assert.Null(models[0].MaxOutputTokens);
        Assert.True(models[1].IsDefault);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"models\":null}")]
    [InlineData("{\"models\":[{\"slug\":\"missing-visibility\"}]}")]
    [InlineData("{\"models\":[{\"slug\":\"bad\\nmodel\",\"visibility\":\"list\"}]}")]
    public void Malformed_catalog_is_not_authoritative_withdrawal(string body)
    {
        using var json = JsonDocument.Parse(body);
        Assert.Throws<InvalidOperationException>(() => ChatGptModelCatalog.Parse(json.RootElement));
    }

    [Fact]
    public async Task Current_compatibility_returns_all_visible_generations_not_just_legacy_four()
    {
        var handler = new Handler();
        handler.Replies.Enqueue(new(HttpStatusCode.Unauthorized));
        handler.Replies.Enqueue(new(HttpStatusCode.OK) { Content = new StringContent("""
            {"models":[
              {"slug":"gpt-6.1-sol","visibility":"list"},
              {"slug":"gpt-6-astra","visibility":"list"},
              {"slug":"gpt-6-sol","visibility":"list"},
              {"slug":"gpt-6-luna","visibility":"list"},
              {"slug":"gpt-reserve","visibility":"hide"},
              {"slug":"gpt-5.6-sol","visibility":"list"},
              {"slug":"gpt-5.6-terra","visibility":"list"},
              {"slug":"gpt-5.6-luna","visibility":"list"}
            ]}
            """) });
        var result = await new ChatGptModelCatalog(new Credentials(), () => new HttpClient(handler))
            .ListAsync(TestContext.Current.CancellationToken);
        Assert.Contains("client_version=0.159.2", handler.Requests[1].Query);
        Assert.Equal(new[] { "gpt-6.1-sol", "gpt-6-astra", "gpt-6-sol", "gpt-6-luna",
            "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna" }, result.Select(m => m.Id));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(200)]
    public async Task Existing_codex_session_uses_account_catalog_without_paid_credentials(int initialStatus)
    {
        var handler = new Handler();
        handler.Replies.Enqueue(new((HttpStatusCode)initialStatus) { Content = new StringContent("{\"models\":[]}") });
        handler.Replies.Enqueue(new(HttpStatusCode.OK) { Content = new StringContent("{\"models\":[{\"slug\":\"current\",\"visibility\":\"list\"}]}") });
        var source = new Credentials();
        var result = await new ChatGptModelCatalog(source, () => new HttpClient(handler)).ListAsync(TestContext.Current.CancellationToken);
        Assert.Equal("current", Assert.Single(result).Id);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("api.openai.com", handler.Requests[0].Host);
        Assert.Equal("chatgpt.com", handler.Requests[1].Host);
        Assert.Contains("client_version=" + ChatGptModelCatalog.CodexCatalogCompatibilityVersion, handler.Requests[1].Query);
        Assert.Equal(1, source.Gets);
        Assert.Equal(0, source.Refreshes);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    public async Task Transient_failures_do_not_trigger_provider_or_paid_fallback(int status)
    {
        var handler = new Handler();
        handler.Replies.Enqueue(new((HttpStatusCode)status) { Content = new StringContent("private error text") });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ChatGptModelCatalog(new Credentials(), () => new HttpClient(handler)).ListAsync(TestContext.Current.CancellationToken));
        Assert.Single(handler.Requests);
        Assert.DoesNotContain("private error text", ex.Message);
    }

    private sealed class Credentials : ISubscriptionCredentialSource
    {
        public int Gets, Refreshes;
        public ValueTask<SubscriptionCredential> GetAsync(CancellationToken ct) { Gets++; return ValueTask.FromResult(new SubscriptionCredential("test-access", "test-account")); }
        public ValueTask<SubscriptionCredential> RefreshAsync(CancellationToken ct) { Refreshes++; throw new InvalidOperationException("Unexpected refresh"); }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public readonly Queue<HttpResponseMessage> Replies = new();
        public readonly List<Uri> Requests = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-access", request.Headers.Authorization.Parameter);
            Assert.Equal("test-account", Assert.Single(request.Headers.GetValues("ChatGPT-Account-Id")));
            Assert.Equal("omnicore", Assert.Single(request.Headers.GetValues("originator")));
            Assert.StartsWith("omnicore/", request.Headers.UserAgent.ToString());
            Requests.Add(request.RequestUri!);
            return Task.FromResult(Replies.Dequeue());
        }
    }
}
