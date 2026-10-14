namespace OmniCore.Host;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Extensions;

/// <summary>Durably activates one eligible/model-invocable project skill for the next turn.</summary>
public sealed class SkillLoadTool : ITool
{
    private const string Schema = "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":128}},\"required\":[\"id\"],\"additionalProperties\":false}";
    private readonly SessionId _session;
    private readonly SessionSkillActivationStore _store;
    private readonly SkillManifestLoader _loader;
    private readonly Func<IReadOnlyList<DiscoveredSkill>> _candidateProvider;
    private readonly SkillSelectionLimits _limits;
    private readonly SkillSelector _selector = new();
    private readonly ToolDescriptor _descriptor = new(new ToolId("skill.load"),
        "Load an eligible skill for the next turn.", new InputSchema(Schema), ["skill", "load"],
        readOnly: false, destructive: false, ToolRisk.Low, ComponentSource.Core(), ToolProtection.None);

    public SkillLoadTool(SessionId session, SessionSkillActivationStore store,
        SkillManifestLoader loader, IReadOnlyList<DiscoveredSkill> candidates,
        SkillSelectionLimits? limits = null)
    {
        _session = session;
        _store = store;
        _loader = loader;
        var snapshot = candidates.ToArray();
        _candidateProvider = () => snapshot;
        _limits = limits ?? new SkillSelectionLimits(1, 16_384);
    }

    public SkillLoadTool(SessionId session, SessionSkillActivationStore store,
        SkillManifestLoader loader, Func<IReadOnlyList<DiscoveredSkill>> candidateProvider,
        SkillSelectionLimits? limits = null)
    {
        _session = session;
        _store = store;
        _loader = loader;
        _candidateProvider = candidateProvider ?? throw new ArgumentNullException(nameof(candidateProvider));
        _limits = limits ?? new SkillSelectionLimits(1, 16_384);
    }

    public ToolDescriptor Descriptor => _descriptor;

    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        string? id;
        try
        {
            using var document = JsonDocument.Parse(call.NormalizedArgumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || document.RootElement.EnumerateObject().Count() != 1
                || !document.RootElement.TryGetProperty("id", out var idNode)
                || idNode.ValueKind != JsonValueKind.String)
                return new PreparationRejected("skill.load requires one string id.", null);
            id = idNode.GetString();
        }
        catch (JsonException)
        {
            return new PreparationRejected("skill.load arguments are malformed.", null);
        }
        if (string.IsNullOrWhiteSpace(id)) return new PreparationRejected("skill.load requires a nonempty id.", null);
        var candidates = _candidateProvider();
        var selection = _selector.Select(candidates, SkillSelectionRequestKind.ModelExplicit,
            _limits, id);
        if (selection.Selected.Count != 1)
            return new PreparationRejected("Skill is not eligible for model invocation.", null);
        return new Prepared(new ToolIntent(call.ToolCallId, call.ToolId, "{\"id\":\""
            + JsonEncodedText.Encode(id).ToString() + "\"}", EffectClass.None, ResourceClaims.Empty(),
            ToolRisk.Low, null));
    }

    public Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse(intent.Intent.NormalizedArgumentsJson);
        var id = document.RootElement.GetProperty("id").GetString()!;
        var candidates = _candidateProvider();
        var matches = candidates.Where(item => string.Equals(item.Manifest.Id, id, StringComparison.Ordinal)
            && item.State is not (SkillLifecycleState.Conflict or SkillLifecycleState.Released)).ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException("Skill identity is ambiguous; no activation was recorded.");
        var selected = _selector.Select(matches, SkillSelectionRequestKind.ModelExplicit,
            _limits, id);
        var candidate = selected.Selected.SingleOrDefault()
            ?? throw new InvalidOperationException("Skill is no longer eligible for model invocation.");
        var loaded = _loader.Activate(candidate);
        var identity = new SkillActivationIdentity(loaded.Skill.Scope, id);
        var previous = _store.ReadDetailed(_session).GetValueOrDefault(identity);
        if (previous is not null
            && string.Equals(previous.ManifestSha256, loaded.Skill.ManifestSha256, StringComparison.Ordinal)
            && string.Equals(previous.PackageContentSha256, loaded.PackageContentSha256, StringComparison.Ordinal))
            return System.Threading.Tasks.Task.FromResult(new ToolResult("Skill " + id + " is already active.",
                "{\"skillId\":\"" + JsonEncodedText.Encode(id).ToString() + "\",\"nextTurn\":true}",
                null, 2, false, EffectOutcome.None));

        // Revalidate and budget the whole proposed Session set before writing any state.
        var active = new SkillActivationResolver().LoadCandidates(candidates, _session, _store,
            limits: _limits);
        var proposed = active.Select(item => item.Skill)
            .Where(item => item.Scope != loaded.Skill.Scope || item.Manifest.Id != id)
            .Append(loaded.Skill with { State = SkillLifecycleState.Activated }).ToArray();
        var admission = _selector.Select(proposed, SkillSelectionRequestKind.SessionReplay, _limits);
        if (admission.Selected.Count != proposed.Length
            || !admission.Selected.Any(item => item.Scope == loaded.Skill.Scope && item.Manifest.Id == id))
            throw new InvalidOperationException("Skill activation exceeds the Session's effective Harness/context budget.");

        if (context.EmitEvent is null)
            throw new InvalidOperationException("Skill activation cannot be recorded without a durable event sink.");
        _store.Activate(_session, loaded.Skill.Scope, id, loaded.Skill.ManifestSha256,
            loaded.PackageContentSha256 ?? loaded.ContentSha256, "Model");
        try
        {
            context.EmitEvent(new SkillActivationChanged(id, loaded.Skill.Scope,
                loaded.Skill.ManifestSha256, Activated: true, Origin: "Model"));
        }
        catch
        {
            if (previous is null) _store.Release(_session, loaded.Skill.Scope, id);
            else _store.Activate(_session, loaded.Skill.Scope, id, previous.ManifestSha256,
                previous.PackageContentSha256, previous.Origin);
            throw;
        }
        var result = new ToolResult("Skill " + id + " is active for the next turn.", "{\"skillId\":\""
            + JsonEncodedText.Encode(id).ToString() + "\",\"nextTurn\":true}", null, 3, false,
            EffectOutcome.Applied);
        return System.Threading.Tasks.Task.FromResult(result);
    }
}
