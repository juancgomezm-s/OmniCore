namespace OmniCore.Qualification;

using System.Diagnostics;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Runner determinista de probes de cualificación (ADR-0007 §6, M5). Consume el runtime como un
/// cliente a través de IModelProvider (INV-011); nunca ejecuta la suite automáticamente al
/// descubrir un modelo. La suite completa es 10–20 probes; esta es la base mínima `quick`.
/// </summary>
public sealed class ProbeRunner
{
    private readonly IModelProvider _provider;
    private readonly Func<TokenUsage, decimal?>? _quoteCost;

    public TimeSpan PerProbeTimeout { get; }

    public static readonly TimeSpan DefaultPerProbeTimeout = TimeSpan.FromSeconds(60);

    public ProbeRunner(IModelProvider provider) : this(provider, DefaultPerProbeTimeout) { }

    public ProbeRunner(IModelProvider provider, TimeSpan perProbeTimeout)
        : this(provider, perProbeTimeout, null) { }

    /// <summary>Uses explicit prices only when input and output are reported;
    /// null means unavailable. This does not establish a pre-call spending bound.</summary>
    public ProbeRunner(IModelProvider provider, TimeSpan perProbeTimeout,
        Func<TokenUsage, decimal?>? quoteCost)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _quoteCost = quoteCost;
        if (perProbeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(perProbeTimeout), "PerProbeTimeout debe ser positivo");
        }
        PerProbeTimeout = perProbeTimeout;
    }

    /// <summary>
    /// Ejecuta un conjunto de probes con consentimiento explícito y tope de costo total.
    /// </summary>
    public async Task<IReadOnlyList<ProbeResult>> RunSuiteAsync(
        IReadOnlyList<ProbeRequest> requests,
        QualificationConsent consent,
        CancellationToken cancellationToken)
    {
        if (requests is null || requests.Count == 0)
        {
            throw new ArgumentException("la suite requiere al menos un probe", nameof(requests));
        }
        if (consent is null)
        {
            throw new ArgumentNullException(nameof(consent));
        }
        if (!consent.ExplicitlyGiven)
        {
            throw new QualificationConsentRequiredException(
                "la suite de cualificación requiere consentimiento explícito para ejecutarse");
        }

        decimal estimated = 0m;
        try
        {
            foreach (var r in requests) estimated = checked(estimated + r.Probe.MaxCostUsd);
        }
        catch (OverflowException) { throw new QualificationCostEstimateUnavailableException(); }
        if (estimated > consent.MaxTotalCostUsd)
        {
            throw new QualificationCostCapExceededException(consent.MaxTotalCostUsd, estimated);
        }

        var results = new List<ProbeResult>(requests.Count);
        foreach (var r in requests)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                results.Add(new ProbeResult(r.Probe.Id, ProbeStatus.NotRun, 0.0, null,
                    "cancelado antes de ejecutar el probe", TimeSpan.Zero, null));
                continue;
            }
            results.Add(await RunProbeAsync(r, cancellationToken));
        }
        return results;
    }

    /// <summary>Ejecuta un probe individual y lo puntúa por regla exacta.</summary>
    public async Task<ProbeResult> RunProbeAsync(ProbeRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(PerProbeTimeout);

        ModelResponse? response = null;
        string? failureMessage = null;
        try
        {
            await foreach (var evt in _provider.StreamAsync(ToModelRequest(request), timeoutCts.Token))
            {
                if (evt is ResponseCompleted completed)
                {
                    response = completed.Response;
                }
                else if (evt is ResponseFailed failed)
                {
                    failureMessage = $"{failed.ErrorType}: {failed.Message}";
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            throw;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            throw new ProbeTimeoutException(request.Probe.Id.ToString());
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ProbeResult(request.Probe.Id, ProbeStatus.Error, 0.0, null, ex.Message, sw.Elapsed,
                Cost(response), response?.Usage, response?.ReportedUsageFields ?? TokenUsageFields.None);
        }
        sw.Stop();

        if (failureMessage is not null || response is null)
        {
            return new ProbeResult(request.Probe.Id, ProbeStatus.Error, 0.0, null,
                failureMessage ?? "el provider no devolvió una respuesta completa", sw.Elapsed,
                Cost(response), response?.Usage, response?.ReportedUsageFields ?? TokenUsageFields.None);
        }

        if (TokenUsageValidation.IsInvalid(response.Usage, response.ReportedUsageFields))
            return new ProbeResult(request.Probe.Id, ProbeStatus.Error, 0.0, null,
                "provider reported inconsistent token usage", sw.Elapsed, null,
                response.Usage, response.ReportedUsageFields);

        if (response.StopReason is not (StopReason.EndTurn or StopReason.StopSequence or StopReason.MaxOutputTokens))
            return new ProbeResult(request.Probe.Id, ProbeStatus.Error, 0.0, null,
                "probe did not complete with a scorable terminal response: " + response.StopReason,
                sw.Elapsed, Cost(response), response.Usage, response.ReportedUsageFields);

        var text = ProbeScorer.ExtractText(response);
        var score = ProbeScorer.Score(request.Probe.Kind, text, request.Probe.Expected);
        var passed = score >= 1.0;
        return new ProbeResult(request.Probe.Id, passed ? ProbeStatus.Passed : ProbeStatus.Failed,
            score, text, null, sw.Elapsed, Cost(response), response.Usage, response.ReportedUsageFields);
    }

    private decimal? Cost(ModelResponse? response)
    {
        var required = TokenUsageFields.Input | TokenUsageFields.Output;
        if (response is null || _quoteCost is null
            || (response.ReportedUsageFields & required) != required
            || TokenUsageValidation.IsInvalid(response.Usage, response.ReportedUsageFields)) return null;
        try
        {
            var cost = _quoteCost(response.Usage);
            return cost is >= 0m ? cost : null;
        }
        catch (OverflowException) { return null; }
    }

    private static ModelRequest ToModelRequest(ProbeRequest request)
    {
        var messages = new List<ModelMessage>
        {
            new(MessageRole.User, new List<ContentBlock> { new TextBlock(request.Probe.Prompt) }),
        };
        return new ModelRequest(
            request.Selection,
            messages,
            instructions: null,
            tools: Array.Empty<ToolDefinition>(),
            ToolChoice.None(),
            output: null,
            reasoning: null,
            cache: null,
            continuation: null);
    }
}
