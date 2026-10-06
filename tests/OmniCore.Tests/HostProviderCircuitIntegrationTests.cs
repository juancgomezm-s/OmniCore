namespace OmniCore.Tests;

using System.Net;
using System.Net.Sockets;
using System.Text;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Models;

/// <summary>Real Host factory and loopback HTTP errors; no authenticated provider or consumption.</summary>
public sealed class HostProviderCircuitIntegrationTests
{
    [Theory]
    [InlineData(ProviderFamily.OpenAiChatCompatible)]
    [InlineData(ProviderFamily.OpenAIResponses)]
    [InlineData(ProviderFamily.AnthropicMessages)]
    public async System.Threading.Tasks.Task Host_factory_preserves_open_circuit_across_adapter_instances(ProviderFamily family)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var count = 0;
        var serving = Serve();
        var endpoint = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1";
        var descriptor = new ProviderDescriptor("fixture-provider", family, endpoint, AuthConfig.None(), false, false, true);
        var catalog = new ProviderResilienceCatalog();
        var request = new ModelRequest(new ModelSelection(new ModelIdValue("fixture-model"), 4096, ToolMode.Direct, null),
            [new ModelMessage(MessageRole.User, [new TextBlock("hello")])], null, [], ToolChoice.Auto(), null, null, null, null);
        try
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var adapter = OmniHost.ConnectProvider(descriptor, endpoint, "unused", "", circuits: catalog);
                await Assert.ThrowsAsync<ModelProviderException>(async () =>
                {
                    await foreach (var unused in adapter.StreamAsync(request, stop.Token)) { }
                });
            }
            Assert.Equal(5, Volatile.Read(ref count));
            Assert.False(catalog.Snapshot(descriptor.Id)!.CanAttempt);
            var next = OmniHost.ConnectProvider(descriptor, endpoint, "unused", "", circuits: catalog);
            var error = await Assert.ThrowsAsync<ModelProviderException>(async () =>
            {
                await foreach (var unused in next.StreamAsync(request, stop.Token)) { }
            });
            Assert.Equal("ProviderUnavailable", error.Kind);
            Assert.Equal(5, Volatile.Read(ref count));
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            try { await serving; } catch (OperationCanceledException) { }
        }

        async System.Threading.Tasks.Task Serve()
        {
            while (!stop.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
                var length = 0;
                while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 } header)
                    if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(header.AsSpan("Content-Length:".Length).Trim(), out length);
                var body = new char[length];
                var read = 0;
                while (read < length)
                {
                    var received = await reader.ReadAsync(body.AsMemory(read), stop.Token);
                    if (received == 0) break;
                    read += received;
                }
                Interlocked.Increment(ref count);
                var bytes = Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}");
                await stream.WriteAsync(bytes, stop.Token);
                await stream.FlushAsync(stop.Token);
            }
        }
    }
}
