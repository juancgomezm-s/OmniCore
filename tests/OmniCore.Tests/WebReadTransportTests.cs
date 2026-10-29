using System.Net;
using System.Text;
using OmniCore.Abstractions;
using OmniCore.Host;

namespace OmniCore.Tests;

public sealed class WebReadTransportTests
{
    [Fact]
    public async Task Reads_only_text_and_removes_script_and_style_content()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(Response(
            "<html><head><style>hiddenStyle()</style><script>secret()</script></head><body><h1>API v2</h1><p>Safe &amp; current</p><noscript>hidden</noscript></body></html>",
            "text/html; charset=utf-8", lastModified: "Tue, 20 Oct 2026 00:00:00 GMT", etag: "\"build-42\"")));
        using var transport = new WebReadTransport(handler, new FixedResolver(IPAddress.Parse("93.184.216.34")));
        var response = await transport.ReadAsync(Request(), CancellationToken.None);
        Assert.Null(response.ErrorCode);
        Assert.Contains("API v2", response.Text);
        Assert.Contains("Safe & current", response.Text);
        Assert.DoesNotContain("secret", response.Text);
        Assert.DoesNotContain("hiddenStyle", response.Text);
        Assert.DoesNotContain("hidden", response.Text);
        Assert.Null(response.PublishedAt);
        Assert.NotNull(response.UpdatedAt);
        Assert.Equal("\"build-42\"", response.Version);
        Assert.False(response.Truncated);
        Assert.Equal("http_get", response.Method);
        Assert.Equal("https://docs.example/api", response.OriginalUrl);
        Assert.Equal(response.OriginalUrl, response.FinalUrl);
    }

    [Fact]
    public async Task Reads_at_most_maximum_bytes_plus_one_and_reports_truncation()
    {
        var content = new CountingContent(1024);
        using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var transport = new WebReadTransport(handler, new FixedResolver(IPAddress.Parse("93.184.216.34")));
        var response = await transport.ReadAsync(Request(maxBytes: 100), CancellationToken.None);
        Assert.True(response.Truncated);
        Assert.True(Encoding.UTF8.GetByteCount(response.Text) <= 100);
        Assert.True(content.BytesSupplied <= 101);
    }

    [Fact]
    public async Task Redirect_is_reported_and_never_followed_even_when_limit_allows_it()
    {
        var calls = 0;
        using var handler = new StubHandler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://outside.example/private");
            return Task.FromResult(response);
        });
        using var transport = new WebReadTransport(handler, new FixedResolver(IPAddress.Parse("93.184.216.34")));
        var response = await transport.ReadAsync(Request(maxRedirects: 3), CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.Equal("redirect_requires_authorization", response.ErrorCode);
        Assert.Equal("https://outside.example/private", response.RedirectChain[^1]);
        Assert.Equal("https://docs.example/api", response.FinalUrl);
        Assert.Equal("", response.Text);
    }

    [Fact]
    public async Task Rejects_private_and_mixed_public_private_dns_answers_before_http()
    {
        foreach (var values in new[]
        {
            new[] { IPAddress.Parse("127.0.0.1") },
            new[] { IPAddress.Parse("93.184.216.34"), IPAddress.Parse("10.0.0.8") },
            new[] { IPAddress.Parse("::ffff:192.168.1.10") },
            new[] { IPAddress.Parse("192.88.99.1") },
            new[] { IPAddress.Parse("2001::1") },
            new[] { IPAddress.Parse("2001:db8::1") },
            new[] { IPAddress.Parse("2002::1") },
            new[] { IPAddress.Parse("3fff::1") },
            new[] { new IPAddress(IPAddress.Parse("2606:4700::1111").GetAddressBytes(), 1) },
        })
        {
            var calls = 0;
            using var handler = new StubHandler((_, _) => { calls++; return Task.FromResult(Response("unexpected", "text/plain")); });
            using var transport = new WebReadTransport(handler, new FixedResolver(values));
            var response = await transport.ReadAsync(Request(), CancellationToken.None);
            Assert.Equal("unsafe_resolved_address", response.ErrorCode);
            Assert.Equal(0, calls);
        }
    }

    [Fact]
    public async Task Allows_public_global_unicast_ipv6_address()
    {
        var calls = 0;
        using var handler = new StubHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(Response("public IPv6 fixture", "text/plain"));
        });
        using var transport = new WebReadTransport(handler, new FixedResolver(IPAddress.Parse("2606:4700::1111")));
        var response = await transport.ReadAsync(Request(), CancellationToken.None);
        Assert.Null(response.ErrorCode);
        Assert.Equal("public IPv6 fixture", response.Text);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Rejects_non_https_and_non_allowlisted_origins_without_dns_or_http()
    {
        var resolver = new CountingResolver(IPAddress.Parse("93.184.216.34"));
        var calls = 0;
        using var handler = new StubHandler((_, _) => { calls++; return Task.FromResult(Response("unexpected", "text/plain")); });
        using var transport = new WebReadTransport(handler, resolver);
        var http = await transport.ReadAsync(new WebReadRequest(new Uri("http://docs.example/api"), 100, 0, ["http://docs.example"]), CancellationToken.None);
        var wrongOrigin = await transport.ReadAsync(new WebReadRequest(new Uri("https://other.example/api"), 100, 0, ["https://docs.example"]), CancellationToken.None);
        Assert.Equal("invalid_or_disallowed_target", http.ErrorCode);
        Assert.Equal("invalid_or_disallowed_target", wrongOrigin.ErrorCode);
        Assert.Equal(0, resolver.Calls);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Rejects_pdf_and_other_non_text_media_without_claiming_extraction()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(Response("%PDF-1.7", "application/pdf")));
        using var transport = new WebReadTransport(handler, new FixedResolver(IPAddress.Parse("93.184.216.34")));
        var response = await transport.ReadAsync(Request(), CancellationToken.None);
        Assert.Equal("unsupported_content_type", response.ErrorCode);
        Assert.Equal("", response.Text);
    }

    [Fact]
    public async Task Rejects_missing_dns_answers()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(Response("unexpected", "text/plain")));
        using var transport = new WebReadTransport(handler, new FixedResolver([]));
        var response = await transport.ReadAsync(Request(), CancellationToken.None);
        Assert.Equal("unsafe_resolved_address", response.ErrorCode);
    }

    private static WebReadRequest Request(int maxBytes = 4096, int maxRedirects = 0) => new(
        new Uri("https://docs.example/api"), maxBytes, maxRedirects, ["https://docs.example"]);

    private static HttpResponseMessage Response(string text, string contentType, string? lastModified = null, string? etag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, contentType.Split(';')[0]) };
        if (contentType.Contains(';')) response.Content.Headers.ContentType!.CharSet = contentType.Split("charset=")[1];
        if (lastModified is not null) response.Content.Headers.LastModified = DateTimeOffset.Parse(lastModified);
        if (etag is not null) response.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(etag);
        return response;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class FixedResolver(params IPAddress[] addresses) : IWebHostResolver
    {
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<IPAddress>>(addresses);
    }

    private sealed class CountingResolver(IPAddress address) : IWebHostResolver
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult<IReadOnlyList<IPAddress>>([address]); }
    }

    private sealed class CountingContent : HttpContent
    {
        private readonly int _length;
        public CountingContent(int length)
        {
            _length = length;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain") { CharSet = "utf-8" };
        }
        public int BytesSupplied { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override bool TryComputeLength(out long length) { length = _length; return true; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new CountingStream(this));
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => Task.FromResult<Stream>(new CountingStream(this));

        private sealed class CountingStream : MemoryStream
        {
            private readonly CountingContent _owner;
            public CountingStream(CountingContent owner) : base(new byte[owner._length]) => _owner = owner;
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var read = await base.ReadAsync(buffer, cancellationToken); _owner.BytesSupplied += read; return read;
            }
        }
    }
}
