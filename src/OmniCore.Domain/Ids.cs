namespace OmniCore.Domain;

/// <summary>
/// Identificadores fuertemente tipados (spec §80, arquitectura §22). Todos son UUIDv7 salvo
/// los que tienen significado físico (ContentHash, WorkspaceId derivado de la ruta).
/// </summary>
public sealed class DomainIds
{
    private DomainIds() { }
}

/// <summary>Identifica un evento canónico persistido (ADR-0001 §3).</summary>
public record EventId(Guid Value)
{
    public static EventId New() => new(Guid.CreateVersion7());

    public static EventId Parse(string text) => new(ParseGuidText(text));

    /// <summary>Parsea un texto canónico a Guid UUIDv7 con validación de formato.</summary>
    public static Guid ParseGuidText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Guid.Parse(text.Trim());
    }

    public override string ToString() => Value.ToString();
}

/// <summary>Identifica un comando del protocolo (ADR-0013 §3).</summary>
public record CommandId(Guid Value)
{
    public static CommandId New() => new(Guid.CreateVersion7());

    public static CommandId Parse(string text) => new(EventId.ParseGuidText(text));

    public override string ToString() => Value.ToString();
}

/// <summary>Identifica una interacción humano-en-el-bucle (ADR-0034).</summary>
public record InteractionId(Guid Value)
{
    public static InteractionId New() => new(Guid.CreateVersion7());

    public static InteractionId Parse(string text) => new(EventId.ParseGuidText(text));

    public override string ToString() => Value.ToString();
}

/// <summary>Identifica una sesión durable (spec §6).</summary>
public record SessionId(Guid Value)
{
    public static SessionId New() => new(Guid.CreateVersion7());

    public static SessionId Parse(string text) => new(EventId.ParseGuidText(text));

    public override string ToString() => Value.ToString();
}

/// <summary>Identifica un Run (spec §7).</summary>
public record RunId(Guid Value)
{
    public static RunId New() => new(Guid.CreateVersion7());

    public static RunId Parse(string text) => new(EventId.ParseGuidText(text));

    public override string ToString() => Value.ToString();
}

/// <summary>Identifica un Plan canónico de un Run (ADR-0016).</summary>
public record PlanId(Guid Value)
{
    public static PlanId New() => new(Guid.CreateVersion7());

    public static PlanId Parse(string text) => new(EventId.ParseGuidText(text));

    public override string ToString() => Value.ToString();
}

/// <summary>Identifica un PlanItem dentro de un Plan (ADR-0016, ADR-0036 §4).</summary>
public record PlanItemId(Guid Value)
{
    public static PlanItemId New() => new(Guid.CreateVersion7());

    public static PlanItemId Parse(string text) => new(EventId.ParseGuidText(text));

    public override string ToString() => Value.ToString();
}

/// <summary>Identifica una Task del TaskGraph (spec §8–§9).</summary>
public record TaskId(Guid Value)
{
    public static TaskId New() => new(Guid.CreateVersion7());

    public static TaskId Parse(string text) => new(EventId.ParseGuidText(text));

    public override string ToString() => Value.ToString();
}

/// <summary>Identifica una Lane, ejecución concreta de una Task (spec §10).</summary>
public record LaneId(Guid Value)
{
    public static LaneId New() => new(Guid.CreateVersion7());

    public static LaneId Parse(string text) => new(EventId.ParseGuidText(text));

    public override string ToString() => Value.ToString();
}

/// <summary>Identifica un Turn de una Lane (spec §12).</summary>
public record TurnId(Guid Value)
{
    public static TurnId New() => new(Guid.CreateVersion7());

    public static TurnId Parse(string text) => new(EventId.ParseGuidText(text));

    public override string ToString() => Value.ToString();
}

/// <summary>Identifica una ToolCall durable del Effect Journal (ADR-0004).</summary>
public record ToolCallId(Guid Value)
{
    public static ToolCallId New() => new(Guid.CreateVersion7());

    public static ToolCallId Parse(string text) => new(EventId.ParseGuidText(text));

    public override string ToString() => Value.ToString();
}

/// <summary>Identifica un grant de permisos persistido (ADR-0037).</summary>
public record GrantId(Guid Value)
{
    public static GrantId New() => new(Guid.CreateVersion7());

    public static GrantId Parse(string text) => new(EventId.ParseGuidText(text));

    public override string ToString() => Value.ToString();
}

/// <summary>Identifica un artifact lógico content-addressed (ADR-0001 §4).</summary>
public record ArtifactId(Guid Value)
{
    public static ArtifactId New() => new(Guid.CreateVersion7());

    public static ArtifactId Parse(string text) => new(EventId.ParseGuidText(text));

    public override string ToString() => Value.ToString();
}

/// <summary>Identifica un profile de agente o de modelo (spec §15, ADR-0007).</summary>
public record ProfileId(Guid Value)
{
    public static ProfileId New() => new(Guid.CreateVersion7());

    public static ProfileId Parse(string text) => new(EventId.ParseGuidText(text));

    public override string ToString() => Value.ToString();
}

/// <summary>Identifica un tokenizador por su identidad estable (ADR-0042 §1).</summary>
public record TokenizerId(string Value)
{
    public static TokenizerId Parse(string text) => new(text);

    public override string ToString() => Value;
}

/// <summary>Evento o comando que originó una cadena causal (ADR-0013 §3).</summary>
public interface CausationId { }

/// <summary>Causación por evento canónico.</summary>
public record EventCausation(EventId EventId) : CausationId { }

/// <summary>Causación por comando del protocolo.</summary>
public record CommandCausation(CommandId CommandId) : CausationId { }