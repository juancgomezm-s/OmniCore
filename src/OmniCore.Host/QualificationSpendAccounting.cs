namespace OmniCore.Host;

using System.Globalization;
using System.Text.Json;
using OmniCore.Domain;
using OmniCore.Infrastructure;
using OmniCore.Qualification;

/// <summary>User-scoped qualification admission and canonical receipts, independent of
/// profile revisions. No manufactured Session/Run/Turn and no ledger amounts as usage.</summary>
internal sealed class QualificationSpendAccounting : IProbeExecutionObserver
{
    private readonly SqliteModelQualificationStore _store;
    private readonly SqliteSpendReservationStore _reservations;
    private readonly UserWorkspaceSpendReader _workspaces;
    private readonly FileArtifactStore _artifacts;
    private readonly Guid _executionId = Guid.NewGuid();
    private readonly string _keyHash, _taskSetHash;
    private readonly decimal _maximum, _suiteCap, _dailyCap;
    private readonly long _attemptBound;
    private readonly BillingMode _billing;
    private int _ordinal;
    private (ProbeRequest Request, string Id, DateTimeOffset Started)? _active;

    internal QualificationSpendAccounting(SqliteModelQualificationStore store, string dataDirectory,
        string keyHash, IReadOnlyList<Probe> probes, decimal maximum, long attemptBound,
        BillingMode billing, decimal suiteCap, decimal dailyCap)
    {
        _store = store; _keyHash = keyHash; _taskSetHash = ProbeScorer.TaskSetHash(probes);
        _maximum = maximum; _attemptBound = attemptBound; _billing = billing;
        _suiteCap = suiteCap; _dailyCap = dailyCap;
        _workspaces = new UserWorkspaceSpendReader(dataDirectory);
        _artifacts = new FileArtifactStore(dataDirectory);
        _reservations = new SqliteSpendReservationStore(Path.Combine(dataDirectory, "spend-reservations.db"));
    }

    public ValueTask BeforeDispatchAsync(ProbeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_active is not null) throw new InvalidOperationException("A qualification probe is already admitted.");
        Reconcile(_store.ProbeReceipts(cancellationToken), _reservations);
        var id = $"qualification/{_executionId:D}/{_ordinal}";
        var codecs = EventCodecs.Create();
        var admission = _reservations.TryReserve(id, _maximum, () =>
        {
            var day = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var receipts = _store.ProbeReceipts(cancellationToken);
            var daily = checked(CanonicalSpendReader.ReadAllWorkspaceDaily(_workspaces, codecs, day)
                + Daily(receipts, day, _reservations));
            var suite = receipts.Where(r => r.ExecutionId == _executionId)
                .Aggregate(0m, (sum, r) => checked(sum + (r.CostUsd ?? 0m)));
            return [new SqliteSpendReservationStore.Limit("qualification", _executionId.ToString("D"), _suiteCap, suite),
                new("daily", "user", UserDailyBudgetContinuation.Limit(_workspaces, codecs, day, _dailyCap), daily)];
        }, out var scope);
        if (admission != SqliteSpendReservationStore.Admission.Reserved)
            throw new ModelQualificationBudgetAdmissionException(scope ?? "duplicate");
        try { cancellationToken.ThrowIfCancellationRequested(); }
        catch
        {
            _reservations.ReleaseBeforeDispatch(id);
            throw;
        }
        var started = DateTimeOffset.UtcNow;
        // A failure here is uncertain, not proof no dispatch was marked. Do not release it.
        _reservations.MarkDispatched(id);
        _active = (request, id, started);
        return ValueTask.CompletedTask;
    }

    public ValueTask CompletedAsync(ProbeRequest request, ProbeExecutionObservation observation)
    {
        if (_active is not { } active || active.Request != request)
            throw new InvalidOperationException("Qualification observation has no matching admission.");
        var result = observation.Result;
        // Invalid provider counters are raw evidence, not usable measurements. Preserve
        // them separately in CAS and retain an unknown-cost receipt with its full bound.
        var invalidUsage = result.Usage is { } raw && TokenUsageValidation.IsInvalid(raw, result.ReportedUsageFields);
        var receipt = new QualificationProbeReceipt(_executionId, request.Probe.Id.ToString(), _ordinal,
            active.Id, _keyHash, QuickProbeSuite.SuiteId, QuickProbeSuite.SuiteVersion, _taskSetHash,
            active.Started, observation.CompletedAtUtc, result.Status, observation.Termination,
            _billing, _maximum, _attemptBound, observation.ObservedGenerationSends,
            invalidUsage ? null : result.CostUsd, invalidUsage ? null : result.Usage,
            invalidUsage ? TokenUsageFields.None : result.ReportedUsageFields, null!);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject(); writer.WriteString("schema", QualificationProbeReceipt.EvidenceSchema);
            writer.WritePropertyName("receipt"); writer.WriteRawValue(receipt.CanonicalObservationJson());
            writer.WriteString("output", result.Output); writer.WriteString("error", result.Error);
            if (invalidUsage && result.Usage is { } rejected)
            {
                writer.WritePropertyName("invalidReportedUsage"); writer.WriteStartObject();
                writer.WriteNumber("fields", (int)result.ReportedUsageFields);
                writer.WriteNumber("input", rejected.Input); writer.WriteNumber("output", rejected.Output);
                writer.WriteNumber("cacheRead", rejected.CacheRead); writer.WriteNumber("cacheWrite", rejected.CacheWrite);
                writer.WriteNumber("reasoning", rejected.Reasoning); writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        var json = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        receipt = receipt with { Evidence = _artifacts.PutText(json, "application/json",
            ArtifactKind.Other, Sensitivity.Sensitive) };
        // Cancellation does not discard consumption already incurred. FULL receipt precedes settlement.
        _store.RecordProbeReceipt(receipt, CancellationToken.None);
        Reconcile([receipt], _reservations);
        _active = null;
        _ordinal++;
        return ValueTask.CompletedTask;
    }

    internal static void Reconcile(IReadOnlyList<QualificationProbeReceipt> receipts,
        SqliteSpendReservationStore reservations)
    {
        foreach (var receipt in receipts)
        {
            if (receipt.BillingMode is not (BillingMode.MeteredCurrency or BillingMode.CreditBalance or BillingMode.Unknown)) continue;
            ValidateBound(receipt);
            var complete = receipt.Termination == ProbeExecutionTermination.Completed
                && (receipt.ObservedGenerationSends == 1
                    || receipt.ObservedGenerationSends == 0 && receipt.MaximumGenerationAttempts == 1);
            var found = reservations.ReconcileCanonicalReceipt(receipt.ReservationId, receipt.MaximumUsd,
                receipt.CostUsd, receipt.Evidence.Hash.ToString(), complete);
            if (!found && (receipt.CostUsd is null || !complete))
                throw new InvalidDataException("Incomplete qualification consumption has no outstanding reservation.");
        }
    }

    internal static decimal Daily(IReadOnlyList<QualificationProbeReceipt> receipts, string day,
        SqliteSpendReservationStore reservations)
    {
        var total = 0m;
        foreach (var receipt in receipts)
        {
            if (receipt.BillingMode is not (BillingMode.MeteredCurrency or BillingMode.CreditBalance or BillingMode.Unknown)) continue;
            ValidateBound(receipt);
            if (receipt.CostUsd is null && !reservations.HasFullDispatchedBound(receipt.ReservationId, receipt.MaximumUsd))
                throw new InvalidDataException("Unknown qualification consumption lacks its full reserved bound.");
            if (receipt.CompletedAtUtc.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) == day
                && receipt.CostUsd is { } known) total = checked(total + known);
        }
        return total;
    }

    private static void ValidateBound(QualificationProbeReceipt receipt)
    {
        // Preserve contradictory observations, but a disproven finite ceiling cannot
        // authorize another send or release any of the outstanding reservation.
        if (receipt.ObservedGenerationSends > receipt.MaximumGenerationAttempts
            || receipt.CostUsd > receipt.MaximumUsd)
            throw new ModelQualificationProbeBoundExceededException();
    }
}

public sealed class ModelQualificationProbeBoundExceededException()
    : Exception("el uso reportado contradice la cota de cualificación; se conserva el recibo y no se autoriza otro envío");

public sealed class ModelQualificationBudgetAdmissionException(string scope)
    : Exception("presupuesto de cualificación insuficiente (" + scope + ")")
{
    public string Scope { get; } = scope;
}
