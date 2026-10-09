namespace OmniCore.Domain;

/// <summary>
/// Marca a los payloads de eventos canónicos. Cada evento concreto declara su tipo y su versión
/// de schema durable (ADR-0013).
/// </summary>
public interface DomainEventPayload
{
    EventType Type();

    int SchemaVersion();
}

/// <summary>Payloads outside a frozen record family can still opt in to codec shape validation.</summary>
public interface IValidatedDomainEventPayload : DomainEventPayload
{
    void Validate();
}

/// <summary>Identidad del evento durable: string estable en minúsculas con puntos (ADR-0013 §3).</summary>
public sealed class EventType
{
    private readonly string _value;

    private EventType(string value) => _value = value;

    public static EventType Of(string stableName)
    {
        ArgumentNullException.ThrowIfNull(stableName);
        if (stableName.Length == 0 || stableName.Any(ch => ch == ' ' || ch == '\t'))
        {
            throw new FormatException("EventType inválido: " + stableName);
        }

        return new EventType(stableName);
    }

    public override string ToString() => _value;

    public string Value() => _value;

    public override bool Equals(object? other) =>
        other is EventType t && t._value.Equals(_value, StringComparison.Ordinal);

    public override int GetHashCode() => _value.GetHashCode();
}

/// <summary>Persistencia de un evento: envelope + payload ya serializado a JSON.</summary>
public record StoredEvent(DomainEvent Envelope, DomainEventPayload Payload) { }
