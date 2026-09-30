using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

public sealed class Epic009ContractTests
{
    [Fact]
    public void Execution_fingerprint_hash_is_stable_and_covers_every_component()
    {
        var baseline = Fingerprint("model", "harness", "toolset", "context", "overrides", "build", "policy", "tokenizer");
        var same = Fingerprint("model", "harness", "toolset", "context", "overrides", "build", "policy", "tokenizer");

        Assert.Equal(baseline.Hash(), same.Hash());
        Assert.Matches("^[0-9a-f]{64}$", baseline.Hash());
        Assert.NotEqual(baseline.Hash(), Fingerprint("model-2", "harness", "toolset", "context", "overrides", "build", "policy", "tokenizer").Hash());
        Assert.NotEqual(baseline.Hash(), Fingerprint("model", "harness-2", "toolset", "context", "overrides", "build", "policy", "tokenizer").Hash());
        Assert.NotEqual(baseline.Hash(), Fingerprint("model", "harness", "toolset-2", "context", "overrides", "build", "policy", "tokenizer").Hash());
        Assert.NotEqual(baseline.Hash(), Fingerprint("model", "harness", "toolset", "context-2", "overrides", "build", "policy", "tokenizer").Hash());
        Assert.NotEqual(baseline.Hash(), Fingerprint("model", "harness", "toolset", "context", "overrides-2", "build", "policy", "tokenizer").Hash());
        Assert.NotEqual(baseline.Hash(), Fingerprint("model", "harness", "toolset", "context", "overrides", "build-2", "policy", "tokenizer").Hash());
        Assert.NotEqual(baseline.Hash(), Fingerprint("model", "harness", "toolset", "context", "overrides", "build", "policy-2", "tokenizer").Hash());
        Assert.NotEqual(baseline.Hash(), Fingerprint("model", "harness", "toolset", "context", "overrides", "build", "policy", "tokenizer-2").Hash());
    }

    [Fact]
    public void Turn_started_v1_payload_still_decodes_after_schema_bump()
    {
        var codecs = EventCodecs.Create();
        var turnId = TurnId.New();
        var laneId = LaneId.New();
        var codec = codecs.CodecFor(EventType.Of("turn.started"));
        var payload = codec.Encode(new TurnStarted(turnId, laneId));
        payload = payload.Replace(",\"fingerprint\":null", "", StringComparison.Ordinal);
        var storedV1 = DomainEvent.Create(SessionId.New(), EventType.Of("turn.started"), 1,
            null, null, null, null, null, null, null, null, Array.Empty<ArtifactRef>(), payload);

        Assert.Equal(2, codecs.CurrentVersion(EventType.Of("turn.started")));
        var decoded = Assert.IsType<TurnStarted>(codecs.Decode(storedV1));
        Assert.Equal(turnId, decoded.TurnId);
        Assert.Equal(laneId, decoded.LaneId);
        Assert.Null(decoded.Fingerprint);
    }

    [Fact]
    public void Secret_serialization_fails_instead_of_leaking()
    {
        const string secretValue = "credential-that-must-never-leak";
        var secret = Secret.Of(secretValue);

        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(secret));
        Assert.Equal("***", secret.ToString());
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<Secret>("\"***\""));
    }

    [Fact]
    public void Scenario_errors_and_doctor_headings_resolve_in_spanish_and_english()
    {
        var error = Assert.Throws<ScenarioFormatException>(() => ScenarioLoader.Parse("scenario: 1\n"));
        var spanish = new Localization("es");
        var english = new Localization("en");

        var spanishError = spanish.Resolve(error.UserMessage.Key, error.UserMessage.Args);
        var englishError = english.Resolve(error.UserMessage.Key, error.UserMessage.Args);
        Assert.StartsWith("Escenario inválido:", spanishError);
        Assert.StartsWith("Invalid scenario:", englishError);
        Assert.NotEqual(spanishError, englishError);
        Assert.Equal("omni doctor — diagnóstico de M2", spanish.Resolve("doctor.heading"));
        Assert.Equal("omni doctor — M2 diagnostics", english.Resolve("doctor.heading"));
    }

    private static ExecutionFingerprint Fingerprint(string model, string harness, string toolkit,
        string context, string overrides, string build, string policy, string tokenizer) =>
        new(model, harness, toolkit, context, overrides, build, policy, tokenizer);
}
