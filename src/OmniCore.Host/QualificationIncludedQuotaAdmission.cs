namespace OmniCore.Host;

using OmniCore.Domain;
using OmniCore.Models;
using OmniCore.Protocol;
using OmniCore.Qualification;

/// <summary>Subscription quota admission for each qualification probe. No fabricated
/// Session/Run/Turn or monetary receipt; suite consent is not low-window consent.</summary>
internal sealed class QualificationIncludedQuotaAdmission(
    ProviderDescriptor descriptor, QualificationOptions options) : IProbeExecutionObserver
{
    public async ValueTask BeforeDispatchAsync(ProbeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = options.QueryQuota is { } query
            ? await query(descriptor.Id, cancellationToken).ConfigureAwait(false)
            : await QueryDefaultAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!StringComparer.Ordinal.Equals(snapshot.ProviderId, descriptor.Id))
            throw new InvalidDataException("Quota provider identity mismatch.");
        if (IncludedQuotaAdmission.LowWindows(snapshot).Length == 0) return;
        if (options.ConfirmLowQuota is not { } confirm
            || !await confirm(snapshot, cancellationToken).ConfigureAwait(false))
            throw new ModelQualificationConsentException(
                "Reported subscription quota is below 10%; separate informed consent is required for this probe.");
        cancellationToken.ThrowIfCancellationRequested();
    }

    public ValueTask CompletedAsync(ProbeRequest request, ProbeExecutionObservation observation) =>
        ValueTask.CompletedTask;

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
