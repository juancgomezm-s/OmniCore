using Microsoft.Data.Sqlite;
using OmniCore.Host;

namespace OmniCore.Tests;

/// <summary>Private configuration only: no provider is constructed, no authenticated measurement.</summary>
public sealed class QualificationCostPreviewTests
{
    [Theory]
    [InlineData("Unknown")]
    [InlineData("IncludedQuota")]
    [InlineData("CreditBalance")]
    [InlineData("MeteredCurrency")]
    public void Missing_prices_do_not_turn_a_zero_probe_declaration_into_a_zero_cost_preview(string billing)
    {
        WithHost(billing, priced: false, host =>
        {
            var preview = host.PreviewSuiteCost("preview-model", "quick");
            Assert.Null(preview.Usd);
            Assert.Equal("unavailable", preview.Source);
        });
    }

    [Fact]
    public void Explicit_local_preview_is_a_declaration_not_a_measured_charge()
    {
        WithHost("Local", priced: false, host =>
        {
            var preview = host.PreviewSuiteCost("preview-model", "quick");
            Assert.Equal(0m, preview.Usd);
            Assert.Equal("declared-local-probe-maxima", preview.Source);
        });
    }

    [Fact]
    public void Configured_preview_covers_all_ten_probes_and_exposes_its_source()
    {
        WithHost("MeteredCurrency", priced: true, host =>
        {
            var preview = host.PreviewSuiteCost("preview-model", "quick");
            // Ten probes, three configured generation sends each (initial + two retries).
            Assert.Equal(0.983040m, preview.Usd);
            Assert.Equal("max-declared-and-configured-descriptor-token-estimate", preview.Source);
            Assert.Throws<ModelQualificationUnsupportedSuiteException>(() =>
                host.PreviewSuiteCost("preview-model", "full"));
        });
    }

    private static void WithHost(string billing, bool priced, Action<ModelQualificationHost> verify)
    {
        var directory = Path.Combine(Path.GetTempPath(), "omnicore-qualification-cost-preview",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var paths = OmniHost.CreatePlatformPaths(directory);
            Directory.CreateDirectory(paths.ConfigDirectory);
            var prices = priced ? "    inputPricePerMillionUsd: 2\n    outputPricePerMillionUsd: 8\n" : "";
            File.WriteAllText(Path.Combine(paths.ConfigDirectory, "providers.yaml"),
                "providers:\n  preview-provider:\n    baseUrl: https://fixture.invalid/v1\n"
                + "    auth: none\n    billingMode: " + billing + "\n" + prices);
            File.WriteAllText(Path.Combine(paths.ConfigDirectory, "models.yaml"),
                "models:\n  preview-model:\n    provider: preview-provider\n    context: 8192\n"
                + "    recommendedUsableContext: 8192\n    maxOutput: 2048\n");
            using var host = ModelQualificationHost.Create(directory);
            verify(host);
        }
        finally
        {
            using var pool = new SqliteConnection("DataSource=" + Path.Combine(directory, "user.db"));
            SqliteConnection.ClearPool(pool);
            Directory.Delete(directory, recursive: true);
        }
    }
}
