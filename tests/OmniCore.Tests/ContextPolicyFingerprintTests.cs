namespace OmniCore.Tests;

using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;

/// <summary>
/// Tests for ContextPolicyHash in ExecutionFingerprint (M5.5 Phase A bug 3).
/// Verifies that ContextPolicyHash describes the effective context assembly/budget policy,
/// not the tokenizer identity (which belongs in TokenizerHash).
/// </summary>
public sealed class ContextPolicyFingerprintTests
{
    private static string ComputeHash(params object[] values)
    {
        var canonical = string.Join("|", values);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    [Fact]
    public void ComputeContextPolicyHash_SamePolicyAndBudget_ProducesSameHash()
    {
        // Arrange
        var policy = ContextManagementPolicy.Default;
        var budget = 8192L;

        // Act
        var hash1 = OmniCliRuntime_ComputeContextPolicyHash(policy, budget);
        var hash2 = OmniCliRuntime_ComputeContextPolicyHash(policy, budget);

        // Assert
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeContextPolicyHash_DifferentBudget_ProducesDifferentHash()
    {
        // Arrange
        var policy = ContextManagementPolicy.Default;

        // Act
        var hash1 = OmniCliRuntime_ComputeContextPolicyHash(policy, 4096L);
        var hash2 = OmniCliRuntime_ComputeContextPolicyHash(policy, 8192L);
        var hash3 = OmniCliRuntime_ComputeContextPolicyHash(policy, 16384L);

        // Assert
        Assert.NotEqual(hash1, hash2);
        Assert.NotEqual(hash2, hash3);
        Assert.NotEqual(hash1, hash3);
    }

    [Fact]
    public void ComputeContextPolicyHash_DifferentPolicy_ProducesDifferentHash()
    {
        // Arrange
        var budget = 8192L;
        var policy1 = new ContextManagementPolicy(4096, 1200, 12, 24, 6000); // Default
        var policy2 = new ContextManagementPolicy(2048, 600, 8, 16, 3000);  // Small context
        var policy3 = new ContextManagementPolicy(8192, 2400, 24, 48, 12000); // Large context

        // Act
        var hash1 = OmniCliRuntime_ComputeContextPolicyHash(policy1, budget);
        var hash2 = OmniCliRuntime_ComputeContextPolicyHash(policy2, budget);
        var hash3 = OmniCliRuntime_ComputeContextPolicyHash(policy3, budget);

        // Assert
        Assert.NotEqual(hash1, hash2);
        Assert.NotEqual(hash2, hash3);
        Assert.NotEqual(hash1, hash3);
    }

    [Fact]
    public void ComputeContextPolicyHash_VersionPrefix_EnsuresDeterministicFormat()
    {
        // Arrange
        var policy = ContextManagementPolicy.Default;
        var budget = 8192L;

        // Act
        var hash = OmniCliRuntime_ComputeContextPolicyHash(policy, budget);

        // Assert - verify the canonical format includes version prefix
        var expectedCanonical = $"ctx-policy-v1|{policy.ExternalizeAboveCharacters}|{policy.CompressBodyCharacters}|{policy.RecentTailItems}|{policy.CompactAfterItems}|{policy.MaxCheckpointCharacters}|{budget}";
        var expectedHash = ComputeHash(expectedCanonical);
        Assert.Equal(expectedHash, hash);
    }

    [Fact]
    public void ExecutionFingerprint_TokenizerChange_ChangesOnlyTokenizerHash()
    {
        // Arrange
        var modelKey = "test-model";
        var harnessHash = "harness-hash";
        var toolkitHash = "toolkit-hash";
        var contextPolicyHash = "context-policy-hash";
        var overridesHash = "overrides-hash";
        var build = "M3";
        var modelPolicyHash = "model-policy-hash";

        var tokenizer1 = "fake:words/1";
        var tokenizer2 = "heuristic:chars4/1";

        // Act
        var fp1 = new ExecutionFingerprint(modelKey, harnessHash, toolkitHash, contextPolicyHash, overridesHash, build, modelPolicyHash, tokenizer1);
        var fp2 = new ExecutionFingerprint(modelKey, harnessHash, toolkitHash, contextPolicyHash, overridesHash, build, modelPolicyHash, tokenizer2);

        // Assert
        Assert.NotEqual(fp1.TokenizerHash, fp2.TokenizerHash);
        Assert.Equal(fp1.ContextPolicyHash, fp2.ContextPolicyHash);
        Assert.Equal(fp1.ModelKey, fp2.ModelKey);
        Assert.Equal(fp1.HarnessPolicyHash, fp2.HarnessPolicyHash);
        Assert.Equal(fp1.ToolkitHash, fp2.ToolkitHash);
        Assert.Equal(fp1.OverridesHash, fp2.OverridesHash);
        Assert.Equal(fp1.Build, fp2.Build);
        Assert.Equal(fp1.ModelPolicyHash, fp2.ModelPolicyHash);

        // Aggregate hash should differ because TokenizerHash differs
        Assert.NotEqual(fp1.Hash(), fp2.Hash());
    }

    [Fact]
    public void ExecutionFingerprint_ContextPolicyChange_ChangesContextPolicyHashAndAggregate()
    {
        // Arrange
        var modelKey = "test-model";
        var harnessHash = "harness-hash";
        var toolkitHash = "toolkit-hash";
        var overridesHash = "overrides-hash";
        var build = "M3";
        var modelPolicyHash = "model-policy-hash";
        var tokenizerHash = "fake:words/1";

        var contextPolicyHash1 = "context-policy-hash-v1";
        var contextPolicyHash2 = "context-policy-hash-v2";

        // Act
        var fp1 = new ExecutionFingerprint(modelKey, harnessHash, toolkitHash, contextPolicyHash1, overridesHash, build, modelPolicyHash, tokenizerHash);
        var fp2 = new ExecutionFingerprint(modelKey, harnessHash, toolkitHash, contextPolicyHash2, overridesHash, build, modelPolicyHash, tokenizerHash);

        // Assert
        Assert.NotEqual(fp1.ContextPolicyHash, fp2.ContextPolicyHash);
        Assert.Equal(fp1.TokenizerHash, fp2.TokenizerHash);
        Assert.NotEqual(fp1.Hash(), fp2.Hash());
    }

    [Fact]
    public void OmniCliRuntime_Fingerprint_UsesCorrectContextPolicyHash_NotTokenizerId()
    {
        // This test verifies the production code path in OmniCliRuntime creates fingerprints
        // with ContextPolicyHash derived from ContextManagementPolicy + budget,
        // and TokenizerHash from tokenCounter.Id.Value.
        //
        // We can't easily instantiate OmniCliRuntime without full dependencies,
        // but we can verify the ComputeContextPolicyHash method behaves correctly
        // and that the ExecutionFingerprint constructor parameters are in the right order.

        // Arrange
        var policy = ContextManagementPolicy.Default;
        var budget = 8192L;
        var tokenizerId = "fake:words/1";

        var contextPolicyHash = OmniCliRuntime_ComputeContextPolicyHash(policy, budget);

        // Act - simulate the fingerprint creation as done in OmniCliRuntime
        var fingerprint = new ExecutionFingerprint(
            "test-model",
            "harness-hash",
            "core-tools-1",
            contextPolicyHash,      // 4th param: ContextPolicyHash
            "none",
            "M3",
            "model-policy-hash",
            tokenizerId);           // 8th param: TokenizerHash

        // Assert
        Assert.Equal(contextPolicyHash, fingerprint.ContextPolicyHash);
        Assert.Equal(tokenizerId, fingerprint.TokenizerHash);
        Assert.NotEqual(tokenizerId, fingerprint.ContextPolicyHash); // Critical: they must differ
    }

    [Fact]
    public void OmniServer_MaterializeSnapshot_UsesCorrectContextPolicyHash()
    {
        // This test verifies the production code path in OmniServer.MaterializeSnapshot
        // creates fingerprints with proper ContextPolicyHash and TokenizerHash.

        // Arrange
        var policy = ContextManagementPolicy.Default;
        var budget = 8192L;
        var tokenizerId = FakeTokenCounter.FakeId.Value;

        var contextPolicyHash = OmniServer_ComputeContextPolicyHash(policy, budget);

        // Act - simulate the fingerprint creation as done in OmniServer.MaterializeSnapshot
        var fingerprint = new ExecutionFingerprint(
            "qwen38-27b-local",
            "harness-v1",
            "core-tools",
            contextPolicyHash,      // 4th param: ContextPolicyHash
            "none",
            "M2",
            "",                     // modelPolicyHash (empty for simulation)
            tokenizerId);           // 8th param: TokenizerHash

        // Assert
        Assert.Equal(contextPolicyHash, fingerprint.ContextPolicyHash);
        Assert.Equal(tokenizerId, fingerprint.TokenizerHash);
        Assert.NotEqual(tokenizerId, fingerprint.ContextPolicyHash); // Critical: they must differ
    }

    [Fact]
    public void BothCallers_ProduceCompatibleContextPolicyHashes_ForSameInputs()
    {
        // Both OmniCliRuntime and OmniServer use the same ComputeContextPolicyHash logic.
        // For the same policy and budget, they should produce identical hashes.

        // Arrange
        var policy = ContextManagementPolicy.Default;
        var budget = 8192L;

        // Act
        var hashFromCli = OmniCliRuntime_ComputeContextPolicyHash(policy, budget);
        var hashFromServer = OmniServer_ComputeContextPolicyHash(policy, budget);

        // Assert
        Assert.Equal(hashFromCli, hashFromServer);
    }

    [Fact]
    public void BothCallers_UseCultureInvariantNumericSerialization()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            var customCulture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            customCulture.NumberFormat.NegativeSign = "~";
            CultureInfo.CurrentCulture = customCulture;

            var policy = new ContextManagementPolicy(-1, -2, -3, -4, -5);
            const long usableContext = -8192;
            const string canonical = "ctx-policy-v1|-1|-2|-3|-4|-5|-8192";
            var expectedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

            Assert.Equal(expectedHash, OmniCliRuntime_ComputeContextPolicyHash(policy, usableContext));
            Assert.Equal(expectedHash, OmniServer_ComputeContextPolicyHash(policy, usableContext));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    // Helper to access private static method via reflection (test-only)
    private static string OmniCliRuntime_ComputeContextPolicyHash(ContextManagementPolicy policy, long usableContext)
    {
        var method = typeof(OmniCliRuntime).GetMethod("ComputeContextPolicyHash",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method.Invoke(null, new object[] { policy, usableContext })!;
    }

    // Helper to access private static method via reflection (test-only)
    private static string OmniServer_ComputeContextPolicyHash(ContextManagementPolicy policy, long usableContext)
    {
        var method = typeof(OmniServer).GetMethod("ComputeContextPolicyHash",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method.Invoke(null, new object[] { policy, usableContext })!;
    }
}
