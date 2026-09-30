namespace OmniCore.Abstractions;

/// <summary>Per-Run memory of explicit consent to the weak sandbox fallback.</summary>
public sealed class WeakSandboxConsentState
{
    public bool GrantedForRun { get; private set; }
    public void GrantForRun() => GrantedForRun = true;
}
