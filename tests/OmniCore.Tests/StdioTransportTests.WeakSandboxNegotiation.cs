using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed partial class StdioTransportTests
{
    private const string ChoiceCapability = "command.conversation.interaction.choice.respond.user";
    private const string WeakSandboxCapability = "command.conversation.weak-sandbox.respond.user";
    private const string QuestionnaireCapability = "command.conversation.questionnaire.respond.user";

    [Fact]
    public void Trusted_legacy_hello_without_native_choice_does_not_advertise_weak_sandbox_consent()
    {
        using var fixture = new WeakNegotiationFixture();
        var capabilities = fixture.Negotiate(requestedCapabilities: null);

        Assert.Contains(QuestionnaireCapability, capabilities);
        Assert.DoesNotContain(ChoiceCapability, capabilities);
        Assert.DoesNotContain(WeakSandboxCapability, capabilities);
        Assert.DoesNotContain("query.interactionRecovery", capabilities);
    }

    [Fact]
    public void Weak_sandbox_consent_requires_explicit_opt_in_in_addition_to_native_choice()
    {
        using var fixture = new WeakNegotiationFixture();

        var choiceOnly = fixture.Negotiate([ChoiceCapability]);
        Assert.Contains(ChoiceCapability, choiceOnly);
        Assert.DoesNotContain(WeakSandboxCapability, choiceOnly);

        var optedIn = fixture.Negotiate([ChoiceCapability, WeakSandboxCapability]);
        Assert.Contains(QuestionnaireCapability, optedIn);
        Assert.Contains(ChoiceCapability, optedIn);
        Assert.Contains(WeakSandboxCapability, optedIn);
        Assert.Contains("query.interactionRecovery", optedIn);
    }

    private sealed class WeakNegotiationFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "omnicore-weak-negotiation-" + Guid.NewGuid().ToString("N"));
        private readonly string _workspace;
        private readonly string _data;
        private readonly string? _oldData;
        private readonly string? _oldConfig;
        private readonly string _oldDirectory;
        private readonly byte[] _bootstrapKey = RandomNumberGenerator.GetBytes(32);

        public WeakNegotiationFixture()
        {
            _workspace = Path.Combine(_root, "workspace");
            _data = Path.Combine(_root, "data");
            var config = Path.Combine(_root, "config");
            Directory.CreateDirectory(_workspace);
            Directory.CreateDirectory(_data);
            Directory.CreateDirectory(config);

            _oldData = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
            _oldConfig = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
            _oldDirectory = Environment.CurrentDirectory;
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", _data);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", config);
            Environment.CurrentDirectory = _workspace;

            var canonicalWorkspace = ProjectIdentity.CanonicalWorkspacePath(_workspace);
            var trustId = WorkspaceId.Of(canonicalWorkspace).ToString();
            File.WriteAllText(Path.Combine(_data, "trust.yaml"),
                "workspaces:\n  " + trustId + ":\n    canonicalPath: " + canonicalWorkspace + "\n    trusted: true\n",
                new UTF8Encoding(false));
        }

        public HashSet<string> Negotiate(IReadOnlyList<string>? requestedCapabilities)
        {
            var cliDll = Path.Combine(FindRepositoryRoot(), "src", "OmniCore.Cli", "bin", "Debug", "net10.0", "omni.dll");
            using var host = StartRawHost(cliDll, _workspace, Convert.ToHexString(_bootstrapKey));
            try
            {
                var payload = requestedCapabilities is null
                    ? "{\"supportedVersions\":[\"1.0\"]}"
                    : JsonSerializer.Serialize(new { supportedVersions = new[] { "1.0" }, capabilities = requestedCapabilities });
                var hello = RawExchange(host, WireEnvelope.FromJson(
                    "{\"version\":\"1.0\",\"messageType\":\"hello\",\"messageId\":\"weak-cap-hello\",\"payload\":"
                    + payload + "}"));
                Assert.Equal(MessageTypes.Response, hello.MessageType);
                using var response = JsonDocument.Parse(hello.PayloadJson);
                Assert.True(response.RootElement.TryGetProperty("trustedActionNonce", out _));
                return response.RootElement.GetProperty("capabilities").EnumerateArray()
                    .Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
            }
            finally
            {
                try { if (!host.HasExited) host.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                try { host.WaitForExit(5000); }
                catch (InvalidOperationException) { }
            }
        }

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(_bootstrapKey);
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", _oldData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", _oldConfig);
            Environment.CurrentDirectory = _oldDirectory;
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
