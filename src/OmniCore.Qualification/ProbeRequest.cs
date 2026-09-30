namespace OmniCore.Qualification;

using OmniCore.Domain;

/// <summary>
/// Solicitud de un probe individual: el probe definido + la selección de modelo exacta que se
/// va a medir. El runner la convierte en un ModelRequest para el IModelProvider.
/// </summary>
public sealed class ProbeRequest
{
    public Probe Probe { get; }

    public ModelSelection Selection { get; }

    public ProbeRequest(Probe probe, ModelSelection selection)
    {
        Probe = probe ?? throw new ArgumentNullException(nameof(probe));
        Selection = selection ?? throw new ArgumentNullException(nameof(selection));
    }
}
