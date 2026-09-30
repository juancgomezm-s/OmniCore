namespace OmniCore.Abstractions;

/// <summary>Typed refusal when Strong was requested but Weak consent was not granted.</summary>
public sealed class WeakSandboxConsentRequiredException : Exception
{
    public const string Code = "WEAK_SANDBOX_CONSENT_REQUIRED";

    public WeakSandboxConsentRequiredException()
        : base(Code) { }
}
