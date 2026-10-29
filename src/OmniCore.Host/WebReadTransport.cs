namespace OmniCore.Host;

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using OmniCore.Abstractions;

/// <summary>Injectable DNS seam; production connections are pinned to validated addresses.</summary>
public interface IWebHostResolver
{
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);
}

public sealed class SystemWebHostResolver : IWebHostResolver
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal)) return [literal];
        return await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Bounded read-only HTTPS transport. The default client disables redirects and pins its socket
/// to a public address resolved and validated for the requested hostname.
/// </summary>
public sealed class WebReadTransport : IWebReadTransport, IDisposable
{
    private const int MaximumAllowedBytes = 2_097_152;
    private readonly HttpClient _client;
    private readonly IWebHostResolver _resolver;
    private readonly TimeProvider _clock;
    private readonly bool _ownsClient;

    public WebReadTransport() : this(new SystemWebHostResolver(), TimeProvider.System) { }

    public WebReadTransport(IWebHostResolver resolver, TimeProvider? clock = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _clock = clock ?? TimeProvider.System;
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
        handler.ConnectCallback = ConnectPinnedAsync;
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
        _ownsClient = true;
    }

    // Scripted HTTP is deliberately internal: composition cannot accidentally substitute an
    // ordinary HttpClient whose automatic redirects or DNS behavior bypass the pinning policy.
    internal WebReadTransport(HttpMessageHandler fixtureHandler, IWebHostResolver resolver, TimeProvider? clock = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _clock = clock ?? TimeProvider.System;
        _client = new HttpClient(fixtureHandler ?? throw new ArgumentNullException(nameof(fixtureHandler)), disposeHandler: true)
            { Timeout = TimeSpan.FromSeconds(30) };
        _ownsClient = true;
    }

    public async Task<WebReadResponse> ReadAsync(WebReadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Uri? requestedUrl = request.Url;
        var original = requestedUrl?.AbsoluteUri ?? "";
        var chain = new List<string>();
        if (!ValidLimits(request) || !TryValidateTarget(requestedUrl, request.AllowedOrigins, out _))
            return Failure(original, "invalid_or_disallowed_target", chain);
        var url = requestedUrl!;
        chain.Add(url.AbsoluteUri);

        IReadOnlyList<IPAddress> addresses;
        try
        {
            addresses = await _resolver.ResolveAsync(url.Host, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is SocketException or ArgumentException or InvalidOperationException)
        { return Failure(original, "dns_resolution_failed", chain); }
        if (!AreAllPublic(addresses)) return Failure(original, "unsafe_resolved_address", chain);

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (IsRedirect(response.StatusCode))
            {
                var target = response.Headers.Location is { } location
                    ? location.IsAbsoluteUri ? location : new Uri(url, location)
                    : null;
                if (target is not null) chain.Add(target.AbsoluteUri);
                return Failure(original, "redirect_requires_authorization", chain);
            }
            if (!response.IsSuccessStatusCode) return Failure(original, "http_" + (int)response.StatusCode, chain);

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType is null || !(contentType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
                || contentType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase)
                || contentType.Equals("text/plain", StringComparison.OrdinalIgnoreCase)))
                return Failure(original, "unsupported_content_type", chain);

            var maximum = request.MaximumBytes;
            var declaredTooLarge = response.Content.Headers.ContentLength > maximum;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var bytes = await ReadBoundedAsync(stream, maximum, cancellationToken).ConfigureAwait(false);
            var truncated = declaredTooLarge || bytes.Length > maximum;
            if (truncated) Array.Resize(ref bytes, maximum);
            string text;
            try { text = Decode(bytes, response.Content.Headers.ContentType?.CharSet); }
            catch (ArgumentException) { return Failure(original, "unsupported_character_encoding", chain); }
            if (contentType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
                || contentType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
                text = ExtractHtmlText(text);
            var final = url.AbsoluteUri;
            var updated = response.Content.Headers.LastModified?.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            var version = response.Headers.ETag?.ToString();
            return new WebReadResponse(original, final, text, "http_get", _clock.GetUtcNow(), chain, truncated,
                PublishedAt: null, UpdatedAt: updated, Version: version);
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException) { return Failure(original, "network_error", chain); }
        catch (IOException) { return Failure(original, "read_failed", chain); }
    }

    private async ValueTask<Stream> ConnectPinnedAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = await _resolver.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
        if (!AreAllPublic(addresses)) throw new HttpRequestException("The resolved web host is not public.");
        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException)
            {
                socket.Dispose(); last = exception;
                if (exception is OperationCanceledException) throw;
            }
        }
        throw new HttpRequestException("Could not connect to a validated public address.", last);
    }

    private static bool ValidLimits(WebReadRequest request) => request.MaximumBytes is >= 1 and <= MaximumAllowedBytes
        && request.MaximumRedirects is >= 0 and <= 10 && request.AllowedOrigins is { Count: > 0 and <= 128 };

    private static bool TryValidateTarget(Uri? uri, IReadOnlyList<string> allowedOrigins, out string error)
    {
        error = "";
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
        { error = "invalid_uri"; return false; }
        var origin = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        if (!allowedOrigins.Any(item => string.Equals(item.TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase)))
        { error = "origin_not_allowed"; return false; }
        return true;
    }

    private static bool AreAllPublic(IReadOnlyList<IPAddress> addresses) => addresses.Count > 0 && addresses.All(IsPublicAddress);

    private static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.IPv6None) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            return false;
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224
                || b[0] == 100 && b[1] is >= 64 and <= 127
                || b[0] == 169 && b[1] == 254
                || b[0] == 172 && b[1] is >= 16 and <= 31
                || b[0] == 192 && b[1] == 168
                || b[0] == 192 && b[1] == 0 && b[2] == 0
                || b[0] == 192 && b[1] == 0 && b[2] == 2
                || b[0] == 192 && b[1] == 88 && b[2] == 99
                || b[0] == 198 && b[1] is 18 or 19
                || b[0] == 198 && b[1] == 51 && b[2] == 100
                || b[0] == 203 && b[1] == 0 && b[2] == 113);
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Deliberately conservative local policy: allow only global-unicast 2000::/3,
            // with known special-use, transition and documentation ranges excluded below.
            // This is a safety filter, not a claim to mirror the complete IANA registry.
            if (address.ScopeId != 0 || (b[0] & 0xe0) != 0x20) return false;
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] <= 0x01) return false; // 2001::/23
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return false; // 2001:db8::/32
            if (b[0] == 0x20 && b[1] == 0x02) return false; // 2002::/16 (6to4)
            if (b[0] == 0x3f && b[1] == 0xff && (b[2] & 0xf0) == 0) return false; // 3fff::/20
            return true;
        }
        return false;
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(Math.Min(maximum + 1, 64 * 1024));
        var buffer = new byte[16 * 1024];
        while (output.Length <= maximum)
        {
            var count = (int)Math.Min(buffer.Length, maximum + 1L - output.Length);
            var read = await stream.ReadAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static string Decode(byte[] bytes, string? charset)
    {
        Encoding encoding;
        try { encoding = string.IsNullOrWhiteSpace(charset) ? new UTF8Encoding(false, true) : Encoding.GetEncoding(charset.Trim(' ', '"'), EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback); }
        catch (ArgumentException) { throw; }
        return encoding.GetString(bytes);
    }

    private static string ExtractHtmlText(string html)
    {
        html = Regex.Replace(html, "<!--.*?-->", " ", RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        html = Regex.Replace(html, "<(script|style|noscript|template)\\b[^>]*>.*?</\\1\\s*>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        html = Regex.Replace(html, "<(br|/p|/div|/li|/h[1-6]|/tr|/section|/article)\\b[^>]*>", "\n", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        html = Regex.Replace(html, "<[^>]{0,4096}>", " ", RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        html = WebUtility.HtmlDecode(html);
        return Regex.Replace(html, "[\\t\\r ]+|\\n{3,}", match => match.Value[0] == '\n' ? "\n\n" : " ", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)).Trim();
    }

    private WebReadResponse Failure(string original, string code, IReadOnlyList<string> chain) =>
        new(original, original, "", "http_get", _clock.GetUtcNow(), chain, false, ErrorCode: code);

    private static bool IsRedirect(HttpStatusCode code) => code is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect
        or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}
