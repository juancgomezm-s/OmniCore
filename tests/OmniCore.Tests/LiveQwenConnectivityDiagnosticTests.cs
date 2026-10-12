using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OmniCore.Host;

namespace OmniCore.Tests;

public sealed class LiveQwenConnectivityDiagnosticTests
{
    [Fact]
    public async System.Threading.Tasks.Task Configured_qwen_minimal_nonstreaming_request_preserves_pinned_tls_and_existing_credentials()
    {
        if (Environment.GetEnvironmentVariable("OMNICORE_LIVE_QWEN_DIAGNOSTIC") != "1")
            Assert.Skip("Explicit diagnostic opt-in required; this is not tool acceptance.");
        var data = Environment.GetEnvironmentVariable("OMNICORE_LIVE_ACCEPTANCE_DATA_DIR")!;
        var config = Environment.GetEnvironmentVariable("OMNICORE_LIVE_ACCEPTANCE_CONFIG_DIR")!;
        Assert.True(Path.IsPathFullyQualified(data) && Directory.Exists(data));
        Assert.True(Path.IsPathFullyQualified(config) && Directory.Exists(config));
        var paths = new OmniCore.Infrastructure.DefaultPlatformPaths(data, config);
        var registry = OmniHost.LoadUserModelRegistry(paths);
        var model = registry.Model("qwen38-27b-abl-v9-best-q4kxl")!;
        var descriptor = registry.Provider(model.ProviderId)!;
        Assert.Equal("https://192.168.1.104:8080/v1", descriptor.BaseUrl.TrimEnd('/'));
        Assert.False(string.IsNullOrWhiteSpace(descriptor.TrustedCertificatePath));
        var credential = OmniHost.ResolveApiKey(OmniHost.CreateUserCredentialStore(paths),
            descriptor.Auth.SecretRef!, environmentKey: null, CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(credential));
        var start = DateTimeOffset.UtcNow;
        var status = "failed";
        int? modelsStatus = null, generationStatus = null;
        var modelPresent = false;
        long? outputTokens = null;
        string? finishReason = null, failure = null;
        using var http = new HttpClient(OmniHost.CreateTlsHandler(descriptor.BaseUrl, descriptor.TrustedCertificatePath))
            { Timeout = TimeSpan.FromMinutes(35) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        try
        {
            using var catalogResponse = await http.GetAsync(descriptor.BaseUrl.TrimEnd('/') + "/models", TestContext.Current.CancellationToken);
            modelsStatus = (int)catalogResponse.StatusCode;
            using var catalog = JsonDocument.Parse(await catalogResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            modelPresent = catalog.RootElement.GetProperty("data").EnumerateArray().Any(item => item.GetProperty("id").GetString() == model.Id);
            Assert.True(modelPresent);
            // Deliberately omit system instructions, tools and SSE to isolate generation availability.
            using var body = new StringContent(JsonSerializer.Serialize(new { model = model.Id,
                messages = new[] { new { role = "user", content = "Reply OK only." } },
                max_tokens = 16, stream = false }), Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(descriptor.BaseUrl.TrimEnd('/') + "/chat/completions", body, TestContext.Current.CancellationToken);
            generationStatus = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
                finishReason = result.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString();
                if (result.RootElement.TryGetProperty("usage", out var usage) && usage.TryGetProperty("completion_tokens", out var count))
                    outputTokens = count.GetInt64();
                status = "generation_completed_not_tool_acceptance";
            }
            else failure = "generation_http_" + generationStatus;
        }
        catch (OperationCanceledException) { failure = "bounded_2100_second_timeout"; }
        catch (Exception exception) { failure = "exception_type:" + exception.GetType().Name; }
        var directory = Environment.GetEnvironmentVariable("OMNICORE_LIVE_ACCEPTANCE_EVIDENCE_DIR")!;
        Assert.True(Path.IsPathFullyQualified(directory));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "qwen-pinned-minimal.json"), JsonSerializer.Serialize(new
        {
            modelId = model.Id, status, modelsStatus, modelPresent, generationStatus, outputTokens, finishReason, failure,
            configuredTlsPreserved = true, existingCredentialReused = true, modelOrServerModified = false,
            requestOutputCap = 16, maxGenerationRequests = 1, timeoutSeconds = 2100,
            startedAt = start, completedAt = DateTimeOffset.UtcNow,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(status == "generation_completed_not_tool_acceptance", "qwen_diagnostic:" + failure);
    }
}
