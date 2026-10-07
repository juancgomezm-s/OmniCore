using OmniCore.Domain;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

public sealed class ReasoningPreferenceStoreTests
{
    [Fact]
    public void Explicit_off_is_durable_and_distinct_from_reset()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-reasoning-pref-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "user.db");
        try
        {
            using (var store = new ReasoningPreferenceStore(database))
            {
                Assert.Equal(new UserReasoningPreference(false, null, 0), store.Read());
                Assert.Equal(new UserReasoningPreference(true, null, 1), store.Set(null, 0));
                Assert.Throws<ReasoningPreferenceConflictException>(() =>
                    store.Set(new ReasoningRequest("high", null), 0));
            }

            using (var reopened = new ReasoningPreferenceStore(database))
            {
                Assert.Equal(new UserReasoningPreference(true, null, 1), reopened.Read());
                Assert.Equal(new UserReasoningPreference(false, null, 2), reopened.Reset(1));
            }
            using var reset = new ReasoningPreferenceStore(database);
            Assert.Equal(new UserReasoningPreference(false, null, 2), reset.Read());
        }
        finally
        {
            if (File.Exists(database)) File.Delete(database);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Selected_request_reopens_with_exact_label_budget_and_revision()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-reasoning-pref-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "user.db");
        try
        {
            var selected = new UserReasoningPreference(true, new ReasoningRequest("budget", 4096), 1);
            using (var store = new ReasoningPreferenceStore(database))
                Assert.Equal(selected, store.Set(selected.Request, 0));
            using var reopened = new ReasoningPreferenceStore(database);
            Assert.Equal(selected, reopened.Read());
        }
        finally
        {
            if (File.Exists(database)) File.Delete(database);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Invalid_or_ambiguous_budget_requests_are_rejected_without_advancing_revision()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-reasoning-pref-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "user.db");
        try
        {
            using (var store = new ReasoningPreferenceStore(database))
            {
                Assert.Throws<ArgumentException>(() => store.Set(new ReasoningRequest("budget", null), 0));
                Assert.Throws<ArgumentException>(() => store.Set(new ReasoningRequest("budget", 1023), 0));
                Assert.Throws<ArgumentException>(() => store.Set(new ReasoningRequest("high", 4096), 0));
                Assert.Equal(new UserReasoningPreference(false, null, 0), store.Read());
            }

            using var reopened = new ReasoningPreferenceStore(database);
            Assert.Equal(new UserReasoningPreference(false, null, 0), reopened.Read());
        }
        finally
        {
            if (File.Exists(database)) File.Delete(database);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
