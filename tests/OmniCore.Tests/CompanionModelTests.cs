using OmniCore.Host;
using OmniCore.Protocol;
using OmniCore.Domain;
using OmniCore.Cli;

namespace OmniCore.Tests;

public sealed class CompanionModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "omni-pet-model-" + Guid.NewGuid().ToString("N"));
    private static readonly CompanionModelOption[] Models = [new("paid-model", "paid", false), new("local-model", "local", true)];
    private CompanionCoordinator Hub() => new(_root);
    [Fact]
    public void Model_choice_is_independent_persistent_and_local_only_is_optional()
    {
        var hub = Hub(); Assert.Equal(new CompanionChatSettings(), hub.ChatSettings());
        Assert.Throws<InvalidOperationException>(() => CompanionCoordinator.SelectChatModel(hub.ChatSettings(), Models));
        hub.SaveChatSettings(new("paid-model"), Models); Assert.Equal("paid-model", Hub().ChatSettings().ModelId); Assert.False(hub.ChatSettings().LocalOnly);
        Assert.Throws<InvalidOperationException>(() => hub.SaveChatSettings(new("paid-model", true), Models));
        Assert.Equal("paid-model", hub.ChatSettings().ModelId);
        hub.SaveChatSettings(new("local-model", true), Models); Assert.True(Hub().ChatSettings().LocalOnly);
        Assert.Throws<InvalidOperationException>(() => CompanionCoordinator.SelectChatModel(hub.ChatSettings(), [Models[0]]));
        hub.SaveChatSettings(new("paid-model", false), Models); Assert.False(hub.ChatSettings().LocalOnly);
    }
    [Fact]
    public void Own_usage_deduplicates_and_does_not_double_count_cache_or_reasoning()
    {
        var a = Hub(); var b = Hub(); var id = Guid.NewGuid().ToString();
        a.BeginChatUsage(id, "paid-model", "paid"); b.BeginChatUsage(id, "paid-model", "paid");
        var response = new ModelResponse([new TextBlock("hola")], StopReason.EndTurn, new(100, 20, 40, 0, 10), null, new("test", "paid-model", null),
            ReportedProviderCostUsd: .0002m);
        var sample = OmniCliRuntime.UsageSample(id, "paid-model", "paid", response, new(2m, 5m));
        a.FinishChatUsage(sample); b.FinishChatUsage(sample); b.FinishChatUsage(sample, failed: true);
        var usage = Assert.Single(a.ChatUsage()); Assert.Equal(1, usage.Requests); Assert.Equal(1, usage.Responses); Assert.Equal(0, usage.Failed);
        Assert.Equal(120, usage.Input + usage.Output); Assert.Equal(40, usage.CacheRead); Assert.Equal(10, usage.Reasoning);
        Assert.Equal(.0003m, usage.EstimatedUsd); Assert.Equal(.0002m, usage.ReportedUsd);
        Assert.Equal(1, usage.MeasuredTokenRequests); Assert.Equal(1, usage.PricedResponses);
    }
    [Fact]
    public void Missing_usage_and_prices_stay_unknown_and_never_become_free_usage()
    {
        var hub = Hub(); var id = Guid.NewGuid().ToString(); hub.BeginChatUsage(id, "paid-model", "paid");
        var response = new ModelResponse([], StopReason.EndTurn, new(0, 0, 0, 0, 0), null, new("test", "paid-model", null), TokenUsageFields.None);
        var sample = OmniCliRuntime.UsageSample(id, "paid-model", "paid", response, new(1, 2));
        Assert.Null(sample.Input); Assert.Null(sample.EstimatedUsd); hub.FinishChatUsage(sample);
        var usage = Assert.Single(hub.ChatUsage()); Assert.Equal(0, usage.MeasuredTokenRequests); Assert.Equal(0, usage.PricedResponses);
        Assert.Contains("Sin tokens completos: 1", TuiApp.FormatCompanionUsage([usage]));
        Assert.Contains("no disponible", TuiApp.FormatCompanionUsage([usage]));
    }
    [Fact]
    public void Counter_has_no_quota_and_remains_separate_by_model()
    {
        var hub = Hub();
        for (var n = 0; n < 80; n++)
        {
            var id = Guid.NewGuid().ToString(); hub.BeginChatUsage(id, "paid-model", "paid");
            Hub().FinishChatUsage(new(id, "paid-model", "paid", Input: 50, Output: 10, EstimatedUsd: 1000m));
        }
        var failed = Guid.NewGuid().ToString(); hub.BeginChatUsage(failed, "local-model", "local");
        hub.FinishChatUsage(new(failed, "local-model", "local"), failed: true);
        var paid = Assert.Single(hub.ChatUsage(), u => u.Model == "paid-model"); Assert.Equal(80, paid.Requests); Assert.Equal(80000m, paid.EstimatedUsd);
        var local = Assert.Single(hub.ChatUsage(), u => u.Model == "local-model"); Assert.Equal(1, local.Failed);
        Assert.Equal(0, local.MeasuredTokenRequests);
    }
    public void Dispose()
    { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
