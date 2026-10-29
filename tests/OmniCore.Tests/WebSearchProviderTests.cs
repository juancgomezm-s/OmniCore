using System.Net;
using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Host;

namespace OmniCore.Tests;

public sealed class WebSearchProviderTests
{
    [Fact]
    public async Task Brave_maps_supported_filters_and_keeps_candidate_urls_verbatim()
    {
        using var handler = new StubHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("fixture-key", request.Headers.GetValues("X-Subscription-Token").Single());
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            Assert.Equal("official api behavior", query["q"]);
            Assert.Equal("4", query["count"]);
            Assert.Equal("pw", query["freshness"]);
            return Task.FromResult(Json("""{"web":{"results":[{"title":"Primary report","url":"https://machinelearning.example/research?ref=1","description":"A snippet","age":"2 days ago"}]}}"""));
        });
        var provider = new BraveWebSearchProvider(new HttpClient(handler), () => "fixture-key", new Uri("https://search.fixture.invalid/web"));
        var response = await provider.SearchAsync(Request(filters: new Dictionary<string, string> { ["freshness"] = "pw" },
            requiredDomains: ["machinelearning.example"], primary: true), CancellationToken.None);

        var candidate = Assert.Single(response.Candidates);
        Assert.Equal("https://machinelearning.example/research?ref=1", candidate.Url);
        Assert.Equal("A snippet", candidate.Snippet);
        Assert.Contains("freshness", response.AppliedFilters);
        Assert.Contains("requiredDomains", response.UnsupportedFilters);
        Assert.Contains("preferPrimarySources", response.UnsupportedFilters);
    }

    [Fact]
    public async Task Tavily_sends_required_domains_as_restrictive_filter_and_parses_published_date()
    {
        using var handler = new StubHandler(async (request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer tavily-fixture", request.Headers.Authorization?.ToString());
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("systems design", body.RootElement.GetProperty("query").GetString());
            Assert.Equal("restrict", body.RootElement.GetProperty("include_domains_mode").GetString());
            Assert.Equal("standards.example", body.RootElement.GetProperty("include_domains")[0].GetString());
            Assert.False(body.RootElement.GetProperty("include_raw_content").GetBoolean());
            return Json("""{"results":[{"title":"Standard","url":"https://standards.example/v2","content":"Version 2","published_date":"2026-10-20","score":0.9,"raw_content":null}]}""");
        });
        var provider = new TavilyWebSearchProvider(new HttpClient(handler), () => "tavily-fixture", new Uri("https://search.fixture.invalid/tavily"));
        var response = await provider.SearchAsync(Request(query: "systems design", requiredDomains: ["standards.example"], filters: new Dictionary<string, string> { ["search_depth"] = "advanced" }), CancellationToken.None);
        var candidate = Assert.Single(response.Candidates);
        Assert.Equal("2026-10-20", candidate.PublishedAt);
        Assert.Contains("requiredDomains", response.AppliedFilters);
        Assert.Contains("search_depth", response.AppliedFilters);
        Assert.Empty(response.UnsupportedFilters);
    }

    [Fact]
    public async Task Exa_maps_domains_and_preserves_provider_version_fields()
    {
        using var handler = new StubHandler(async (request, _) =>
        {
            Assert.Equal("exa-fixture", request.Headers.GetValues("x-api-key").Single());
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("official api behavior", body.RootElement.GetProperty("query").GetString());
            Assert.Equal("docs.example", body.RootElement.GetProperty("includeDomains")[0].GetString());
            return Json("""{"results":[{"title":"API docs","url":"https://docs.example/v3","publishedDate":"2026-10-01T00:00:00Z","updatedDate":"2026-10-15T00:00:00Z","version":"v3","text":"Current behavior","highlights":["Current behavior"]}]}""");
        });
        var provider = new ExaWebSearchProvider(new HttpClient(handler), () => "exa-fixture", new Uri("https://search.fixture.invalid/exa"));
        var response = await provider.SearchAsync(Request(requiredDomains: ["docs.example"], primary: true), CancellationToken.None);
        var candidate = Assert.Single(response.Candidates);
        Assert.Equal("v3", candidate.Version);
        Assert.Equal("2026-10-15T00:00:00Z", candidate.UpdatedAt);
        Assert.Equal("Current behavior", candidate.Snippet);
        Assert.Contains("requiredDomains", response.AppliedFilters);
        Assert.Contains("preferPrimarySources", response.UnsupportedFilters);
    }

    [Fact]
    public async Task Provider_failure_is_structured_and_does_not_retry()
    {
        var calls = 0;
        using var handler = new StubHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        });
        var provider = new ExaWebSearchProvider(new HttpClient(handler), () => "fixture", new Uri("https://search.fixture.invalid/exa"));
        var response = await provider.SearchAsync(Request(), CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.Equal("rate_limited", response.ErrorCode);
        Assert.True(response.Retryable);
    }

    [Fact]
    public async Task Missing_credential_fails_before_dispatch()
    {
        var calls = 0;
        using var handler = new StubHandler((_, _) => { calls++; return Task.FromResult(Json("{}")); });
        var provider = new BraveWebSearchProvider(new HttpClient(handler), () => null, new Uri("https://search.fixture.invalid/web"));
        var response = await provider.SearchAsync(Request(), CancellationToken.None);
        Assert.Equal(0, calls);
        Assert.Equal("credentials_unavailable", response.ErrorCode);
    }

    [Fact]
    public async Task Tavily_notice_reports_relaxed_required_domain_as_unsupported()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(Json(
            """{"results":[],"notices":[{"code":"RESTRICTIONS_RELAXED","path":"/query","message":"fixture"}]}""")));
        var provider = new TavilyWebSearchProvider(new HttpClient(handler), () => "fixture", new Uri("https://search.fixture.invalid/tavily"));
        var response = await provider.SearchAsync(Request(requiredDomains: ["standards.example"]), CancellationToken.None);
        Assert.DoesNotContain("requiredDomains", response.AppliedFilters);
        Assert.Contains("requiredDomains", response.UnsupportedFilters);
    }

    [Fact]
    public async Task Exa_warning_reports_relaxed_required_domain_as_unsupported()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(Json(
            """{"results":[],"warnings":[{"code":"RESTRICTIONS_RELAXED","path":"/includeDomains","message":"fixture"}]}""")));
        var provider = new ExaWebSearchProvider(new HttpClient(handler), () => "fixture", new Uri("https://search.fixture.invalid/exa"));
        var response = await provider.SearchAsync(Request(requiredDomains: ["docs.example"]), CancellationToken.None);
        Assert.DoesNotContain("requiredDomains", response.AppliedFilters);
        Assert.Contains("requiredDomains", response.UnsupportedFilters);
    }

    private static WebSearchRequest Request(string query = "official api behavior", IReadOnlyDictionary<string, string>? filters = null,
        IReadOnlyList<string>? requiredDomains = null, bool primary = false) => new(
        query, "verify versioned API claims", 4, requiredDomains ?? [], primary, filters ?? new Dictionary<string, string>());

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        { Content = new StringContent(value, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
