namespace OmniCore.Domain;

// Durable record identities only; they do not schedule work (ADR0046 §8.4).
public record DelegationId(Guid Value)
{
    public static DelegationId New() => new(Guid.CreateVersion7());
    public static DelegationId Parse(string text) => new(EventId.ParseGuidText(text));
    public override string ToString() => Value.ToString("D");
}

public record JoinId(Guid Value)
{
    public static JoinId New() => new(Guid.CreateVersion7());
    public static JoinId Parse(string text) => new(EventId.ParseGuidText(text));
    public override string ToString() => Value.ToString("D");
}

public record BindingId(Guid Value)
{
    public static BindingId New() => new(Guid.CreateVersion7());
    public static BindingId Parse(string text) => new(EventId.ParseGuidText(text));
    public override string ToString() => Value.ToString("D");
}

public record MailboxId(Guid Value)
{
    public static MailboxId New() => new(Guid.CreateVersion7());
    public static MailboxId Parse(string text) => new(EventId.ParseGuidText(text));
    public override string ToString() => Value.ToString("D");
}

public record MailboxMessageId(Guid Value)
{
    public static MailboxMessageId New() => new(Guid.CreateVersion7());
    public static MailboxMessageId Parse(string text) => new(EventId.ParseGuidText(text));
    public override string ToString() => Value.ToString("D");
}

public record WakeRequestId(Guid Value)
{
    public static WakeRequestId New() => new(Guid.CreateVersion7());
    public static WakeRequestId Parse(string text) => new(EventId.ParseGuidText(text));
    public override string ToString() => Value.ToString("D");
}

public record DispositionId(Guid Value)
{
    public static DispositionId New() => new(Guid.CreateVersion7());
    public static DispositionId Parse(string text) => new(EventId.ParseGuidText(text));
    public override string ToString() => Value.ToString("D");
}

public record ValidationDebtId(Guid Value)
{
    public static ValidationDebtId New() => new(Guid.CreateVersion7());
    public static ValidationDebtId Parse(string text) => new(EventId.ParseGuidText(text));
    public override string ToString() => Value.ToString("D");
}
