namespace OmniCore.Domain;

/// <summary>Provenance of a Session security ceiling. External imports cannot claim Native.</summary>
public enum SessionSecurityProvenance
{
    Native,
    Imported,
    LegacyMigrated,
}

/// <summary>Core-owned origin for a durable Session security policy selection.</summary>
public enum SessionSecurityPolicySource
{
    UserDefault,
    UserSelection,
    LegacyMigration,
    ImportReview,
}

/// <summary>
/// Durable, revisioned ceiling for every Run in a Session. It stores the resolved capability and
/// numeric limits alongside the preset label, so authorization never depends on enum ordering or
/// on a later change to preset definitions. This ceiling never grants a tool permission.
/// </summary>
public sealed record SessionSecurityPolicySet(
    SessionId SessionId,
    long PolicyRevision,
    ModelPolicyCategory Category,
    string PresetId,
    IReadOnlyList<ModelToolCapability> CapabilityCeiling,
    ToolMode ToolMode,
    int MaxVisibleTools,
    bool AllowToolDiscovery,
    FileMutationMode FileMutationMode,
    DestructiveActionPolicy DeletePolicy,
    DestructiveActionPolicy MoveOrRenamePolicy,
    int MaxFilesPerTurn,
    int MaxChangedLinesPerTurn,
    double MaxRewriteRatio,
    bool RequirePriorRead,
    bool RequireExpectedVersionToken,
    bool RequirePostEditValidation,
    bool AllowParallelMutations,
    int MaxModelStepsPerTurn,
    int MaxToolCallsPerTurn,
    bool AllowOrchestration,
    int MaxGenerationSeconds,
    SessionSecurityProvenance Provenance,
    SessionSecurityPolicySource Source,
    DateTimeOffset ChangedAt) : DomainEventPayload
{
    public EventType Type() => EventType.Of("session.security_policy_set");

    public int SchemaVersion() => 1;

    public static SessionSecurityPolicySet FromPreset(SessionId sessionId, long revision,
        ModelPolicyCategory category, SessionSecurityProvenance provenance,
        SessionSecurityPolicySource source, DateTimeOffset changedAt)
    {
        if (revision < 1) throw new ArgumentOutOfRangeException(nameof(revision));
        if (!Enum.IsDefined(category) || category == ModelPolicyCategory.Custom)
            throw new ArgumentException("A standard Core session policy preset is required.", nameof(category));
        if (!Enum.IsDefined(provenance)) throw new ArgumentOutOfRangeException(nameof(provenance));
        if (!Enum.IsDefined(source)) throw new ArgumentOutOfRangeException(nameof(source));
        if (!IsValidOriginPair(provenance, source))
            throw new ArgumentException("Session security provenance must be assigned by its matching Core origin.");

        var preset = ModelPolicyPresets.For(category);
        var mutation = preset.MutationPolicy;
        return new SessionSecurityPolicySet(sessionId, revision, category, category.ToString(),
            preset.ToolPolicy.CapabilityCeiling.Order().ToArray(), preset.ToolPolicy.Mode,
            preset.ToolPolicy.MaxVisibleTools, preset.ToolPolicy.AllowToolDiscovery,
            mutation.Mode, mutation.Delete, mutation.MoveOrRename, mutation.MaxFilesPerTurn,
            mutation.MaxChangedLinesPerTurn, mutation.MaxRewriteRatio, mutation.RequirePriorRead,
            mutation.RequireExpectedVersionToken, mutation.RequirePostEditValidation,
            mutation.AllowParallelMutations, preset.MaxModelStepsPerTurn, preset.MaxToolCallsPerTurn,
            preset.AllowOrchestration, preset.MaxGenerationSeconds, provenance, source, changedAt);
    }

    public UserModelPolicy ToUserModelPolicy()
    {
        Validate();
        var tools = new ModelToolPolicy(ToolMode, MaxVisibleTools, AllowToolDiscovery,
            CapabilityCeiling.ToHashSet());
        var mutation = new FileMutationPolicy(FileMutationMode, DeletePolicy, MoveOrRenamePolicy,
            MaxFilesPerTurn, MaxChangedLinesPerTurn, MaxRewriteRatio, RequirePriorRead,
            RequireExpectedVersionToken, RequirePostEditValidation, AllowParallelMutations);
        return new UserModelPolicy(Category, tools, mutation, "session-security:" + PresetId,
            null, MaxModelStepsPerTurn, MaxToolCallsPerTurn, AllowOrchestration, MaxGenerationSeconds);
    }

    public void Validate()
    {
        if (PolicyRevision < 1 || string.IsNullOrWhiteSpace(PresetId) || PresetId.Length > 64
            || !Enum.IsDefined(Category) || Category == ModelPolicyCategory.Custom
            || !Enum.IsDefined(ToolMode) || !Enum.IsDefined(FileMutationMode)
            || !Enum.IsDefined(DeletePolicy) || !Enum.IsDefined(MoveOrRenamePolicy)
            || !Enum.IsDefined(Provenance) || !Enum.IsDefined(Source)
            || !IsValidOriginPair(Provenance, Source)
            || CapabilityCeiling is null || CapabilityCeiling.Count > Enum.GetValues<ModelToolCapability>().Length
            || CapabilityCeiling.Any(capability => !Enum.IsDefined(capability))
            || CapabilityCeiling.Distinct().Count() != CapabilityCeiling.Count
            // FullAgent's explicit full catalog uses the same unbounded sentinel as
            // ModelToolPolicy. Other numeric caps and categories retain their bounds.
            || MaxVisibleTools < 0 || MaxVisibleTools > 64
                && !(Category == ModelPolicyCategory.FullAgent && MaxVisibleTools == int.MaxValue)
            || MaxFilesPerTurn < 0 || MaxChangedLinesPerTurn < 0
            || !double.IsFinite(MaxRewriteRatio) || MaxRewriteRatio is < 0 or > 1
            || FileMutationMode == OmniCore.Domain.FileMutationMode.None && (MaxFilesPerTurn != 0 || MaxChangedLinesPerTurn != 0)
            || MaxModelStepsPerTurn is < 1 or > 8 || MaxToolCallsPerTurn is < 1 or > 64
            || MaxGenerationSeconds is < 1 or > 3600)
            throw new InvalidDataException("Invalid durable Session security policy.");
    }

    /// <summary>Whether <paramref name="next"/> can only reduce this Session's current
    /// authority. Used to permit immediate revocation during a Run without allowing an in-flight
    /// request to gain new capabilities.</summary>
    public bool IsMonotonicTightening(SessionSecurityPolicySet next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (SessionId != next.SessionId || next.PolicyRevision != PolicyRevision + 1
            || next.Provenance != Provenance || !ToolModeCovers(ToolMode, next.ToolMode)
            || !next.CapabilityCeiling.All(CapabilityCeiling.Contains)
            || next.MaxVisibleTools > MaxVisibleTools
            || next.AllowToolDiscovery && !AllowToolDiscovery
            || !MutationModeCovers(FileMutationMode, next.FileMutationMode)
            || Rank(next.DeletePolicy) < Rank(DeletePolicy)
            || Rank(next.MoveOrRenamePolicy) < Rank(MoveOrRenamePolicy)
            || next.MaxFilesPerTurn > MaxFilesPerTurn
            || next.MaxChangedLinesPerTurn > MaxChangedLinesPerTurn
            || next.MaxRewriteRatio > MaxRewriteRatio
            || RequirePriorRead && !next.RequirePriorRead
            || RequireExpectedVersionToken && !next.RequireExpectedVersionToken
            || RequirePostEditValidation && !next.RequirePostEditValidation
            || !AllowParallelMutations && next.AllowParallelMutations
            || next.MaxModelStepsPerTurn > MaxModelStepsPerTurn
            || next.MaxToolCallsPerTurn > MaxToolCallsPerTurn
            || !AllowOrchestration && next.AllowOrchestration
            || next.MaxGenerationSeconds > MaxGenerationSeconds) return false;
        return true;
    }

    private static bool MutationModeCovers(FileMutationMode current, FileMutationMode next)
    {
        var operations = new[] { ModelToolCapability.PatchExisting, ModelToolCapability.CreateFile,
            ModelToolCapability.ReplaceFile, ModelToolCapability.DeleteFile, ModelToolCapability.MoveOrRename };
        return operations.All(operation => !MutationModeAllows(next, operation)
            || MutationModeAllows(current, operation));
    }

    private static bool MutationModeAllows(FileMutationMode mode, ModelToolCapability capability) => mode switch
    {
        FileMutationMode.None => false,
        FileMutationMode.PatchExisting => capability == ModelToolCapability.PatchExisting,
        FileMutationMode.PatchAndCreate => capability is ModelToolCapability.PatchExisting or ModelToolCapability.CreateFile,
        FileMutationMode.Full => capability is ModelToolCapability.PatchExisting or ModelToolCapability.CreateFile
            or ModelToolCapability.ReplaceFile or ModelToolCapability.DeleteFile or ModelToolCapability.MoveOrRename,
        _ => false,
    };

    private static bool ToolModeCovers(ToolMode current, ToolMode next) => current switch
    {
        ToolMode.Direct => next == ToolMode.Direct,
        ToolMode.Discovered => next is ToolMode.Direct or ToolMode.Discovered,
        ToolMode.Code => next == ToolMode.Code,
        _ => false,
    };

    private static int Rank(DestructiveActionPolicy policy) => policy switch
    {
        DestructiveActionPolicy.Deny => 2,
        DestructiveActionPolicy.Ask => 1,
        DestructiveActionPolicy.Allow => 0,
        _ => -1,
    };

    private static bool IsValidOriginPair(SessionSecurityProvenance provenance,
        SessionSecurityPolicySource source) => (provenance, source) switch
    {
        (_, SessionSecurityPolicySource.UserSelection) => true,
        (SessionSecurityProvenance.Native, SessionSecurityPolicySource.UserDefault) => true,
        (SessionSecurityProvenance.LegacyMigrated, SessionSecurityPolicySource.LegacyMigration) => true,
        (SessionSecurityProvenance.Imported, SessionSecurityPolicySource.ImportReview) => true,
        _ => false,
    };
}
