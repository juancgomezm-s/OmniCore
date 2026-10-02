namespace OmniCore.Qualification;

using System.Diagnostics;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Runner determinista de probes de cualificación (ADR-0007 §6, M5). Consume el runtime como un
/// cliente a través de IModelProvider (INV-011); nunca ejecuta la suite automáticamente al
/// descubrir un modelo. La suite completa es 10–20 probes; esta es la base mínima `quick`.
/// El costo se gobierna con <see cref="QualificationCostPolicy"/>: estimación conservadora ANTES
/// de invocar, tope de salida explícito en cada request, costo real medido con la evidencia de uso
/// y fail-closed ante pricing desconocido (ADR-0007 §7).
/// </summary>
public sealed class ProbeRunner
{
    private readonly IModelProvider _provider;

    private readonly QualificationCostPolicy _costPolicy;

    public TimeSpan PerProbeTimeout { get; }

    public static readonly TimeSpan DefaultPerProbeTimeout = TimeSpan.FromSeconds(60);

    public ProbeRunner(IModelProvider provider) : this(provider, DefaultPerProbeTimeout, null) { }

    public ProbeRunner(IModelProvider provider, TimeSpan perProbeTimeout)
        : this(provider, perProbeTimeout, null) { }

    public ProbeRunner(IModelProvider provider, QualificationCostPolicy costPolicy)
        : this(provider, DefaultPerProbeTimeout, costPolicy) { }

    public ProbeRunner(IModelProvider provider, TimeSpan perProbeTimeout, QualificationCostPolicy? costPolicy = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        if (perProbeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(perProbeTimeout), "PerProbeTimeout debe ser positivo");
        }
        PerProbeTimeout = perProbeTimeout;
        _costPolicy = costPolicy ?? QualificationCostPolicy.LocalZeroCost();
    }

    /// <summary>
    /// Ejecuta un conjunto de probes con consentimiento explícito y tope de costo total. Falla en
    /// cerrado (sin emitir requests) si el pricing es desconocido o si la estimación conservadora
    /// excede el tope. Tras un Error/Timeout detiene la suite y marca el resto como NotRun: no se
    /// gasta más después de una falla.
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

        if (_costPolicy.Kind == QualificationCostKind.UnknownPaidPricing)
        {
            throw new QualificationUnknownPricingException();
        }

        var estimated = _costPolicy.EstimateSuiteMaxCostUsd(requests.Select(r => r.Probe)) ?? 0m;
        if (estimated > consent.MaxTotalCostUsd)
        {
            throw new QualificationCostCapExceededException(consent.MaxTotalCostUsd, estimated);
        }

        var results = new List<ProbeResult>(requests.Count);
        decimal spent = 0m;
        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            if (cancellationToken.IsCancellationRequested)
            {
                results.Add(new ProbeResult(request.Probe.Id, ProbeStatus.NotRun, 0.0, null,
                    "cancelado antes de ejecutar el probe", TimeSpan.Zero, null));
                continue;
            }

            var (result, _) = await RunProbeCoreAsync(request, cancellationToken).ConfigureAwait(false);
            results.Add(result);

            if (result.CostUsd is { } actual)
            {
                spent += actual;
                if (spent > consent.MaxTotalCostUsd)
                {
                    throw new QualificationCostCapExceededException(consent.MaxTotalCostUsd, spent);
                }
            }

            if (result.Status is ProbeStatus.Error or ProbeStatus.Timeout)
            {
                for (var j = i + 1; j < requests.Count; j++)
                {
                    results.Add(new ProbeResult(requests[j].Probe.Id, ProbeStatus.NotRun, 0.0, null,
                        "no ejecutado: un probe anterior no se completó", TimeSpan.Zero, null));
                }
                break;
            }
        }
        return results;
    }

    /// <summary>
    /// Ejecuta un probe individual y lo puntúa por regla exacta. Un timeout lanza
    /// <see cref="ProbeTimeoutException"/>: nunca produce un resultado exitoso falso.
    /// </summary>
    public async Task<ProbeResult> RunProbeAsync(ProbeRequest request, CancellationToken cancellationToken)
    {
        var (result, timedOut) = await RunProbeCoreAsync(request, cancellationToken).ConfigureAwait(false);
        if (timedOut)
        {
            throw new ProbeTimeoutException(request.Probe.Id.ToString());
        }
        return result;
    }

    private async Task<(ProbeResult Result, bool TimedOut)> RunProbeCoreAsync(ProbeRequest request,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(PerProbeTimeout);

        ModelResponse? response = null;
        string? failureMessage = null;
        // Uso observado por la evidencia del provider (UsageUpdated o la respuesta final). Se
        // conserva aunque el probe falle/timeout para que el tope agregado lo contabilice.
        TokenUsage? observedUsage = null;
        try
        {
            await foreach (var evt in _provider.StreamAsync(
                ToModelRequest(request, _costPolicy.MaxOutputTokens), timeoutCts.Token))
            {
                if (evt is UsageUpdated updated)
                {
                    observedUsage = updated.Usage;
                }
                else if (evt is ResponseCompleted completed)
                {
                    response = completed.Response;
                    observedUsage = completed.Response.Usage;
                }
                else if (evt is ResponseFailed failed)
                {
                    failureMessage = $"{failed.ErrorType}: {failed.Message}";
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
            return (new ProbeResult(request.Probe.Id, ProbeStatus.Timeout, 0.0, null,
                "el probe superó su tope de tiempo", sw.Elapsed, _costPolicy.ActualCostUsd(observedUsage)), true);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (new ProbeResult(request.Probe.Id, ProbeStatus.Error, 0.0, null, ex.Message,
                sw.Elapsed, _costPolicy.ActualCostUsd(observedUsage)), false);
        }
        sw.Stop();

        if (response is null)
        {
            return (new ProbeResult(request.Probe.Id, ProbeStatus.Error, 0.0, null,
                failureMessage ?? "el provider no devolvió una respuesta completa", sw.Elapsed,
                _costPolicy.ActualCostUsd(observedUsage)), false);
        }

        var actualCost = _costPolicy.ActualCostUsd(response.Usage);
        if (_costPolicy.Kind == QualificationCostKind.DeclaredPricing && actualCost is null)
        {
            // Fail-closed: un provider pago que no reporta uso completo no puede contabilizarse.
            // El costo queda null (desconocido), nunca 0, y la suite no se persiste.
            return (new ProbeResult(request.Probe.Id, ProbeStatus.Error, 0.0, null,
                "uso incompleto: el provider no reportó tokens de entrada/salida; no se puede calcular el costo",
                sw.Elapsed, null), false);
        }

        var text = ProbeScorer.ExtractText(response);
        var score = ProbeScorer.Score(request.Probe.Kind, text, request.Probe.Expected);
        var passed = score >= 1.0;
        return (new ProbeResult(request.Probe.Id, passed ? ProbeStatus.Passed : ProbeStatus.Failed,
            score, text, null, sw.Elapsed, actualCost), false);
    }

    private static ModelRequest ToModelRequest(ProbeRequest request, long maxOutputTokens)
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
            continuation: null,
            maxOutputTokens: maxOutputTokens);
    }
}
