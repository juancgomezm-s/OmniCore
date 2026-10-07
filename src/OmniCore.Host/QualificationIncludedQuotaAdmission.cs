namespace OmniCore.Host;

using OmniCore.Domain;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Protocol;
using OmniCore.Qualification;

/// <summary>Subscription quota admission for each qualification probe. No fabricated
/// Session/Run/Turn or monetary reservation; canonical token observations survive
/// interrupted suites. Suite consent is not low-window consent.</summary>
internal sealed class QualificationIncludedQuotaAdmission(
    ProviderDescriptor descriptor, QualificationOptions options, SqliteModelQualificationStore store,
    string keyHash, IReadOnlyList<Probe> probes, long attemptBound) : IProbeExecutionObserver
{
    private readonly Guid _executionId = Guid.NewGuid();
    private readonly string _taskSetHash = ProbeScorer.TaskSetHash(probes);
    private int _ordinal;
    private (ProbeRequest Request, DateTimeOffset Started)? _active;

    public async ValueTask BeforeDispatchAsync(ProbeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_active is not null) throw new InvalidOperationException("A qualification probe is already admitted.");
        var snapshot = options.QueryQuota is { } query
            ? await query(descriptor.Id, cancellationToken).ConfigureAwait(false)
            : await QueryDefaultAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!StringComparer.Ordinal.Equals(snapshot.ProviderId, descriptor.Id))
            throw new InvalidDataException("Quota provider identity mismatch.");
        if (IncludedQuotaAdmission.LowWindows(snapshot).Length != 0
            && (options.ConfirmLowQuota is not { } confirm
                || !await confirm(snapshot, cancellationToken).ConfigureAwait(false)))
            throw new ModelQualificationConsentException(
                "Reported subscription quota is below 10%; separate informed consent is required for this probe.");
        cancellationToken.ThrowIfCancellationRequested();
        _active = (request, DateTimeOffset.UtcNow);
    }

    public ValueTask CompletedAsync(ProbeRequest request, ProbeExecutionObservation observation)
    {
        if (_active is not { } active || active.Request != request)
            throw new InvalidOperationException("Qualification observation has no matching admission.");
        // ReservationId is an invocation association only here: no USD reservation/ledger
        // is created. MaximumUsd=0 grants no monetary authority; CostUsd remains unknown.
        QualificationReceiptWriter.Record(store, _executionId, keyHash, _taskSetHash,
            request, _ordinal, $"qualification/{_executionId:D}/{_ordinal}", active.Started,
            BillingMode.IncludedQuota, 0m, attemptBound, observation);
        _active = null;
        _ordinal++;
        if (observation.ObservedGenerationSends > attemptBound)
            throw new ModelQualificationProbeBoundExceededException();
        return ValueTask.CompletedTask;
    }

    private async Task<ProviderQuotaSnapshot> QueryDefaultAsync(CancellationToken cancellationToken)
    {
        // An injected provider is not evidence of the developer's authenticated account.
        // Never contact a real CLI/account from an offline provider fixture.
        if (options.Provider is not null)
            return new(descriptor.Id, null, "injected-provider:no-quota-query", DateTimeOffset.UtcNow,
                MetricAvailability.Unknown, [], [], "No quota query was supplied for the injected provider.");
        var subscription = descriptor is { Family: ProviderFamily.OpenAIResponses, Profile: "codex" }
            ? "chatgpt" : descriptor.Id == "claude" ? "claude" : null;
        if (subscription is null)
            return new(descriptor.Id, null, "unmapped-subscription", DateTimeOffset.UtcNow,
                MetricAvailability.Unknown, [], [], "No subscription quota adapter is mapped to this provider.");
        var snapshot = await new SubscriptionQuotaService().QueryAsync(subscription, cancellationToken)
            .ConfigureAwait(false);
        // Configured IDs may be aliases; preserve the official CLI's account/source/date/windows.
        return snapshot with { ProviderId = descriptor.Id };
    }
}
