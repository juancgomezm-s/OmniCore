using System.Net;
using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

/// <summary>Opt-in wire diagnostic; synthetic tool history, NOT real tool acceptance.</summary>
public sealed class LiveQwenReceiptDiagnosticTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_receipt_survives_request_serialization_and_raw_sse(bool splitResult)
    {
        if (Environment.GetEnvironmentVariable("OMNICORE_LIVE_QWEN_RECEIPT_DIAGNOSTIC") != "1")
            Assert.Skip("Explicit receipt diagnostic opt-in required; not tool acceptance.");
        var data = Environment.GetEnvironmentVariable("OMNICORE_LIVE_ACCEPTANCE_DATA_DIR")!;
        var config = Environment.GetEnvironmentVariable("OMNICORE_LIVE_ACCEPTANCE_CONFIG_DIR")!;
        var directory = Environment.GetEnvironmentVariable("OMNICORE_LIVE_ACCEPTANCE_EVIDENCE_DIR")!;
        Assert.True(Path.IsPathFullyQualified(data) && Directory.Exists(data));
        Assert.True(Path.IsPathFullyQualified(config) && Directory.Exists(config));
        Assert.True(Path.IsPathFullyQualified(directory));
        var paths = new DefaultPlatformPaths(data, config);
        var registry = OmniHost.LoadUserModelRegistry(paths);
        var model = Assert.IsType<ModelDefinition>(registry.Model("qwen38-27b-abl-v9-best-q4kxl"));
        var descriptor = registry.Provider(model.ProviderId)!;
        Assert.Equal("https://192.168.1.104:8080/v1", descriptor.BaseUrl.TrimEnd('/'));
        Assert.Equal(ProviderFamily.OpenAiChatCompatible, descriptor.Family);
        Assert.Equal(BillingMode.Local, descriptor.BillingMode);
        Assert.Equal(LocalHostMode.Attach, descriptor.LocalHost?.Mode);
        Assert.False(string.IsNullOrWhiteSpace(descriptor.TrustedCertificatePath));
        Assert.True(File.Exists(descriptor.TrustedCertificatePath));
        var credential = OmniHost.ResolveApiKey(OmniHost.CreateUserCredentialStore(paths),
            descriptor.Auth.SecretRef!, environmentKey: null, CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(credential));

        var receipt = "RESULT-" + Guid.NewGuid().ToString("N");
        var resultJson = JsonSerializer.Serialize(new { tool = "filesystem.read", summary = "Fixture read",
            output = "fixture read content OMNICORE-LIVE-ACCEPTANCE\n[version:4b92ecdc5b2393fe91a2280afca6b547edd0111a918cd45a769d9551056a1be9]",
            verificationReceipt = receipt });
        var call = new ToolCallBlock(ToolCallId.New(), "fixture-read", "filesystem.read", "{\"path\":\"read-me.txt\"}");
        IReadOnlyList<ContentBlock> result = splitResult
            ? [new TextBlock(resultJson[..(resultJson.Length / 2)]), new TextBlock(resultJson[(resultJson.Length / 2)..])]
            : [new TextBlock(resultJson)];
        var request = new ModelRequest(new ModelSelection(new ModelIdValue(model.Id), 32768,
                ToolMode.Direct, null, maxOutputTokens: 8192),
            [new(MessageRole.User, [new TextBlock("Read the fixture file read-me.txt.")]),
                new(MessageRole.Assistant, [call]), new(MessageRole.Tool, [new ToolResultBlock(call.Id, result, false)])],
            "Explain what the returned tool result actually says and include its exact verificationReceipt. "
                + "Distinguish advisory proposals from applied actions. Do not invent other actions or call more tools.",
            [], ToolChoice.None(), null, null, null, null);
        using var handler = new WireDiagnosticHandler(resultJson,
            OmniHost.CreateTlsHandler(descriptor.BaseUrl, descriptor.TrustedCertificatePath));
        var secrets = new SimpleSecretProvider("OMNI_").With(descriptor.Auth.SecretRef!, credential!);
        var provider = new OpenAiChatCompatibleProvider(descriptor, secrets,
            () => new HttpClient(handler, false) { Timeout = TimeSpan.FromMinutes(60) },
            new OpenAiProviderOptions { MaxRetries = 0 });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(60));
        var started = DateTimeOffset.UtcNow;
        ModelResponse? completed = null;
        string? failure = null;
        try
        {
            await foreach (var item in provider.StreamAsync(request, timeout.Token))
                if (item is ResponseCompleted response) completed = response.Response;
        }
        catch (Exception exception) { failure = exception.GetType().Name; }
        var final = completed is null ? "" : string.Concat(completed.Content.OfType<TextBlock>().Select(block => block.Text));
        var exactReceipt = final.Contains(receipt, StringComparison.Ordinal);
        var wireEqualsAdapter = completed is not null && final == handler.RawVisibleText;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"qwen-receipt-split-{splitResult}.json"), JsonSerializer.Serialize(new
        {
            scope = "synthetic_history_wire_diagnostic_not_tool_acceptance", model.Id, splitResult,
            expectedReceipt = receipt, requestToolResultPreserved = handler.ResultPreserved,
            handler.HttpStatus, wireEqualsAdapter, exactReceipt, failure,
            finalTextPreview = final.Length > 1600 ? final[..1600] : final,
            stopReason = completed?.StopReason.ToString(), requests = handler.Requests,
            timeoutSeconds = 3600, configuredTlsPreserved = true, started, completedAt = DateTimeOffset.UtcNow,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(handler.ResultPreserved, "tool_result_changed_before_http_send");
        Assert.True(wireEqualsAdapter, "raw_visible_response_differs_from_adapter_or_transport_failed:" + failure);
        Assert.True(exactReceipt, "model_raw_response_did_not_return_exact_receipt");
    }

    // Observes only synthetic result equality and visible content. It never stores
    // request headers, secrets, reasoning text or provider opaque fields in evidence.
    private sealed class WireDiagnosticHandler(string expectedResult, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public bool ResultPreserved { get; private set; }
        public string RawVisibleText { get; private set; } = "";
        public int? HttpStatus { get; private set; }
        public int Requests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            using var sent = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var message = sent.RootElement.GetProperty("messages").EnumerateArray().Single(item =>
                item.GetProperty("role").GetString() == "tool");
            ResultPreserved = message.GetProperty("content").GetString() == expectedResult;
            var response = await base.SendAsync(request, cancellationToken);
            HttpStatus = (int)response.StatusCode;
            // Buffer once for independent raw/adapter comparison. Do not persist the raw body.
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var visible = new StringBuilder();
            using var lines = new StringReader(body);
            while (lines.ReadLine() is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var payload = line[5..].Trim();
                if (payload is "[DONE]" or "") continue;
                using var chunk = JsonDocument.Parse(payload);
                if (!chunk.RootElement.TryGetProperty("choices", out var choices)) continue;
                foreach (var choice in choices.EnumerateArray())
                    if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var text)
                        && text.ValueKind == JsonValueKind.String) visible.Append(text.GetString());
            }
            RawVisibleText = visible.ToString();
            response.Content.Dispose();
            response.Content = new StringContent(body, Encoding.UTF8, "text/event-stream");
            return response;
        }
    }
}
