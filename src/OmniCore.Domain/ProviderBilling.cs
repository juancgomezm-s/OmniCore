namespace OmniCore.Domain;

/// <summary>Declared billing semantics for a provider; absence is deliberately <see cref="Unknown"/>.</summary>
public enum BillingMode
{
    Unknown = 0,
    Local,
    IncludedQuota,
    CreditBalance,
    MeteredCurrency,
}
