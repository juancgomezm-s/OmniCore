namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Infrastructure;

/// <summary>Shared pure interpretation of primary/meta canonical journal evidence.
/// No persistence, reservations or provider calls. Missing/contradictory evidence stays incomplete.</summary>
internal sealed class CanonicalSpendReader(IEventCodecRegistry codecs, IArtifactStore artifacts)
{
    private readonly IEventCodecRegistry _codecs = codecs;
    private readonly IArtifactStore _artifacts = artifacts;
    internal sealed record Snapshot(decimal? SessionUsd, decimal? DailyUsd, decimal? RunUsd, bool Incomplete);
    internal Snapshot ReadMeta(IEnumerable<DomainEvent> events, SessionId? sessionId,
        RunId? runId, string today, IArtifactStore? evidenceArtifacts = null)
    {
        var artifacts = evidenceArtifacts ?? _artifacts;
        decimal? session = 0m, daily = 0m, run = 0m;
        var incomplete = false;
        var starts = new Dictionary<(SessionId, string), (DomainEvent Event, MetaModelInvocationStarted Payload)>();
        var outcomes = new Dictionary<(SessionId, string), (TokenUsage Usage, TokenUsageFields Fields,
            decimal Cost, string Day)>();
        var notDispatched = new HashSet<(SessionId, string)>();
        foreach (var evt in events.OrderBy(e => e.SessionId.ToString(), StringComparer.Ordinal).ThenBy(e => e.Sequence))
        {
            try
            {
                var payload = _codecs.Decode(evt);
                if (payload is MetaModelInvocationStarted start)
                {
                    if (string.IsNullOrWhiteSpace(start.InvocationId) || string.IsNullOrWhiteSpace(start.Operation)
                        || string.IsNullOrWhiteSpace(start.ModelFingerprint) || evt.RunId != start.RunId
                        // Current summaries retain their private input as context snapshots;
                        // older journals used Other. Both still require verified CAS evidence.
                        || start.InputArtifact is not { } input
                        || input.Kind is not (ArtifactKind.ContextSnapshot or ArtifactKind.Other)
                        || !artifacts.Verify(input.Hash, input.Size)
                        || !starts.TryAdd((evt.SessionId, start.InvocationId), (evt, start))) incomplete = true;
                    continue;
                }
                if (payload is MetaModelInvocationNotDispatched unsent)
                {
                    var unsentKey = (evt.SessionId, unsent.InvocationId);
                    if (!starts.TryGetValue(unsentKey, out var unsentOrigin) || unsentOrigin.Event.Sequence >= evt.Sequence
                        || evt.RunId != unsent.RunId || unsentOrigin.Payload.RunId != unsent.RunId
                        || unsentOrigin.Payload.Operation != unsent.Operation || unsentOrigin.Payload.ModelFingerprint != unsent.ModelFingerprint
                        || evt.TaskId != unsentOrigin.Event.TaskId || evt.LaneId != unsentOrigin.Event.LaneId
                        || evt.TurnId != unsentOrigin.Event.TurnId || evt.ExecutionId != unsentOrigin.Event.ExecutionId
                        || outcomes.ContainsKey(unsentKey) || !notDispatched.Add(unsentKey)) incomplete = true;
                    continue;
                }
                var identity = payload switch
                {
                    MetaModelInvocationCompleted c => (c.InvocationId, c.RunId, c.Operation, c.ModelFingerprint,
                        c.Usage, c.CostUsd, c.ReportedUsageFields),
                    MetaModelInvocationFailed f => (f.InvocationId, f.RunId, f.Operation, f.ModelFingerprint,
                        f.Usage, f.CostUsd, f.ReportedUsageFields),
                    _ => default,
                };
                if (payload is not (MetaModelInvocationCompleted or MetaModelInvocationFailed)) continue;
                var attempts = payload switch
                {
                    MetaModelInvocationCompleted c => c.GenerationAttempts,
                    MetaModelInvocationFailed f => f.GenerationAttempts,
                    _ => null,
                };
                // Retain the final response cost; it is not proof of all retry consumption.
                // Legacy receipts retain their established monetary interpretation.
                if (attempts is { HasCompleteUsageCoverage: false }) incomplete = true;
                if (string.IsNullOrWhiteSpace(identity.InvocationId)) { incomplete = true; continue; }
                var key = (evt.SessionId, identity.InvocationId);
                if (notDispatched.Contains(key)) incomplete = true;
                if (!starts.TryGetValue(key, out var origin) || origin.Event.Sequence >= evt.Sequence
                    || evt.RunId != identity.RunId || origin.Payload.RunId != identity.RunId
                    || origin.Payload.Operation != identity.Operation || origin.Payload.ModelFingerprint != identity.ModelFingerprint
                    || identity.Usage is null || TokenUsageValidation.IsInvalid(identity.Usage,
                        identity.ReportedUsageFields ?? (TokenUsageFields.Input | TokenUsageFields.Output))
                    || identity.CostUsd is null || identity.CostUsd < 0m
                    || identity.ReportedUsageFields is not { } fields
                    || (fields & (TokenUsageFields.Input | TokenUsageFields.Output))
                        != (TokenUsageFields.Input | TokenUsageFields.Output)
                    || payload is MetaModelInvocationCompleted completed
                        && (completed.OutputArtifact is not { } output || output.Kind != ArtifactKind.ModelResponse
                            || !artifacts.Verify(output.Hash, output.Size)))
                {
                    incomplete = true;
                    continue;
                }
                var charge = (Usage: identity.Usage, Fields: fields, Cost: identity.CostUsd.Value,
                    Day: evt.Timestamp.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
                if (outcomes.TryGetValue(key, out var prior))
                {
                    // A second terminal record cannot rewrite measured usage or move the charge's day.
                    if (prior.Usage != charge.Usage || prior.Fields != charge.Fields || prior.Cost != charge.Cost)
                        incomplete = true;
                }
                else outcomes.Add(key, charge);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException
                or FormatException or ArgumentException or IOException)
            {
                incomplete = true;
            }
        }
        if (starts.Keys.Any(key => !outcomes.ContainsKey(key) && !notDispatched.Contains(key))) incomplete = true;
        foreach (var pair in outcomes)
        {
            var cost = pair.Value.Cost;
            if (pair.Value.Day == today) daily = AddHistoricalSpend(daily, cost);
            if (pair.Key.Item1 == sessionId) session = AddHistoricalSpend(session, cost);
            if (pair.Key.Item1 == sessionId && starts[pair.Key].Payload.RunId == runId)
                run = AddHistoricalSpend(run, cost);
        }
        return new(session, daily, run, incomplete || session is null || daily is null || run is null);
    }


    internal static decimal? AddHistoricalSpend(decimal? total, decimal cost)
    {
        if (total is null) return null;
        try { return checked(total.Value + cost); }
        catch (OverflowException) { return null; }
    }


    internal sealed record UsageEnvelope(string Response, string RunId, string Day, decimal? CostUsd,
        TokenUsage? Usage, decimal? ReportedProviderCostUsd = null);

    internal Snapshot ReadPrimary(IEnumerable<DomainEvent> events, SessionId? sessionId, RunId? runId, string today,
        Func<DomainEvent, ModelStepStarted, bool>? ownedPendingPrimary = null)
    {
        var artifacts = _artifacts;
        decimal? session = 0m, daily = 0m, run = 0m;
        var incomplete = false;
        var eventList = events.ToArray();
        IReadOnlyList<DomainEvent> stepStarts = eventList.Where(e => e.Type.ToString() == "model_step.started").ToArray();
        IReadOnlyList<DomainEvent> stepCompletions = eventList.Where(e => e.Type.ToString() == "model_step.completed").ToArray();
        IReadOnlyList<DomainEvent> unsentSteps = eventList.Where(e => e.Type.ToString() == "model_step.not_dispatched").ToArray();
        IReadOnlyList<DomainEvent> completions = eventList.Where(e => e.Type.ToString() == "model.completed").ToArray();
        stepStarts ??= Array.Empty<DomainEvent>();
        stepCompletions ??= Array.Empty<DomainEvent>();
        var startedKeys = new HashSet<(string Session, string Turn, int Index)>();
        var startedRuns = new Dictionary<(string Session, string Turn, int Index), string>();
        var startedEvents = new Dictionary<(string Session, string Turn, int Index), DomainEvent>();
        var startedPayloads = new Dictionary<(string Session, string Turn, int Index), ModelStepStarted>();
        var externalStarts = new HashSet<(string Session, string Turn, int Index)>();
        var externalCompletions = new Dictionary<(string Session, string Turn, int Index),
            (DomainEvent Event, ModelStepCompleted Payload)>();
        foreach (var evt in stepStarts)
        {
            try
            {
                if (_codecs.Decode(evt) is not ModelStepStarted started || started.StepIndex < 0
                    || started.ContextBudget <= 0 || string.IsNullOrWhiteSpace(started.ModelId)
                    || evt.TurnId is null || evt.TurnId.ToString() != started.TurnId.ToString()
                    || evt.RunId is null
                    || !startedKeys.Add((evt.SessionId.ToString(), started.TurnId.ToString(), started.StepIndex)))
                {
                    incomplete = true;
                    continue;
                }
                var key = (evt.SessionId.ToString(), started.TurnId.ToString(), started.StepIndex);
                if (Enum.TryParse<ToolMode>(started.ToolMode, out var toolMode) && Enum.IsDefined(toolMode))
                {
                    // Native model steps retain their established enum validation.
                }
                else if (started.ToolMode == "claude-code-fixed-tools" && evt.ExecutionId is not null
                    && evt.LaneId is not null) externalStarts.Add(key);
                else { incomplete = true; continue; }
                startedRuns[key] = evt.RunId!.ToString();
                startedEvents[key] = evt;
                startedPayloads[key] = started;
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException
                or FormatException or ArgumentException)
            {
                incomplete = true;
            }
        }

        var completedKeys = new HashSet<(string Session, string Turn, int Index)>();
        foreach (var evt in stepCompletions)
        {
            ModelStepCompleted? completed;
            try { completed = _codecs.Decode(evt) as ModelStepCompleted; }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException
                or FormatException or ArgumentException)
            {
                incomplete = true;
                continue;
            }
            if (completed is null || completed.StepIndex < 0 || evt.TurnId is null
                || evt.TurnId.ToString() != completed.TurnId.ToString()
                || completed.Usage is null
                || TokenUsageValidation.IsInvalid(completed.Usage, completed.ReportedUsageFields
                    ?? (TokenUsageFields.Input | TokenUsageFields.Output))
                || !Enum.IsDefined(completed.StopReason)
                || !completedKeys.Add((evt.SessionId.ToString(), completed.TurnId.ToString(), completed.StepIndex)))
            {
                incomplete = true;
                continue;
            }
            var key = (evt.SessionId.ToString(), completed.TurnId.ToString(), completed.StepIndex);
            if (completed.GenerationAttempts is { HasCompleteUsageCoverage: false }) incomplete = true;
            UsageEnvelope stepEnvelope;
            bool artifactValid;
            try
            {
                var responseRef = completed.ResponseArtifact;
                var artifactMetadataValid = responseRef is not null && responseRef.Size >= 0
                    && responseRef.Kind == ArtifactKind.ModelResponse
                    && responseRef.Sensitivity == Sensitivity.Sensitive
                    && responseRef.MediaType == "application/vnd.omnicore.model-usage+json"
                    && responseRef.Hash is not null
                    && string.Equals(responseRef.Hash.Algorithm, "sha256", StringComparison.Ordinal)
                    && artifacts.Verify(responseRef.Hash, responseRef.Size);
                var artifactText = artifactMetadataValid ? artifacts.GetText(responseRef!.Hash!) : null;
                artifactValid = TryDecodeUsageEnvelope(artifactText, out stepEnvelope);
            }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or OverflowException)
            {
                artifactValid = false;
                stepEnvelope = new UsageEnvelope("", "", "", null, null);
            }
            if (!startedKeys.Contains(key) || completed.CostUsd is null || !artifactValid
                || stepEnvelope.Usage is null || stepEnvelope.Usage != completed.Usage
                || stepEnvelope.CostUsd != completed.CostUsd
                || stepEnvelope.ReportedProviderCostUsd != completed.ReportedProviderCostUsd
                || stepEnvelope.Day != completed.Day
                || evt.RunId is null || stepEnvelope.RunId != evt.RunId.ToString()
                || !startedRuns.TryGetValue(key, out var startedRun)
                || evt.RunId.ToString() != startedRun
                || !DateOnly.TryParseExact(completed.Day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out _))
            {
                incomplete = true;
                continue;
            }
            var cost = completed.CostUsd.Value;
            if (cost < 0) { incomplete = true; continue; }
            if (completed.CostIncomplete) incomplete = true;
            if (externalStarts.Contains(key)) externalCompletions[key] = (evt, completed);
            else
            {
                if (completed.Day == today) daily = AddHistoricalSpend(daily, cost);
                if (evt.SessionId == sessionId) session = AddHistoricalSpend(session, cost);
                if (evt.SessionId == sessionId && evt.RunId == runId) run = AddHistoricalSpend(run, cost);
            }
        }
        var unsentKeys = new HashSet<(string Session, string Turn, int Index)>();
        foreach (var evt in unsentSteps ?? Array.Empty<DomainEvent>())
        {
            try
            {
                if (_codecs.Decode(evt) is not ModelStepNotDispatched unsent) { incomplete = true; continue; }
                var key = (evt.SessionId.ToString(), unsent.TurnId.ToString(), unsent.StepIndex);
                if (!startedEvents.TryGetValue(key, out var origin) || origin.Sequence >= evt.Sequence
                    || evt.TurnId != unsent.TurnId || evt.RunId != origin.RunId || evt.TaskId != origin.TaskId
                    || evt.LaneId != origin.LaneId || evt.ExecutionId != origin.ExecutionId
                    || completedKeys.Contains(key) || !unsentKeys.Add(key)) incomplete = true;
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException
                or FormatException or ArgumentException) { incomplete = true; }
        }
        if (startedKeys.Any(key => !completedKeys.Contains(key) && !unsentKeys.Contains(key)
            && (!startedEvents.TryGetValue(key, out var origin) || !startedPayloads.TryGetValue(key, out var start)
                || ownedPendingPrimary?.Invoke(origin, start) != true))) incomplete = true;
        foreach (var group in startedKeys.GroupBy(key => (key.Session, key.Turn)))
        {
            var indexes = group.Select(key => key.Index).OrderBy(index => index).ToArray();
            for (var i = 0; i < indexes.Length; i++)
                if (indexes[i] != i) { incomplete = true; break; }
        }

        // A Turn with invocation records is accounted per invocation; its final summary is
        // retained for compatibility but must never be added a second time.
        var stepTurnKeys = startedKeys.Select(key => (key.Session, key.Turn)).ToHashSet();
        foreach (var evt in completions)
        {
            if (evt.TurnId is not null && stepTurnKeys.Contains((evt.SessionId.ToString(), evt.TurnId.ToString())))
                continue;
            ModelCompleted? completed;
            try { completed = _codecs.Decode(evt) as ModelCompleted; }
            catch (Exception ex) when (ex is System.Text.Json.JsonException
                or InvalidOperationException or FormatException or ArgumentException)
            {
                incomplete = true;
                continue;
            }
            if (completed?.ResponseArtifact is null) { incomplete = true; continue; }
            string? text;
            try { text = artifacts.GetText(completed.ResponseArtifact.Hash); }
            catch (InvalidDataException)
            {
                incomplete = true;
                continue;
            }
            UsageEnvelope record;
            bool decoded;
            try { decoded = TryDecodeUsageEnvelope(text, out record); }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                incomplete = true;
                continue;
            }
            if (!decoded || record.CostUsd is null)
            {
                incomplete = true;
                continue;
            }
            if (!DateOnly.TryParseExact(record.Day, "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out _)
                || evt.RunId is null || record.RunId != evt.RunId.ToString())
            {
                // Invalid/mismatched metadata cannot safely exclude a record from today's or
                // this Run's total. Keep the known cost in broad totals and fail closed below.
                incomplete = true;
            }
            var cost = record.CostUsd.Value;
            if (cost < 0)
            {
                // Legacy summaries are still journal evidence. A negative amount must not be
                // allowed to reduce a daily/session total and thereby bypass a spend cap.
                incomplete = true;
                continue;
            }
            if (record.Day == today) daily = AddHistoricalSpend(daily, cost);
            if (evt.SessionId == sessionId) session = AddHistoricalSpend(session, cost);
            if (evt.SessionId == sessionId && evt.RunId == runId) run = AddHistoricalSpend(run, cost);
        }
        var externalReceiptKeys = new HashSet<(string Session, string Turn, int Index)>();
        foreach (var evt in eventList.Where(e => e.Type.ToString() == "external_lane.usage_recorded"))
        {
            ExternalLaneUsageRecorded? receipt;
            try { receipt = _codecs.Decode(evt) as ExternalLaneUsageRecorded; }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException
                or FormatException or ArgumentException)
            {
                incomplete = true;
                continue;
            }
            if (receipt is null || evt.RunId is null || evt.LaneId != receipt.LaneId
                || evt.ExecutionId != receipt.ExecutionId || string.IsNullOrWhiteSpace(receipt.SessionId)
                || receipt.TotalCostUsd is null || receipt.TotalCostUsd < 0m
                || receipt.ToolUseCount < 0 || receipt.EventCount < 0 || receipt.StreamBytes < 0
                || receipt.InputTokens is < 0 || receipt.OutputTokens is < 0
                || receipt.CacheReadInputTokens is < 0 || receipt.CacheCreationInputTokens is < 0
                || !artifacts.Verify(receipt.ResultRef.Hash, receipt.ResultRef.Size))
            {
                incomplete = true;
                continue;
            }
            var matchingSteps = externalStarts.Where(key => key.Session == evt.SessionId.ToString()
                && startedEvents.TryGetValue(key, out var start)
                && start.ExecutionId == receipt.ExecutionId && start.RunId == evt.RunId
                && start.LaneId == receipt.LaneId && evt.TurnId?.ToString() == key.Turn).ToArray();
            if (matchingSteps.Length != 1 || !externalCompletions.TryGetValue(matchingSteps[0], out var externalStep)
                || !externalReceiptKeys.Add(matchingSteps[0]))
            {
                incomplete = true;
                continue;
            }
            long expectedInput;
            try
            {
                expectedInput = receipt.InputTokens is { } input && receipt.CacheReadInputTokens is { } cacheRead
                    && receipt.CacheCreationInputTokens is { } cacheWrite
                    ? checked(input + cacheRead + cacheWrite) : -1;
            }
            catch (OverflowException) { incomplete = true; continue; }
            if (receipt.InputTokens is null || receipt.OutputTokens is null
                || externalStep.Payload.Usage != new TokenUsage(expectedInput, receipt.OutputTokens.Value,
                    receipt.CacheReadInputTokens ?? 0, receipt.CacheCreationInputTokens ?? 0, 0)
                || externalStep.Payload.CostUsd != receipt.TotalCostUsd)
            {
                incomplete = true;
                continue;
            }
            // A resumed external session can return a valid final result while earlier request
            // usage remains unknowable. Retain the known receipt cost, but block capped writes.
            if (!receipt.UsageCoverageComplete) incomplete = true;
            var externalCost = receipt.TotalCostUsd.Value;
            if (evt.Timestamp.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) == today)
                daily = AddHistoricalSpend(daily, externalCost);
            if (evt.SessionId == sessionId) session = AddHistoricalSpend(session, externalCost);
            if (evt.SessionId == sessionId && evt.RunId == runId) run = AddHistoricalSpend(run, externalCost);
        }
        foreach (var key in externalStarts.Where(key => !externalReceiptKeys.Contains(key)))
        {
            // The one open external step currently owned by the active dispatcher is expected
            // during crash recovery; its already-reserved usage remains unknown, but should not
            // prevent the exact bound session from consuming only its second reserved allowance.
            // Any other open external attempt stays an unresolved run-level accounting debt.
            if (!startedEvents.TryGetValue(key, out var origin) || !startedPayloads.TryGetValue(key, out var payload)
                || ownedPendingPrimary?.Invoke(origin, payload) != true)
                incomplete = true;
        }
        return new Snapshot(session, daily, run,
            incomplete || session is null || daily is null || run is null);
    }


    internal static bool TryDecodeUsageEnvelope(string? text, out UsageEnvelope record)
    {
        record = new UsageEnvelope("", "", "", null, null);
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(text);
            var root = document.RootElement;
            if (!root.TryGetProperty("omnicoreUsage", out var version) || version.GetInt32() != 1
                || !root.TryGetProperty("response", out var response)
                || !root.TryGetProperty("runId", out var runId)
                || !root.TryGetProperty("day", out var day)
                || !root.TryGetProperty("costUsd", out var cost)) return false;
            decimal? parsedCost = cost.ValueKind == System.Text.Json.JsonValueKind.String
                && decimal.TryParse(cost.GetString(), System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;
            decimal? reportedProviderCost = null;
            if (root.TryGetProperty("reportedProviderCostUsd", out var reportedCost)
                && reportedCost.ValueKind == System.Text.Json.JsonValueKind.String
                && decimal.TryParse(reportedCost.GetString(), System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsedReportedCost)
                && parsedReportedCost is >= 0m and <= 1_000_000m)
                reportedProviderCost = parsedReportedCost;
            else if (root.TryGetProperty("reportedProviderCostUsd", out reportedCost)
                && reportedCost.ValueKind is not (System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined))
                return false;
            TokenUsage? parsedUsage = null;
            var usageNames = new[] { "input", "output", "cacheRead", "cacheWrite", "reasoning" };
            var present = 0;
            foreach (var name in usageNames)
                if (root.TryGetProperty(name, out _)) present++;
            if (present != 0 && present != usageNames.Length) return false;
            if (present == usageNames.Length)
            {
                var numbers = new long[usageNames.Length];
                for (var i = 0; i < usageNames.Length; i++)
                    if (!root.GetProperty(usageNames[i]).TryGetInt64(out numbers[i]) || numbers[i] < 0) return false;
                parsedUsage = new TokenUsage(numbers[0], numbers[1], numbers[2], numbers[3], numbers[4]);
            }
            record = new UsageEnvelope(response.GetString() ?? "", runId.GetString() ?? "",
                day.GetString() ?? "", parsedCost, parsedUsage, reportedProviderCost);
            return true;
        }
        catch (System.Text.Json.JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }


    internal static decimal ReadAllWorkspaceDaily(UserWorkspaceSpendReader reader, IEventCodecRegistry codecs, string day)
    {
        decimal daily = 0m;
        foreach (var evidence in reader.ReadOtherWorkspaces())
        {
            var parser = new CanonicalSpendReader(codecs, evidence.Artifacts);
            var primary = parser.ReadPrimary(evidence.Events, null, null, day);
            var meta = parser.ReadMeta(evidence.Events, null, null, day);
            if (primary.Incomplete || meta.Incomplete || primary.DailyUsd is null || meta.DailyUsd is null)
                throw new InvalidDataException("Canonical User workspace usage is incomplete.");
            daily = checked(daily + primary.DailyUsd.Value + meta.DailyUsd.Value);
        }
        return daily;
    }
}
